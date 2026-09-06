using Microsoft.EntityFrameworkCore;
using SmartSentinelEye.AuditObservability.Infrastructure.Persistence;
using SmartSentinelEye.Shared.Kernel;
using Xunit.Abstractions;

namespace SmartSentinelEye.Integration.Tests.AuditObservability;

/// <summary>
/// The same division, of the same span, at the same paced rate — against a stack
/// running in <b>run mode</b> (spec 054 US1).
///
/// <para>
/// <b>This class carries no <c>[Collection]</c> attribute, and that is the whole
/// mechanism.</b> The attribute is what injects <c>AspireFixture</c>, and the
/// fixture boots its own stack — which is exactly what makes it not run mode. No
/// attribute, no fixture, no stack. It is asserted in
/// <see cref="RunModeDriverTests"/> rather than left to a reader's care, because
/// the failure would be silent and would produce a complete, well-formed
/// breakdown labelled "run mode" that came from somewhere else.
/// </para>
///
/// <para>
/// Excluded from CI like its fixture sibling, and for a stronger reason: it needs
/// a stack CI does not run, and it refuses rather than starting one.
/// </para>
/// </summary>
public class RunModeIngestAttributionTests(ITestOutputHelper output)
{
    /// <summary>
    /// The log level the run-mode services are running at, read from the
    /// environment because that is what propagates into the AppHost's children.
    /// Absent means nothing overrode the appsettings, so the level was inherited
    /// rather than chosen for this run — <c>Information</c> as of spec 081
    /// (2026-09-06), a pairing with no measured throughput figure (#2133).
    /// </summary>
    private static string ServiceLogLevel =>
        ChosenServiceLogLevel ?? "Information (from appsettings)";

    /// <summary>
    /// The level somebody set for this run, or absent if nobody did — the value
    /// and its provenance being different facts.
    /// </summary>
    private static string? ChosenServiceLogLevel =>
        Environment.GetEnvironmentVariable("Logging__LogLevel__Default");

    /// <summary>Whether the level above was chosen for this run rather than inherited.</summary>
    private static bool ServiceLogLevelWasChosen => ChosenServiceLogLevel is not null;

    [Trait("Category", "Measurement")]
    [Fact]
    public async Task Where_the_ingest_span_goes_in_run_mode()
    {
        Option<RunModeStackAddress> configured = RunModeStackAddress.FromEnvironment();

        // **A refusal, not a fallback.** Spelled out rather than asserted with a
        // bare Shouldly call so the message carries the runbook.
        configured.HasValue.ShouldBeTrue(RunModeStackAddress.Missing);

        RunModeStackAddress address = configured.Value;

        // **The endpoint before anything that can fail**, because the run that
        // most needs this line is the one that throws inside the drive — a wrong
        // address, an expired token — and printing it afterwards loses it exactly
        // then. It is the only guard against attributing a figure to the wrong
        // stack, and no automated check can replace it.
        output.WriteLine($"environment                           : run mode (AppHost)");
        output.WriteLine($"endpoint reached                      : {address.Describe()}");
        output.WriteLine($"service log level                     : {ServiceLogLevel}");

        using HttpClient variables = await address.CreateAuthenticatedClientAsync(CancellationToken.None);
        await using AuditObservabilityDbContext context = address.CreateAuditContext();

        IngestSpanResult result = await IngestSpanMeasurement.RunAsync(
            variables,
            context,
            environment: "run mode (AppHost)",
            endpoint: address.Describe(),
            logLevel: ServiceLogLevel,
            logLevelWasChosen: ServiceLogLevelWasChosen,
            CancellationToken.None);

        output.WriteLine(result.Conditions.Describe());

        // **Asserted before the breakdown is printed, not after.** A run where
        // only nine hundred rows landed would otherwise emit a complete,
        // well-formed division — the exact output shape a good run produces —
        // and only then fail. A breakdown that looks like a result must not be
        // printed for a run that is not one.
        result.Conditions.RowsMeasured.ShouldBe(
            IngestRunShape.MeasuredEvents,
            $"only {result.Conditions.RowsMeasured} of {IngestRunShape.MeasuredEvents} events reached "
            + "the audit store, so there is no population to divide");

        output.WriteLine("--- typical event (medians over every row) ---");
        output.WriteLine(result.Typical.Describe());
        output.WriteLine("--- tail band (rows at or above the p99 of the total) ---");
        output.WriteLine(result.Tail.Describe());
        output.WriteLine($"this process vs the run-mode server: {result.Offset}");
        output.WriteLine($"  clock standing: {result.Verdict.Standing} — {result.Verdict.Reason}");

        // **Two senses of "established", kept apart.** The verdict above is about
        // the *clocks*, and here they agree closely. The write leg is still not
        // established as a measurement, for a reason the clocks cannot fix: it
        // ends at insert rather than commit, where NFR-001's words are "audit row
        // committed". Close clocks make it readable; they do not make it the
        // figure the requirement names.
        output.WriteLine(
            "  the write leg crosses host and container clocks — bounded by that offset — and ends "
            + "at insert, not commit, so it under-reports the requirement's back end either way");

        result.Typical.EveryRowStamped.ShouldBeTrue(
            $"{result.Typical.RowsMissingStamps} rows arrived without the measurement stamps; set "
            + "AuditObservability__Measurement__RecordIngestBreakdown=true in the shell that launches "
            + "the AppHost, before it starts");

        result.Typical.PartsCoverEveryRow.ShouldBeTrue(
            $"the median row leaves {result.Typical.PerRowResidualMs:F3} ms between its span and its "
            + "parts; consecutive stamps cannot do that, so the stamps disagree with the timestamps "
            + "that bracket them");

        result.Tail.PartsCoverEveryRow.ShouldBeTrue(
            $"the median tail row leaves {result.Tail.PerRowResidualMs:F3} ms between its span and its parts");

        result.Verdict.IsEstablished.ShouldBeTrue(
            $"{result.Verdict.Reason} — the write leg subtracts a host-process stamp from a container "
            + "one, and it is the same size as that disagreement, so it cannot be reported as measured");

        result.Conditions.LoggingIsVerbose.ShouldBeFalse(
            $"the services are logging at '{result.Conditions.LogLevel}', where this stack sustains "
            + "~80 ev/s against a target of 100; set Logging__LogLevel__Default=Warning in the shell "
            + "that launches the AppHost");

        result.Conditions.RateWasMet.ShouldBeTrue(
            $"the run drove {result.Conditions.AchievedRatePerSecond:F1} ev/s against a target of "
            + $"{IngestRunShape.TargetRatePerSecond:F0}; NFR-001 is a claim about that rate sustained, "
            + "and a breakdown taken at another rate answers another question");
    }
}
