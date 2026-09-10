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

    // ≈ 100 ms expected per batch (10 µs × 10 000); 5× headroom for the
    // shared CI runner. The order-of-magnitude expectation is what NFR-002
    // asks for, not a tight production envelope.
    private const double MedianBudgetMilliseconds = 500;

    // Gross-regression guard for the slowest batch.
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
