using System.Diagnostics;
using System.Text.Json;
using SmartSentinelEye.Integration.Tests.Fixtures;

namespace SmartSentinelEye.Integration.Tests.SystemVariables;

/// <summary>
/// Spec 100 T002/T003 (#494) — one label carrying two placeholders, both
/// resolved, read over <c>GET /system-variables/snapshot</c>.
///
/// <para>
/// <b>What this proves.</b> The multi-name snapshot loop in
/// <c>GetOverlaySnapshotQueryHandler.BuildSnapshotAsync</c>. That loop has four
/// <c>continue</c> exits before it writes an entry, and every one of its unit
/// tests binds a single placeholder except the archived/unset one, where
/// neither name reaches the write. So it has never been observed completing a
/// second iteration with a value in hand. This file is that observation, and it
/// takes it through the whole HTTP path: define, publish, index, set, read.
/// </para>
///
/// <para>
/// <b>What this does not prove.</b> The substitution mechanism.
/// <c>PlaceholderParser.Substitute</c> is a single <c>Regex.Replace</c> with a
/// stateless evaluator — no loop, no ordering — and two placeholders resolving
/// in one string is already asserted against the real resolver by
/// <c>VariableValueChangedPreCommitTests.A_sibling_variable_still_resolves_from_storage</c>.
/// That test covers the <i>push</i> path's own snapshot loop, which is a
/// different method from the one here.
/// </para>
///
/// <para>
/// <b>Which half carries which claim.</b> The happy path is the one the
/// counterfactual in <c>plan.md</c> (M1 — <c>break;</c> after the snapshot
/// write) turns red. The partial-resolution case stays green under M1 and
/// <b>cannot</b> detect it: M1's <c>break</c> sits after the write, which only
/// the valued name reaches, and that name is last. The control earns its place
/// on a different ground — a label mixing a resolved and an unset name over the
/// query path is covered nowhere else, at any level, and with the unset name
/// first it is the only case that shows a <c>continue</c> exit does not end the
/// loop.
/// </para>
///
/// <para>
/// <b>No <c>[Trait]</c>, deliberately.</b> The CI integration job filters
/// <c>Category!=Measurement&amp;Category!=Disruptive&amp;Category!=Maintenance</c>
/// and every file in this directory carries no trait at all. Adding one would
/// quietly remove this file from the only job that can run it.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public class TwoPlaceholdersInOneLabelTests(AspireFixture aspire) : IAsyncLifetime
{
    /// <summary>
    /// The reverse index is populated by an integration event, so an overlay is
    /// not resolvable the instant publish returns. 30 s is the ceiling
    /// <c>NFR_VariableResolutionLatencyTests</c> already uses for the same wait.
    /// </summary>
    private const int IndexReadinessCeilingMs = 30_000;

    private const int PollIntervalMs = 200;

    public Task InitializeAsync() => aspire.ResetSystemVariablesAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// US1 happy path. The assertion is the <b>whole</b> resolved string rather
    /// than two <c>Contains</c> checks: a pair of those would pass against a
    /// snapshot that dropped the separator, emitted the two values in the wrong
    /// order, or substituted one value into both placeholders.
    /// </summary>
    [Fact]
    public async Task Two_placeholders_in_one_label_both_resolve()
    {
        using HttpClient variables = await aspire.CreateAdminClientAsync("system-variables");
        using HttpClient overlays = await aspire.CreateAdminClientAsync("overlay-designer");

        string first = UniqueVariableName();
        string second = UniqueVariableName();
        await DefineAsync(variables, first);
        await DefineAsync(variables, second);

        Guid overlay = await PublishOverlayReferencingAsync(overlays, first, second);

        (await VariableRequests.SetValueAsync(variables, first, "82.5")).EnsureSuccessStatusCode();
        (await VariableRequests.SetValueAsync(variables, second, "91.5")).EnsureSuccessStatusCode();

        // The *first* name only, and that is the point rather than a shortcut.
        // There is no non-defect state in which the first placeholder resolves
        // and the second lags: the label is written into the index in one
        // atomic assignment (InMemoryReverseIndex:30), GetByNameAsync reads
        // live from the DbContext, and both values are committed above. So a
        // second name still literal *is* the defect — and requiring it here
        // would bury that defect in a 30 s readiness timeout instead of letting
        // the assertion below print the expected and actual strings.
        await WaitUntilResolvableAsync(variables, overlay, [first]);

        using HttpResponseMessage snapshot = await SnapshotAsync(variables, overlay);
        string body = await snapshot.Content.ReadAsStringAsync();

        snapshot.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        ResolvedTextIn(body).ShouldBe("Line A: 82.5 / Line B: 91.5");
    }

    /// <summary>
    /// US1 partial resolution: one label carrying an unset name and a resolved
    /// one, read over the query path. It is <b>not</b> the control the happy
    /// path needs against "one value written into every placeholder" — the
    /// happy path uses two distinct values and asserts the whole string, so
    /// such a resolver yields <c>"Line A: 82.5 / Line B: 82.5"</c> and fails
    /// there directly.
    ///
    /// <para>
    /// It earns its place because mixed resolve/unset in one label over
    /// <b>this</b> handler is covered nowhere else. The only two-name unit test
    /// of the loop, <c>GetOverlaySnapshotQueryHandlerTests.Skips_archived_and_unset_variables…</c>
    /// (<c>:54-73</c>), has neither name reach the write.
    /// </para>
    ///
    /// <para>
    /// The unset name is deliberately <b>first</b>. The valued one is then only
    /// reached on a second iteration, which makes this the one case that shows
    /// a <c>continue</c> exit does not end the loop. It still cannot detect M1,
    /// whose <c>break</c> sits after the write that only the last name reaches.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_unset_first_variable_leaves_only_its_own_placeholder_literal()
    {
        using HttpClient variables = await aspire.CreateAdminClientAsync("system-variables");
        using HttpClient overlays = await aspire.CreateAdminClientAsync("overlay-designer");

        string unset = UniqueVariableName();
        string valued = UniqueVariableName();
        await DefineAsync(variables, unset);
        await DefineAsync(variables, valued);

        Guid overlay = await PublishOverlayReferencingAsync(overlays, unset, valued);

        (await VariableRequests.SetValueAsync(variables, valued, "82.5")).EnsureSuccessStatusCode();

        // Only the valued name can ever leave the text; the unset one is the
        // expected output, so it is not part of the readiness signal.
        await WaitUntilResolvableAsync(variables, overlay, [valued]);

        using HttpResponseMessage snapshot = await SnapshotAsync(variables, overlay);
        string body = await snapshot.Content.ReadAsStringAsync();

        snapshot.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        ResolvedTextIn(body).ShouldBe($"Line A: {{{{{unset}}}}} / Line B: 82.5");
    }

    /// <summary>
    /// Defines a Number variable with <b>no</b> initial value, so it starts
    /// <c>Unset</c> and its placeholder renders literal until something sets it.
    ///
    /// <para>
    /// Omitting <c>initialValue</c> is the whole point. The neighbours pass
    /// <c>"0"</c>, which is a *set* variable holding zero — a variable defined
    /// that way resolves to <c>0</c> rather than staying literal, and the
    /// unset-placeholder case below silently proves nothing against it.
    /// </para>
    /// </summary>
    private static async Task DefineAsync(HttpClient variables, string name)
    {
        (await variables.PostAsJsonAsync("/system-variables", new
        {
            name,
            type = "Number",
            initialValue = (string?)null,
            truthyLabel = (string?)null,
            falsyLabel = (string?)null,
        })).EnsureSuccessStatusCode();
    }

    private static async Task<Guid> PublishOverlayReferencingAsync(
        HttpClient overlays, string first, string second)
    {
        HttpResponseMessage created = await overlays.PostAsJsonAsync("/overlays", new
        {
            name = $"Two-{Guid.NewGuid():N}"[..16],
            label = new
            {
                text = $"Line A: {{{{{first}}}}} / Line B: {{{{{second}}}}}",
                normalizedX = 0.5m,
                normalizedY = 0.05m,
                normalizedWidth = 0.3m,
                normalizedHeight = 0.08m,
                fontSizePx = 48,
            },
        });
        created.EnsureSuccessStatusCode();

        Guid overlay = await created.Content.ReadFromJsonAsync<Guid>();
        (await OverlayRequests.PostAsync(overlays, overlay, "revisions/1/publish")).EnsureSuccessStatusCode();

        return overlay;
    }

    /// <summary>
    /// Polls until the snapshot answers 200 <b>and</b> none of
    /// <paramref name="names"/> still renders as its literal placeholder. The
    /// timeout names every variable and the overlay, and quotes the last text
    /// seen, because an unbooted index and a snapshot loop that stopped early
    /// otherwise look identical from here.
    ///
    /// <para>
    /// The 200 is half the condition, not a formality. Until the reverse index
    /// picks the overlay up the endpoint answers 404, and a failed snapshot
    /// mapped to an empty string would satisfy "no literal remains" trivially —
    /// so a wait that ignored the status code would return before the index had
    /// done anything, which is exactly how the first run of this file failed.
    /// </para>
    /// </summary>
    private static async Task WaitUntilResolvableAsync(
        HttpClient variables, Guid overlay, IReadOnlyList<string> names)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        string? resolved = null;

        while (stopwatch.ElapsedMilliseconds < IndexReadinessCeilingMs)
        {
            resolved = await ResolvedTextAsync(variables, overlay);
            if (resolved is not null &&
                names.All(name => !resolved.Contains($"{{{{{name}}}}}", StringComparison.Ordinal)))
            {
                return;
            }

            await Task.Delay(PollIntervalMs);
        }

        throw new TimeoutException(
            $"Overlay {overlay} never resolved all of [{string.Join(", ", names)}] within "
            + $"{IndexReadinessCeilingMs} ms; the last snapshot was "
            + $"{(resolved is null ? "not a 200" : $"'{resolved}'")}. Either the reverse index never "
            + "picked the overlay up, or the snapshot loop never reached one of them.");
    }

    /// <summary>
    /// The resolved text, or <c>null</c> when the snapshot did not answer 200 —
    /// the two are different states and the readiness wait must tell them apart.
    /// </summary>
    private static async Task<string?> ResolvedTextAsync(HttpClient variables, Guid overlay)
    {
        using HttpResponseMessage snapshot = await SnapshotAsync(variables, overlay);
        if (!snapshot.IsSuccessStatusCode)
        {
            return null;
        }

        return ResolvedTextIn(await snapshot.Content.ReadAsStringAsync());
    }

    private static Task<HttpResponseMessage> SnapshotAsync(HttpClient variables, Guid overlay) =>
        variables.GetAsync($"/system-variables/snapshot?overlayIdentifier={overlay}");

    private static string ResolvedTextIn(string body)
    {
        using JsonDocument payload = JsonDocument.Parse(body);

        return payload.RootElement.GetProperty("resolvedText").GetString() ?? string.Empty;
    }

    private static string UniqueVariableName() => $"v{Guid.NewGuid():N}"[..12];
}
