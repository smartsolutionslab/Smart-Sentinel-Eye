using System.Globalization;

namespace SmartSentinelEye.Integration.Tests.AuditObservability;

/// <summary>
/// What a measurement run reports about itself (spec 054 US2).
///
/// <para>
/// <b>A breakdown without these is not comparable with anything.</b> The figures
/// this feature produces exist to sit beside figures from another stack, and a
/// number whose provenance lives in somebody's memory of which shell they ran it
/// in cannot be set beside anything honestly.
/// </para>
///
/// <para>
/// <b><see cref="Endpoint"/> is the one that matters most</b>, and it is the one
/// no automated check can replace. An endpoint is an endpoint: nothing in a run
/// can tell the stack it was aimed at from another that happened to answer. What
/// establishes it is a person reading this line against the stack they started —
/// so the line has to be there, and has to say what was actually reached rather
/// than what was configured.
/// </para>
///
/// <para>
/// <b><see cref="LogLevel"/> and <see cref="LogLevelWasChosen"/> are two facts,
/// not one.</b> The level says what the services were at; the flag says whether
/// anybody picked it <i>for this run</i>. A run that inherits whatever the
/// Development appsettings happen to pin is a different kind of run from one
/// launched with the level named in its shell, and the level's spelling alone
/// cannot tell the two apart.
/// </para>
/// </summary>
public sealed record IngestRunConditions(
    string Environment,
    string Endpoint,
    double IntendedRatePerSecond,
    double AchievedRatePerSecond,
    string LogLevel,
    bool LogLevelWasChosen,
    bool MeasurementSwitchOn,
    int RowsMeasured,
    int RowsMissingStamps)
{
    /// <summary>
    /// Whether this run's logging leaves it unfit to measure through.
    ///
    /// <para>
    /// <b>Two ways in, and they refuse for different reasons.</b> At Debug or
    /// Trace this stack sustains 60–83 ev/s — below the rate the requirement
    /// names — so a breakdown taken there measures the logging as much as the
    /// pipeline. And a level nobody chose <i>for this run</i> is refused
    /// whatever it is: an inherited level is whatever the Development
    /// appsettings pin at the time, and no throughput figure exists for a
    /// pairing nobody has measured, so there is nothing to say a run taken
    /// under it clears the target.
    /// </para>
    ///
    /// <para>
    /// <b>What the Development appsettings pin is deliberately not named
    /// here.</b> A sentence naming it goes silently false the moment those files
    /// move, and one here did — the refusal of an inherited level used to ride
    /// on the spelling of a literal rather than on the fact. The run carries its
    /// level beside <see cref="LogLevelWasChosen"/> so provenance is read as a
    /// fact rather than inferred from a string.
    /// </para>
    /// </summary>
    public bool LoggingIsVerbose =>
        !LogLevelWasChosen
        || LogLevel.StartsWith("Debug", StringComparison.OrdinalIgnoreCase)
        || LogLevel.StartsWith("Trace", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the achieved rate lands close enough to the intended one.</summary>
    public bool RateWasMet =>
        AchievedRatePerSecond >= IngestRunShape.MinimumAcceptableRate
        && AchievedRatePerSecond <= IngestRunShape.MaximumAcceptableRate;

    public string Describe()
    {
        // The provenance is rendered rather than left to the level's spelling:
        // a reader of a printed block cannot otherwise tell a level somebody
        // picked from one the appsettings happened to supply, and only one of
        // those two is a condition anybody chose to measure under.
        string level = LogLevelWasChosen
            ? LogLevel
            : $"{LogLevel} (inherited from the appsettings; nobody chose it for this run)";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"""
             environment                           : {Environment}
             endpoint reached                      : {Endpoint}
             rate, intended → achieved             : {IntendedRatePerSecond:F0} → {AchievedRatePerSecond:F1} ev/s
             service log level                     : {level}
             measurement switch                    : {(MeasurementSwitchOn ? "ON" : "OFF")}
             rows measured                         : {RowsMeasured} ({RowsMissingStamps} missing stamps)
             """);
    }
}
