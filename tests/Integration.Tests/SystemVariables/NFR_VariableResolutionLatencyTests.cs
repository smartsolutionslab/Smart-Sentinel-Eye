using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using SmartSentinelEye.Integration.Tests.Fixtures;

namespace SmartSentinelEye.Integration.Tests.SystemVariables;

/// <summary>
/// Spec 014 T031 — a baseline for the leg the reverse-index rewrite touches.
/// Measures <b>SystemVariables'</b> own part of it: a value change returning →
/// the overlay's resolved text carrying it.
///
/// <para>
/// <b>It does not close #749, and this file said it did until spec 106.</b> #749
/// is spec 007's T099 — an <i>Automation</i> measurement, over
/// <c>FabEventIngestedV1</c> consumed → action V1 published. Different context,
/// different span; nothing here enters <c>FabEventIngestedV1Handler</c>. Closing
/// #749 against this figure would have made the claim actively misleading. The
/// Automation part is measured by
/// <c>Automation/AcceptToDecideLatencyTests</c> — and even that is a proxy for
/// NFR-001's own span, so read its remarks before quoting either figure. No
/// assertion here changed: the measurement was always this one, only its label
/// was wrong.
/// </para>
///
/// <para>
/// Constitution §IV gives <c>event → overlay state</c> 200 ms of an 800 ms
/// budget. It is the product's load-bearing NFR and, before this, nothing
/// watched it: no test measured the leg at all, so a regression could only be
/// noticed on a kiosk.
/// </para>
///
/// <para>
/// <b>This must be taken against the global-keyed implementation, before T033
/// changes the key.</b> Measured afterwards it would compare the new code
/// against itself and pass trivially, which is exactly the failure mode the
/// phase gate exists to prevent. T039 re-runs it against the fab-keyed
/// implementation and records both figures.
/// </para>
///
/// <para>
/// What is measured: <c>PUT /system-variables/{name}/value</c> returning, then
/// polling <c>GET /system-variables/snapshot</c> until the resolved text
/// carries the new value. That spans the value write, the domain event, the
/// reverse-index lookup and the resolve — the whole of the leg that lives in
/// this context. It excludes the SignalR hop to the kiosk, which
/// <c>ResolvedTextReachesItsFabTests</c> covers separately.
/// </para>
///
/// <para>
/// That sentence used to name <c>OverlayPushIntegrationTests</c>, which covers
/// a different frame, from a different context, on a different trigger. The
/// resolved-text hop was covered by nothing at all, and a cross-reference that
/// made a gap look closed is part of why #2012 survived (spec 063 FR-008).
/// </para>
///
/// <para>
/// The assertion is deliberately loose. This runs on shared CI against a cold
/// stack, so a tight bound would flake and get deleted; the figure recorded in
/// the output is the artefact that matters, and <see cref="LegBudgetMs"/> is
/// there to catch an order-of-magnitude regression rather than to police the
/// budget.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public class NFR_VariableResolutionLatencyTests(AspireFixture aspire) : IAsyncLifetime
{
    /// <summary>
    /// Constitution §IV leg 4 is 200 ms. Asserted at 4x to survive CI jitter
    /// and a cold JIT — see the class remarks on why this is not the budget.
    /// </summary>
    private const int LegBudgetMs = 800;

    private const int WarmupRounds = 3;
    private const int MeasuredRounds = 5;

    public Task InitializeAsync() => aspire.ResetSystemVariablesAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Value_change_reaches_the_resolved_overlay_text_within_the_leg_budget()
    {
        using HttpClient variables = await aspire.CreateAdminClientAsync("system-variables");
        using HttpClient overlays = await aspire.CreateAdminClientAsync("overlay-designer");

        string variableName = UniqueVariableName();
        (await variables.PostAsJsonAsync("/system-variables", new
        {
            name = variableName,
            type = "Number",
            initialValue = "0",
            truthyLabel = (string?)null,
            falsyLabel = (string?)null,
        })).EnsureSuccessStatusCode();

        Guid overlay = await PublishOverlayReferencingAsync(overlays, variableName);

        // The index is populated by an integration event, so the overlay is not
        // resolvable the instant publish returns. Wait for it before timing
        // anything, or the first measurement is really a measurement of
        // Wolverine's delivery of a different event.
        await WaitUntilResolvableAsync(variables, overlay, variableName);

        for (int round = 0; round < WarmupRounds; round++)
        {
            await MeasureOneChangeAsync(variables, overlay, variableName, 1000 + round);
        }

        List<long> measured = [];
        for (int round = 0; round < MeasuredRounds; round++)
        {
            measured.Add(await MeasureOneChangeAsync(variables, overlay, variableName, 2000 + round));
        }

        measured.Sort();
        long median = measured[measured.Count / 2];
        long worst = measured[^1];

        // The artefact. Recorded in the test output so the figure survives the
        // run and T039 has something to compare against.
        Console.WriteLine(
            $"[NFR spec 014 T031] SystemVariables: value-change -> resolved overlay text, "
            + $"GLOBAL-KEYED baseline: "
            + $"median {median} ms, worst {worst} ms, samples [{string.Join(", ", measured)}] ms "
            + $"(constitution §IV leg 4 budget: 200 ms)");

        median.ShouldBeLessThan(LegBudgetMs);
    }

    /// <summary>
    /// One value change, timed from the write returning to the snapshot
    /// carrying it. Polls tightly rather than sleeping: a fixed delay would
    /// quantise every sample to the delay and measure the test, not the system.
    /// </summary>
    private static async Task<long> MeasureOneChangeAsync(
        HttpClient variables, Guid overlay, string variableName, int value)
    {
        HttpResponseMessage written = await VariableRequests.SetValueAsync(
            variables, variableName, value.ToString(CultureInfo.InvariantCulture));
        written.EnsureSuccessStatusCode();

        Stopwatch stopwatch = Stopwatch.StartNew();
        string expected = value.ToString(CultureInfo.InvariantCulture);

        while (stopwatch.ElapsedMilliseconds < 10_000)
        {
            if ((await ResolvedTextAsync(variables, overlay)).Contains(expected, StringComparison.Ordinal))
            {
                return stopwatch.ElapsedMilliseconds;
            }
        }

        throw new TimeoutException(
            $"Resolved text never carried '{expected}' for overlay {overlay} within 10 s.");
    }

    private static async Task WaitUntilResolvableAsync(
        HttpClient variables, Guid overlay, string variableName)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < 30_000)
        {
            string resolved = await ResolvedTextAsync(variables, overlay);

            // Until the index knows the overlay, the snapshot renders the
            // literal placeholder. Its disappearance is the readiness signal.
            if (!resolved.Contains($"{{{{{variableName}}}}}", StringComparison.Ordinal))
            {
                return;
            }
        }

        throw new TimeoutException(
            $"Overlay {overlay} never became resolvable; the reverse index did not pick it up.");
    }

    private static async Task<string> ResolvedTextAsync(HttpClient variables, Guid overlay)
    {
        HttpResponseMessage snapshot = await variables.GetAsync(
            $"/system-variables/snapshot?overlayIdentifier={overlay}");
        if (!snapshot.IsSuccessStatusCode)
        {
            return string.Empty;
        }

        JsonElement payload = await snapshot.Content.ReadFromJsonAsync<JsonElement>();

        return payload.GetProperty("resolvedText").GetString() ?? string.Empty;
    }

    private static async Task<Guid> PublishOverlayReferencingAsync(HttpClient overlays, string variableName)
    {
        HttpResponseMessage created = await overlays.PostAsJsonAsync("/overlays", new
        {
            name = $"Nfr-{Guid.NewGuid():N}"[..16],
            label = new
            {
                text = $"Line 1: {{{{{variableName}}}}}",
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

    private static string UniqueVariableName() => $"v{Guid.NewGuid():N}"[..12];
}
