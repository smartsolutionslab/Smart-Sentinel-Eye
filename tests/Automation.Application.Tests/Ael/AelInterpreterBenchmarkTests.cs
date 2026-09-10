using System.Diagnostics;
using System.Runtime;
using SmartSentinelEye.Automation.Application.Ael;
using Xunit.Abstractions;

namespace SmartSentinelEye.Automation.Application.Tests.Ael;

/// <summary>
/// Coarse benchmark for NFR-002 — a representative predicate evaluates
/// in ≈ 10 µs on dev hardware (≈ 100 ms per 10 000-eval batch).
///
/// <para>
/// Rather than time a single 100 000-eval run (which flakes when a GC
/// pause or scheduler stall lands in the one timed window on a shared
/// CI runner — the budget there equalled the expected runtime, so there
/// was zero headroom), it times <see cref="Batches"/> batches and gates
/// the <em>median</em> batch against a generous budget, guarding the
/// slowest batch only against a gross regression. GC is stabilised up
/// front so the timings reflect the interpreter, not a collection.
/// Total work is unchanged (100 000 evals). See issue #967.
/// </para>
/// </summary>
public class AelInterpreterBenchmarkTests(ITestOutputHelper output)
{
    private const int Batches = 10;
    private const int BatchSize = 10_000;

    /// <summary>
    /// Gate on the median batch. <b>Threshold 500 ms; observed 20.7 ms,
    /// 26.7, 25.4 and 23.4 ms</b> across four consecutive Release runs on a
    /// dev box (2026-09-10, issue #2149), so the budget sits roughly <b>19×</b>
    /// above the slowest median anyone has recorded — 2.07 to 2.67 µs/eval.
    ///
    /// <para>
    /// <b>The ≈ 100 ms this comment used to cite was never a run.</b> It is
    /// ADR-0099's requirement — <c>≤ 10 µs p99/eval</c>
    /// (<c>docs/adr/0099-hand-rolled-ael.md:26</c>) — arithmetic'd out to a
    /// batch, and the "5× headroom" it claimed was 500 ÷ that requirement.
    /// Measured, the interpreter is about <b>4× inside</b> the requirement,
    /// which is why the real margin is 19× rather than 5×.
    /// </para>
    ///
    /// <para>
    /// The margin is left where it is, deliberately and by someone else's
    /// decision (#2149, #2141): a threshold is not tightened in the pass that
    /// first measures it. What made the number generous is on the record —
    /// #967's single-sample version equalled the expected runtime, so one GC
    /// pause in the one timed window turned it red, and the fix bought
    /// headroom rather than precision.
    /// </para>
    ///
    /// <para>
    /// <b>Not one of constitution §IV's six legs.</b> The interpreter runs
    /// inside the projection half of <i>Event → overlay state (RabbitMQ +
    /// projection) ≤ 200 ms</i>, so a regression here would consume that leg's
    /// budget — but this constant is a component throughput expectation
    /// (NFR-002), not a leg budget, and 100 000 evals do not occur on one
    /// event's path.
    /// </para>
    /// </summary>
    private const double MedianBudgetMilliseconds = 500;

    /// <summary>
    /// Gross-regression guard for the slowest batch. <b>Threshold 1 000 ms;
    /// slowest batch observed at 27.4, 39.8, 38.3 and 41.2 ms</b> across four
    /// runs — a <b>24×</b> margin, wider than the median gate's because
    /// this one exists to catch an order-of-magnitude regression (a
    /// reintroduced allocation per eval, a lost cache) rather than to describe
    /// the tail.
    /// </summary>
    private const double CeilingMilliseconds = 1_000;

    [Fact]
    public void Predicate_evaluation_throughput_clears_NFR002()
    {
        AelExpression expression = AelParser.Parse(AelFixtures.SimplePlcPredicate);
        EvaluationContext context = AelFixtures.ContextFor(AelFixtures.PlcCycleStartContext);

        for (int i = 0; i < BatchSize; i++)
        {
            _ = AelInterpreter.Evaluate(expression, context);
        }

        double[] batchMilliseconds = new double[Batches];
#pragma warning disable S1215 // Intentional: deterministic benchmark stabilisation, not production code.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
#pragma warning restore S1215
        GCLatencyMode previousLatencyMode = GCSettings.LatencyMode;
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        try
        {
            for (int batch = 0; batch < Batches; batch++)
            {
                long start = Stopwatch.GetTimestamp();
                for (int i = 0; i < BatchSize; i++)
                {
                    _ = AelInterpreter.Evaluate(expression, context);
                }
                batchMilliseconds[batch] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
        }
        finally
        {
            GCSettings.LatencyMode = previousLatencyMode;
        }

        Array.Sort(batchMilliseconds);
        double median = batchMilliseconds[Batches / 2];
        double slowest = batchMilliseconds[^1];

        // Reported whether or not it passes (#2149): both customMessages below
        // are built only on failure, so a green run discarded the throughput it
        // had just measured, and the margin was unknowable from the tree.
        output.WriteLine(
            $"{Batches} batches x {BatchSize} evals: median = {median:F1} ms "
            + $"({median * 1000 / BatchSize:F2} us/eval), slowest = {slowest:F1} ms "
            + $"(budgets: median {MedianBudgetMilliseconds} ms, ceiling {CeilingMilliseconds} ms)");

        // Gate on the median batch; guard the slowest only against gross
        // regression — see the class remarks for why the single-sample gate
        // was flaky.
        median.ShouldBeLessThan(
            MedianBudgetMilliseconds,
            $"median batch of {BatchSize} evals took {median:F1} ms (≈ {median * 1000 / BatchSize:F1} µs/eval); exceeds the {MedianBudgetMilliseconds} ms NFR-002 budget. slowest = {slowest:F1} ms");
        slowest.ShouldBeLessThan(
            CeilingMilliseconds,
            $"slowest batch {slowest:F1} ms exceeded the {CeilingMilliseconds} ms regression ceiling. median = {median:F1} ms");
    }
}
