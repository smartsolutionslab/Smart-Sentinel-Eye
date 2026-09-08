using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartSentinelEye.AuditObservability.Infrastructure.Persistence;
using SmartSentinelEye.Integration.Tests.Fixtures;
using Xunit.Abstractions;

namespace SmartSentinelEye.Integration.Tests.Automation;

/// <summary>
/// Spec 106 (#749, spec 007 T099). The figure Automation's leg never had.
///
/// <para>
/// <b>Read this before quoting anything this file prints.</b> #749 asks for
/// <c>NFR001_RuleEvaluationLatencyTests</c> measuring <i>"<c>FabEventIngestedV1</c>
/// consume → action V1 published"</i>. <b>Neither of those two moments is
/// observable from a test process</b>, because Automation stamps neither: nothing
/// on the wire records when the handler was entered, and nothing records when the
/// broker took the outgoing message. A file named for that span would assert more
/// than it can see, so this one is named for the span it actually measures.
/// </para>
///
/// <para>
/// <b>The two moments, both already on the wire and both already on one audit
/// row.</b> AuditObservability subscribes to
/// <c>SystemVariableValueRequestedV1</c> and writes <c>occurred_at</c> plus the
/// whole serialised message, so no join and no cross-row correlation is needed:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>Moment A</b> — <c>Metadata.RootIngestedAt</c>, read as
///     <c>payload-&gt;'Metadata'-&gt;&gt;'RootIngestedAt'</c>: when EventIngestion
///     <i>accepted</i> the plant-floor event. Set there, forwarded unchanged by
///     <c>FabEventIngestedV1Handler</c> (spec 025).
///   </description></item>
///   <item><description>
///     <b>Moment B</b> — <c>Metadata.OccurredAt</c> (= <c>RequestedAt</c>), the
///     row's <c>occurred_at</c>: <c>clock.UtcNow</c> taken in
///     <c>FabEventIngestedV1Handler</c> after evaluation and <i>before</i> the
///     publish loop — when Automation <i>decided</i> what to publish.
///   </description></item>
/// </list>
///
/// <para>
/// <b>So this span overshoots at the head and undershoots at the tail, and is
/// therefore not a superset of NFR-001's span.</b> The head carries
/// EventIngestion's domain-event dispatch, its outbox release, one RabbitMQ hop
/// and Wolverine's deserialise. The tail is missing the publish itself,
/// Wolverine's flush at handler completion, and the broker send — all of which
/// happen after Moment B is stamped.
/// </para>
///
/// <para>
/// A pass therefore licenses exactly one sentence: <i>over N events on this
/// machine, the interval from EventIngestion accepting a plant-floor event to
/// Automation deciding its action had a p95 of X ms.</i> It does <b>not</b>
/// license "NFR-001 holds", "Automation's leg is within 100 ms", or "constitution
/// §IV's <c>event → overlay state</c> leg is measured" — that leg runs to
/// <i>effect applied</i>, and this measures a proper prefix of it. A failure is
/// equally constrained: the ingest tail and a broker hop are inside the figure,
/// so a breach here is a finding to split, not a verdict on Automation.
/// </para>
///
/// <para>
/// <b>Clock provenance.</b> Moment A is EventIngestion's clock and Moment B is
/// Automation's. On the Aspire fixture both processes share one OS clock, so the
/// difference is meaningful. On a distributed deployment it would not be, and the
/// figure must not be carried there.
/// </para>
///
/// <para>
/// <b>Why the count assertions run before the percentile.</b> A latency test's
/// characteristic failure is passing while measuring a path that skipped the
/// work — a stubbed evaluator or a short-circuited predicate makes the figure
/// <i>better</i>. So the row count and the correct-value count are asserted
/// first, and the SQL <c>COALESCE</c>s to <c>-1</c> rather than <c>0</c>, because
/// a zero over an empty population is a perfect score for a journey nobody
/// watched. Weakening either count to <c>ShouldBeGreaterThan(0)</c> would make
/// that failure silent.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public class AcceptToDecideLatencyTests(AspireFixture aspire, ITestOutputHelper output) : IAsyncLifetime
{
    private const string Fab = "munich";

    private const int WarmupIterations = 20;
    private const int MeasuredIterations = 100;
    private const double BudgetMilliseconds = 100;

    private const int GuardWarmupIterations = 3;
    private const int GuardMeasuredIterations = 5;

    /// <summary>
    /// Four times the budget, on the same reasoning
    /// <c>NFR_VariableResolutionLatencyTests.LegBudgetMs</c> gives: on a shared
    /// runner beside a Postgres, a RabbitMQ, a Keycloak and eight services, a
    /// figure at the real budget would either flake or be quoted. Four times it
    /// catches an order-of-magnitude regression without policing the budget.
    /// </summary>
    private const double GuardBudgetMilliseconds = 400;

    /// <summary>
    /// The rule's value expression is <c>100 - $.payload.cycleTime * 2</c> and
    /// <c>cycleTime</c> cycles <c>1..30</c>, so consecutive iterations always
    /// expect a different value. That is what keeps the per-iteration poll a real
    /// sync point: a poll waiting for the value the variable already holds
    /// returns instantly and gates nothing.
    /// </summary>
    private const int CycleTimeValues = 30;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>How long one measured effect may take before the run is a failure rather than a slow sample.</summary>
    private static readonly TimeSpan EffectDeadline = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The first event of a message type on a cold stack was measured at 3.6–4.8 s
    /// (spec 023), and the first event of a cold stack at 10–16 s. This wait is
    /// what keeps that cost out of the first measured sample.
    /// </summary>
    private static readonly TimeSpan ReadinessDeadline = TimeSpan.FromSeconds(120);

    /// <summary>Audit writes are batched (ADR-0127), so the last rows are not visible the instant the last effect lands.</summary>
    private static readonly TimeSpan SettleDeadline = TimeSpan.FromSeconds(90);

    private readonly PlantFloor plant = new(aspire);
    private readonly List<string> definedVariables = [];
    private readonly List<string> activatedRules = [];

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Takes the run's variables and rules away again (#2004). A measurement run
    /// writes 120 values to one variable, and a run-mode stack keeps every one of
    /// them forever.
    /// </summary>
    public async Task DisposeAsync()
    {
        using HttpClient variables = await aspire.CreateAdminClientAsync("system-variables");
        using HttpClient rules = await aspire.CreateAdminClientAsync("automation");

        foreach (string rule in activatedRules)
        {
            using HttpResponseMessage archived = await RuleRequests.PostAsync(rules, rule, "archive");
            archived.EnsureSuccessStatusCode();
        }

        await VariableRequests.ArchiveAllAsync(variables, definedVariables, CancellationToken.None);
    }

    /// <summary>
    /// US1. The measurement, and the whole of #749's deliverable.
    ///
    /// <para>
    /// <b>Traited out of CI</b> (<c>ci.yml</c>'s
    /// <c>Category!=Measurement&amp;…</c> filter), and the reason is the second
    /// half of that filter's own comment rather than duration: this test's entire
    /// product is a <i>figure</i>, and a figure taken on a shared GitHub runner
    /// alongside a Postgres, a RabbitMQ, a Keycloak and eight services is a figure
    /// about the runner — which would then be quoted as if it measured this code.
    /// </para>
    ///
    /// <para>
    /// <b>The cost of that, stated rather than inherited:</b> an excluded test is
    /// one nobody watches, and this repository has a documented history of tests
    /// that were green because they never ran.
    /// <see cref="Accept_to_decide_has_not_regressed_by_an_order_of_magnitude"/> is
    /// what pays that cost down, which is why it carries no trait.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Measurement")]
    public Task Accept_to_decide_p95_stays_within_the_automation_leg_budget() =>
        MeasureAsync(WarmupIterations, MeasuredIterations, BudgetMilliseconds, CancellationToken.None);

    /// <summary>
    /// US2. The same helper, cheap enough for CI and loose enough not to flake
    /// there — so an order-of-magnitude regression is caught by the build rather
    /// than by the next person who happens to run a measurement.
    /// </summary>
    [Fact]
    public Task Accept_to_decide_has_not_regressed_by_an_order_of_magnitude() =>
        MeasureAsync(
            GuardWarmupIterations, GuardMeasuredIterations, GuardBudgetMilliseconds, CancellationToken.None);

    private async Task MeasureAsync(
        int warmup, int measured, double budgetMilliseconds, CancellationToken cancellationToken)
    {
        using HttpClient variables = await aspire.CreateAdminClientAsync("system-variables", cancellationToken);
        using HttpClient rules = await aspire.CreateAdminClientAsync("automation", cancellationToken);

        // **v4, not v7.** A Guid v7's leading hex digits are the top bits of its
        // millisecond timestamp, so the first eight are the same for roughly a
        // minute — and the guard fact runs seconds after the measurement one.
        // Both facts then mint the same rule name, the second `GET /rules/{name}`
        // answers 400, and the run fails in arrange for a reason that looks
        // nothing like its cause. Observed, not theorised: it is what the second
        // clean run of this file did.
        string suffix = Guid.NewGuid().ToString("N")[..8];

        // Two variables and two rules, so the warm-up's rows are kept out of the
        // population **by construction**: the query selects on the measured
        // variable's name, so warm-up rows are never selected rather than trimmed
        // afterwards on an ordering the query does not guarantee.
        string warmVariable = $"warm{suffix}";
        string measuredVariable = $"meas{suffix}";
        await DefineVariableAsync(variables, warmVariable, cancellationToken);
        await DefineVariableAsync(variables, measuredVariable, cancellationToken);

        string warmKind = $"PlcWarm{suffix}";
        string measuredKind = $"PlcMeas{suffix}";
        await ActivateRuleAsync(rules, $"lw{suffix}", warmVariable, warmKind, cancellationToken);
        await ActivateRuleAsync(rules, $"lm{suffix}", measuredVariable, measuredKind, cancellationToken);

        await DriveAsync(variables, warmKind, warmVariable, index: 0, ReadinessDeadline, cancellationToken);
        for (int index = 1; index <= warmup; index++)
        {
            await DriveAsync(variables, warmKind, warmVariable, index, EffectDeadline, cancellationToken);
        }

        output.WriteLine($"warmed up over {warmup} events on {warmVariable}");

        HashSet<string> expectedValues = new(StringComparer.Ordinal);
        for (int index = 0; index < measured; index++)
        {
            expectedValues.Add(ExpectedValue(index));
            await DriveAsync(variables, measuredKind, measuredVariable, index, EffectDeadline, cancellationToken);
        }

        output.WriteLine($"drove {measured} measured events on {measuredVariable}");

        await using AuditObservabilityDbContext audit =
            await aspire.CreateAuditObservabilityDbContextAsync(cancellationToken);
        await SettleAsync(audit, measuredVariable, measured, cancellationToken);

        AcceptToDecideSpan span = await SpanAsync(audit, measuredVariable, [.. expectedValues], cancellationToken);

        // (1) before anything is printed as if it meant something: a percentile
        // over an empty set is NULL, so a test asserting only the percentile
        // would pass on a stack where nothing fired at all.
        span.TotalRows.ShouldBe(
            measured,
            $"{span.TotalRows} of {measured} action events reached the audit log for "
            + $"'{measuredVariable}'. A low count means the pipeline did not fire — not that "
            + "it was fast. Look at the rule cache, the handler and the broker before reading "
            + "any figure below.");

        // (2) the trap. A stubbed evaluator, a short-circuited predicate or a
        // stale cache hit all make the *latency* better, so correctness is
        // asserted separately and before the figure.
        span.RowsWithExpectedValue.ShouldBe(
            measured,
            $"{span.RowsWithExpectedValue} of {span.TotalRows} action events carried both a "
            + "RootIngestedAt stamp and one of the values the rule's expression yields. The "
            + "pipeline ran and produced the wrong effect, or stopped forwarding "
            + "RootIngestedAt — either way the figure below is over the wrong population.");

        // (3) the artefact. The span is spelled out here so the figure cannot be
        // quoted without its definition (FR-008).
        //
        // Invariant, because this line is quoted into a PR body and read on
        // other machines: under a German locale `{p95:F1}` renders 28.6 as
        // "28,6", which a reader elsewhere may take for 286.
        string figures = string.Create(
            CultureInfo.InvariantCulture,
            $"n={span.TotalRows} min={span.Min:F1} p50={span.P50:F1} p95={span.P95:F1} p99={span.P99:F1} max={span.Max:F1} ms — budget {budgetMilliseconds:F0} ms");

        output.WriteLine(
            $"[accept→decide] {figures}. "
            + "Span: EventIngestion accepting the plant-floor event (Metadata.RootIngestedAt) → "
            + "Automation deciding its action (Metadata.OccurredAt). This is NOT NFR-001's "
            + "consume→published span: it overshoots at the head (ingest dispatch, outbox "
            + "release, one broker hop, deserialise) and undershoots at the tail (the publish, "
            + "Wolverine's flush and the broker send are after the stamp).");

        string breach = string.Create(
            CultureInfo.InvariantCulture,
            $"the p95 of the accept→decide span was {span.P95:F1} ms over {span.TotalRows} events, against a budget of {budgetMilliseconds:F0} ms");

        span.P95.ShouldBeLessThanOrEqualTo(
            budgetMilliseconds,
            $"{breach}. This span is not NFR-001's — it "
            + "carries EventIngestion's dispatch and a broker hop at the head and stops before "
            + "the publish at the tail — so this failure does not by itself convict Automation "
            + "of breaching its leg. Split the span before concluding: "
            + "IngestThroughputMeasurementTests already bounds the ingest part.");
    }

    // ---- driving -------------------------------------------------------------

    /// <summary>
    /// One event, then a wait for its own effect before the next is published
    /// (FR-004). This measures single-event latency, not throughput under
    /// concurrency, so it must not become a saturating burst.
    ///
    /// <para>
    /// The wait is a <b>sync point, not a measurement</b> — its own HTTP cost is
    /// outside the figure entirely, because the figure is read from stamps taken
    /// inside the services. A timeout fails the run naming the iteration; it
    /// never <c>continue</c>s, because a dropped sample both shrinks the
    /// population and removes the slowest observation from it.
    /// </para>
    /// </summary>
    private async Task DriveAsync(
        HttpClient variables,
        string kind,
        string variable,
        int index,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        string expected = ExpectedValue(index);
        await plant.PublishRawAsync(PlantFloor.Payload(kind, CycleTime(index)));
        await WaitForValueAsync(variables, variable, expected, index, deadline, cancellationToken);
    }

    private static int CycleTime(int index) => (index % CycleTimeValues) + 1;

    private static string ExpectedValue(int index) =>
        (100 - (CycleTime(index) * 2)).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Polls until the endpoint answers <b>200</b> <i>and</i> carries
    /// <paramref name="expected"/>, delaying between polls, and reports which of
    /// the two it was still missing.
    ///
    /// <para>
    /// The shape is
    /// <c>TwoPlaceholdersInOneLabelTests.WaitUntilResolvableAsync</c>'s, not
    /// <c>NFR_VariableResolutionLatencyTests.WaitUntilResolvableAsync</c>'s —
    /// that one maps a non-200 to an empty string, so it returns on its first
    /// iteration against a 404, and it has no delay so it would spin if it ever
    /// looped (#2201). The distinction matters more here than anywhere: a
    /// readiness check that returns early does not merely wait less, it moves
    /// set-up work into the first measured sample.
    /// </para>
    /// </summary>
    private static async Task WaitForValueAsync(
        HttpClient variables,
        string name,
        string expected,
        int index,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        DateTimeOffset expiry = DateTimeOffset.UtcNow + deadline;
        string? observed = null;
        bool everAnswered = false;

        while (DateTimeOffset.UtcNow < expiry)
        {
            observed = await ReadValueAsync(variables, name, cancellationToken);
            everAnswered |= observed is not null;
            if (string.Equals(observed, expected, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        string neverCarried = everAnswered
            ? "the last read was not a 200, though the endpoint had answered earlier in this wait"
            : "the endpoint never answered 200 for that variable";
        string diagnosis = observed is null
            ? neverCarried
            : $"a 200 carrying '{observed}' rather than '{expected}'";

        throw new TimeoutException(
            $"iteration {index} on '{name}' never reached '{expected}' within "
            + $"{deadline.TotalSeconds:F0} s; {diagnosis}. A dropped sample must not be "
            + "silently excluded from the percentile, so the run fails here rather than "
            + "measuring the events that did land.");
    }

    /// <summary>The value, or <c>null</c> when the read was not a 200 — two different states the wait must tell apart.</summary>
    private static async Task<string?> ReadValueAsync(
        HttpClient variables, string name, CancellationToken cancellationToken)
    {
        using HttpResponseMessage fetched =
            await variables.GetAsync($"/system-variables/{name}", cancellationToken);
        if (!fetched.IsSuccessStatusCode)
        {
            return null;
        }

        JsonElement body = await fetched.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return body.TryGetProperty("value", out JsonElement value) ? value.GetString() : null;
    }

    // ---- measuring -----------------------------------------------------------

    /// <summary>
    /// Waits for the run's rows to reach the store before the percentile query,
    /// rather than sleeping a fixed interval. Audit writes are batched
    /// (ADR-0127), which delays <c>written_at</c> — not <c>occurred_at</c> and
    /// not the payload — so the figure is unaffected and only the readback is.
    /// Returns quietly when the count stalls: the count assertion is what reports
    /// a short population, and it says more than a timeout here would.
    /// </summary>
    private static async Task SettleAsync(
        AuditObservabilityDbContext audit, string variable, int measured, CancellationToken cancellationToken)
    {
        DateTimeOffset expiry = DateTimeOffset.UtcNow + SettleDeadline;

        while (DateTimeOffset.UtcNow < expiry)
        {
            List<long> counted = await audit.Database
                .SqlQueryRaw<long>(
                    "SELECT count(*) AS \"Value\" FROM audit_events "
                    + "WHERE event_kind = 'SystemVariableValueRequestedV1' AND payload->>'Name' = {0}",
                    variable)
                .ToListAsync(cancellationToken);

            if (counted[0] >= measured)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    /// <summary>
    /// The measurement: one query, one pass over the run's own rows.
    ///
    /// <para>
    /// Selected on <c>payload-&gt;&gt;'Name'</c> rather than
    /// <c>resource_identifier</c>. <c>V1ResourceMap</c>'s convention picker
    /// prefers the first <c>Guid</c>-typed property and only falls back to the
    /// name allow-list when there is none — <c>SystemVariableValueRequestedV1</c>
    /// has one, <c>CausingEventIdentifier</c>, so its <c>resource_identifier</c>
    /// is the causing plant-floor event and not the variable. Verified against a
    /// real <c>OverlayHighlightRequestedV1</c> row, which carries its
    /// <c>OverlayIdentifier</c> there for the same reason.
    /// </para>
    ///
    /// <para>
    /// <c>COALESCE(…, -1)</c> and never <c>COALESCE(…, 0)</c>: an empty
    /// population yields <c>NULL</c>, and a zero there is a perfect score for a
    /// journey nobody watched. <c>-1</c> cannot be mistaken for a good figure.
    /// <c>percentile_cont</c> rather than an index into a sorted list, matching
    /// <c>IngestSpanMeasurement.PercentilesAsync</c> — two spellings of "p95" in
    /// one repository is how two figures come to disagree.
    /// </para>
    ///
    /// <para>
    /// Negative deltas are not clamped. They would mean Automation's clock ran
    /// behind EventIngestion's, and a clamp manufactures a perfect score.
    /// </para>
    /// </summary>
    private static async Task<AcceptToDecideSpan> SpanAsync(
        AuditObservabilityDbContext audit,
        string variable,
        string[] expectedValues,
        CancellationToken cancellationToken)
    {
        object[] arguments = [variable, expectedValues];

        List<double> figures = await audit.Database
            .SqlQueryRaw<double>(
                "SELECT unnest(ARRAY["
                + "count(*)::float8, "
                + "count(*) FILTER (WHERE delta IS NOT NULL AND value_expected)::float8, "
                + "COALESCE(min(delta), -1), "
                + "COALESCE(percentile_cont(0.50) WITHIN GROUP (ORDER BY delta), -1), "
                + "COALESCE(percentile_cont(0.95) WITHIN GROUP (ORDER BY delta), -1), "
                + "COALESCE(percentile_cont(0.99) WITHIN GROUP (ORDER BY delta), -1), "
                + "COALESCE(max(delta), -1)]) AS \"Value\" FROM ("
                + "SELECT EXTRACT(EPOCH FROM ("
                + "occurred_at - (payload->'Metadata'->>'RootIngestedAt')::timestamptz)) * 1000 AS delta, "
                + "(payload->>'Value') = ANY({1}) AS value_expected "
                + "FROM audit_events "
                + "WHERE event_kind = 'SystemVariableValueRequestedV1' AND payload->>'Name' = {0}"
                + ") samples",
                arguments)
            .ToListAsync(cancellationToken);

        return new AcceptToDecideSpan(
            TotalRows: (int)figures[0],
            RowsWithExpectedValue: (int)figures[1],
            Min: figures[2],
            P50: figures[3],
            P95: figures[4],
            P99: figures[5],
            Max: figures[6]);
    }

    /// <summary>
    /// What one run produced. The two counts sit beside the percentiles rather
    /// than collapsing into one number: rows that do not exist and rows that
    /// exist carrying the wrong value are different findings with different
    /// causes.
    /// </summary>
    private sealed record AcceptToDecideSpan(
        int TotalRows, int RowsWithExpectedValue, double Min, double P50, double P95, double P99, double Max);

    // ---- arranging -----------------------------------------------------------

    private async Task DefineVariableAsync(
        HttpClient variables, string name, CancellationToken cancellationToken)
    {
        using HttpResponseMessage defined = await variables.PostAsJsonAsync(
            "/system-variables",
            new
            {
                name,
                type = "Number",
                initialValue = "0",
                truthyLabel = (string?)null,
                falsyLabel = (string?)null,
            },
            cancellationToken);

        defined.EnsureSuccessStatusCode();
        definedVariables.Add(name);
    }

    /// <summary>
    /// Creates the rule, publishes it, and <b>reads back <c>Active</c></b>.
    /// <c>POST /rules</c> mints a Draft and only Active rules are evaluated, so a
    /// run against an unpublished rule would fail on the count assertion for a
    /// reason that has nothing to do with latency.
    ///
    /// <para>
    /// A trigger kind unique to the run, so one event fires exactly one rule and
    /// the suite's other <c>PlcCycleStart</c> rules cannot join the population.
    /// </para>
    /// </summary>
    private async Task ActivateRuleAsync(
        HttpClient rules, string name, string variable, string triggerKind, CancellationToken cancellationToken)
    {
        using HttpResponseMessage created = await rules.PostAsJsonAsync(
            $"/rules?fabId={Fab}",
            new
            {
                name,
                triggerSource = "plc",
                triggerKind,
                predicate = "$.payload.cycleTime <= 30",
                actionType = "SetVariableValue",
                variableName = variable,
                valueExpression = "100 - $.payload.cycleTime * 2",
                overlayIdentifier = (Guid?)null,
                durationMs = (int?)null,
            },
            cancellationToken);

        created.StatusCode.ShouldBe(
            HttpStatusCode.Created, await created.Content.ReadAsStringAsync(cancellationToken));
        activatedRules.Add(name);

        using HttpResponseMessage published = await RuleRequests.PostAsync(rules, name, "publish");
        published.EnsureSuccessStatusCode();

        using HttpResponseMessage readBack = await rules.GetAsync($"/rules/{name}?fabId={Fab}", cancellationToken);
        readBack.EnsureSuccessStatusCode();
        JsonElement body = await readBack.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        body.GetProperty("state").GetString().ShouldBe(
            "Active",
            $"rule '{name}' is not Active, so nothing it says would have happened regardless — "
            + "every figure below would be measuring an empty population");
    }
}
