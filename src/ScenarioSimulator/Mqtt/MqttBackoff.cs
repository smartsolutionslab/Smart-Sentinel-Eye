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
    private TimeSpan servedDelay;

    public MqttBackoff()
        : this(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30))
    {
    }

    /// <summary>Attempts made since the last <see cref="Reset"/>, for the log.</summary>
    public int Attempt => attempts;

    /// <summary>How long to wait before the next attempt.</summary>
    public TimeSpan Next()
    {
        if (attempts++ == 0)
        {
            servedDelay = TimeSpan.Zero;
            return servedDelay;
        }

        double seconds = Math.Min(first.TotalSeconds * Math.Pow(2, attempts - 2), cap.TotalSeconds);
        servedDelay = TimeSpan.FromSeconds(seconds * Jitter());
        return servedDelay;
    }

    /// <summary>Puts the next wait back to nothing, after a connect that worked.</summary>
    public void Reset()
    {
        attempts = 0;
        servedDelay = TimeSpan.Zero;
    }

    /// <summary>
    /// Clears the debt for a connection that <b>held</b>, and leaves it alone for
    /// one that merely arrived.
    ///
    /// <para>
    /// <b>The yardstick is twice the wait this attempt actually served</b> —
    /// twice the floor when it served none, which is every attempt that follows
    /// a reset. It used to be the floor itself, and a constant yardstick is
    /// exactly what a takeover defeats: two clients sharing a fixed client id
    /// take the session off each other at roughly one another's reconnect
    /// period, and that period converges on a little <em>above</em> the floor,
    /// so every cycle cleared the backoff and the flap ran at full speed for as
    /// long as the other client was there — the very thing this guard was
    /// written to prevent.
    /// </para>
    ///
    /// <para>
    /// Measuring against the served wait alone is not enough either, because the
    /// wait is jittered over [0.8, 1.2]: a peer holding at 1.1 × the floor
    /// outlasts three quarters of that band. Doubling puts the yardstick clear
    /// of the whole band, so a takeover run escalates to the cap instead of
    /// resetting, while a connection that genuinely outlives two of its own
    /// waits still clears the debt.
    /// </para>
    /// </summary>
    public void ResetIfHeld(TimeSpan held)
    {
        TimeSpan yardstick = (servedDelay > TimeSpan.Zero ? servedDelay : first) * 2;

        if (held >= yardstick)
        {
            Reset();
        }
    }

    // Not a security decision: this de-synchronises retries between clients, it
    // does not generate anything anyone could guess their way into.
#pragma warning disable CA5394
    private static double Jitter() => 0.8 + (Random.Shared.NextDouble() * 0.4);
#pragma warning restore CA5394
}
