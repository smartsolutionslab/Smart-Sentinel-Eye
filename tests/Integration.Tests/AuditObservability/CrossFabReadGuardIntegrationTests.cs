using System.Net.Http.Json;
using System.Text.Json;
using SmartSentinelEye.AuditObservability.Domain.AuditEvent;
using SmartSentinelEye.AuditObservability.Infrastructure.Persistence;
using SmartSentinelEye.Integration.Tests.Fixtures;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.Integration.Tests.AuditObservability;

/// <summary>
/// Spec 009 US2 (T056): the per-resource timeline endpoint runs the shared
/// fab guard (spec 008 FR-019). A single-fab operator can read its own fab's
/// timeline but is refused another fab's with <c>403
/// RESOURCE_FAB_NOT_AUTHORIZED</c>; the cross-cutting search (no fabId) is
/// scoped to the caller's fab membership.
/// </summary>
[Collection(AspireCollection.Name)]
public class CrossFabReadGuardIntegrationTests(AspireFixture aspire)
{
    [Fact]
    public async Task Munich_member_reads_its_own_fab_timeline_but_is_refused_another_fab()
    {
        // admin@munich.test is a member of /fabs/munich only.
        using HttpClient client = await aspire.CreateAuthenticatedClientAsync(
            "audit-observability", "admin@munich.test", "Admin1234");
        Guid overlayId = Guid.CreateVersion7();

        HttpResponseMessage ownFab = await client.GetAsync($"/audit/overlay/{overlayId}?fabId=munich");
        ownFab.StatusCode.ShouldBe(HttpStatusCode.OK);

        HttpResponseMessage otherFab = await client.GetAsync($"/audit/overlay/{overlayId}?fabId=berlin");
        otherFab.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        JsonElement problem = await otherFab.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("title").GetString().ShouldBe("RESOURCE_FAB_NOT_AUTHORIZED");
    }

    /// <summary>
    /// The invariant is "never a fab the caller does not hold" — not "always
    /// the caller's own fab". A cross-fab row carries no fab and so belongs to
    /// nobody's fab; excluding it hid that whole class of history from every
    /// operator, since every operator belongs to a fab (#1300).
    ///
    /// <para>
    /// The class is smaller than the enumeration this comment used to carry.
    /// Camera, layout and variable events all stamp the fab now (#2068). What
    /// legitimately publishes without one is overlay events, whose domain
    /// events carry no fab at all (ADR-0115), and retention, which spans fabs.
    /// A stream-health event's fab is nullable and may still arrive null
    /// (#2076).
    /// </para>
    ///
    /// <para>
    /// Seeds both a foreign row and a cross-fab row rather than asserting over
    /// whatever the run happens to have produced: the loop this replaced would
    /// have passed on an empty page.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Search_without_a_fab_filter_returns_the_callers_fabs_and_cross_fab_rows()
    {
        await SeedAsync(Row("berlin"), Row(fab: null));

        using HttpClient client = await aspire.CreateAuthenticatedClientAsync(
            "audit-observability", "admin@munich.test", "Admin1234");

        HttpResponseMessage response = await client.GetAsync("/audit?pageSize=200");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonElement page = await response.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement[] rows = [.. page.GetProperty("rows").EnumerateArray()];

        string?[] fabs = [.. rows.Select(row => row.GetProperty("fab").GetString())];
        fabs.ShouldNotContain("berlin");
        fabs.ShouldContain((string?)null, "a cross-fab row must be readable by a fab-assigned caller");
    }

