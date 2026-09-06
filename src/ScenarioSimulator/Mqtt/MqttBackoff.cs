namespace SmartSentinelEye.ScenarioSimulator.Mqtt;

/// <summary>
/// Capped, jittered exponential backoff: nothing before the first attempt, then
/// 1 s, 2 s, 4 s, 8 s, 16 s, capped at 30 s, each multiplied by a factor in
/// [0.8, 1.2].
///
/// <para>
/// <b>This is a deliberate copy of EventIngestion's <c>MqttBackoff</c>, not an
/// oversight.</b> MQTTnet 5 removed the managed client that owned the reconnect,
/// and the two callers live in assemblies with no common reference — sharing
/// twenty lines of arithmetic would mean putting an MQTT concern into
/// <c>Shared.Kernel</c>, which all nine bounded contexts reference. Do not
/// extract a shared client to remove this duplication (spec 079, ADR-0036).
/// </para>
/// </summary>
internal sealed class MqttBackoff(TimeSpan first, TimeSpan cap)
{
    private int attempts;

    public MqttBackoff()
        : this(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30))
    {
    }

    /// <summary>How long to wait before the next attempt.</summary>
    public TimeSpan Next()
    {
        if (attempts++ == 0)
        {
            return TimeSpan.Zero;
        }

        double seconds = Math.Min(first.TotalSeconds * Math.Pow(2, attempts - 2), cap.TotalSeconds);
        return TimeSpan.FromSeconds(seconds * Jitter());
    }

    /// <summary>Puts the next wait back to nothing, after a connect that worked.</summary>
    public void Reset() => attempts = 0;

    // Not a security decision: this de-synchronises retries between clients, it
    // does not generate anything anyone could guess their way into.
#pragma warning disable CA5394
    private static double Jitter() => 0.8 + (Random.Shared.NextDouble() * 0.4);
#pragma warning restore CA5394
}