    /// <summary>
    /// Spec 082 US1 (#2068). The filed symptom, on the read surface: a variable
    /// archived in munich must not be visible to an operator whose only
    /// membership is berlin.
    ///
    /// <para>
    /// Drives the real publish path rather than seeding a row, because that is
    /// where the defect lives. A seeded row carries whatever fab the test wrote;
    /// only an archive performed through the API shows what
    /// <c>VariableArchivedDomainEventHandler</c> actually stamps. Both producer
    /// tests are silent about this: they assert the event, not who can read the
    /// row it becomes.
    /// </para>
    ///
    /// <para>
    /// Asserts the #1300 behaviour in the same breath, so the fix is shown not
    /// to have bought fab scoping by re-excluding rows that genuinely have no
    /// fab.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_variable_archived_in_munich_is_not_returned_to_a_berlin_only_operator()
    {
        string name = $"fab{Guid.NewGuid():N}"[..16];
        await ArchiveInMunichAsync(name);
        await SeedAsync(Row(fab: null));

        using HttpClient munich = await aspire.CreateAuthenticatedClientAsync(
            "audit-observability", "admin@munich.test", "Admin1234");
        JsonElement archived = await PollForArchiveRowAsync(munich, name);

        using HttpClient berlin = await aspire.CreateAuthenticatedClientAsync(
            "audit-observability", "op-berlin@berlin.test", "Operator1234");
        JsonElement[] visible = await ArchiveRowsAsync(berlin);

        string[] leaked = [.. visible
            .Select(row => row.GetProperty("payload").GetString()!)
            .Where(payload => payload.Contains(name, StringComparison.Ordinal))];
        leaked.ShouldBeEmpty(
            $"a variable archived in munich must not reach a berlin-only operator (archived '{name}')");

        // The publisher is the only place that knows, so say what it recorded.
        archived.GetProperty("fab").GetString().ShouldBe("munich");

        // #1300, unchanged: a row that genuinely has no fab still reaches
        // everyone. Scoping must not be bought by excluding it again.
        HttpResponseMessage unfiltered = await berlin.GetAsync("/audit?pageSize=200");
        unfiltered.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement page = await unfiltered.Content.ReadFromJsonAsync<JsonElement>();
        page.GetProperty("rows").EnumerateArray()
            .Select(row => row.GetProperty("fab").GetString())
            .ShouldContain((string?)null, "a cross-fab row must stay readable by a fab-assigned caller");
    }

    private async Task ArchiveInMunichAsync(string name)
    {
        using HttpClient variables = await aspire.CreateAuthenticatedClientAsync(
            "system-variables", "admin@munich.test", "Admin1234");

        HttpResponseMessage defined = await variables.PostAsJsonAsync(
            "/system-variables",
            new { name, type = "Number", initialValue = "1" });
        defined.StatusCode.ShouldBe(HttpStatusCode.Created, await defined.Content.ReadAsStringAsync());

        HttpResponseMessage archived = await VariableRequests.ArchiveAsync(variables, name);
        archived.IsSuccessStatusCode.ShouldBeTrue(await archived.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement[]> ArchiveRowsAsync(HttpClient audit)
    {
        HttpResponseMessage response = await audit.GetAsync(
            "/audit?eventKind=SystemVariableArchivedV1&pageSize=200");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonElement page = await response.Content.ReadFromJsonAsync<JsonElement>();
        return [.. page.GetProperty("rows").EnumerateArray()];
    }

    /// <summary>
    /// The archived row as its own fab's operator sees it. Its absence
    /// elsewhere means nothing until it is known to exist.
    /// </summary>
    private static async Task<JsonElement> PollForArchiveRowAsync(HttpClient audit, string name)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            JsonElement[] rows = await ArchiveRowsAsync(audit);
            foreach (JsonElement row in rows)
            {
                if (row.GetProperty("payload").GetString()!.Contains(name, StringComparison.Ordinal))
                {
                    return row;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        throw new Xunit.Sdk.XunitException(
            $"No SystemVariableArchivedV1 audit row for '{name}' appeared within 20s.");
    }

    private static AuditEvent Row(string? fab) =>
        AuditEvent.From(
            new V1Envelope(
                EventTypeName: "CameraRegisteredV1",
                OccurredAt: DateTimeOffset.UtcNow,
                Fab: fab is null
                    ? Option<FabIdentifier>.None
                    : Option<FabIdentifier>.Some(FabIdentifier.From(fab)),
                Actor: ActorIdentifier.System,
                ActorUsername: Option<string>.None,
                EventIdentifier: EventIdentifier.From(Guid.CreateVersion7()),
                Payload: """{"seeded":"cross-fab-read-guard"}"""),
            V1Mapping.Unmapped,
            new SystemClock());

    private async Task SeedAsync(params AuditEvent[] events)
    {
        await using AuditObservabilityDbContext context =
            await aspire.CreateAuditObservabilityDbContextAsync();

        context.AuditEvents.AddRange(events);
        await context.SaveChangesAsync();
    }
}
