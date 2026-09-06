using SmartSentinelEye.EventIngestion.Infrastructure.Ingress;

namespace SmartSentinelEye.EventIngestion.Infrastructure.Tests;

/// <summary>
/// Spec 079 (#1139) — the production backoff shape, asserted where waiting for
/// it costs nothing. <c>MqttConnectionLoopTests</c> runs the loop on
/// millisecond delays because what it asserts is ordering; the seconds live
/// here.
/// </summary>
public class MqttBackoffTests
{
    /// <summary>
    /// The jitter band is [0.8, 1.2], so each delay is asserted as a range
    /// rather than a number. Asserting the midpoint would need the jitter
    /// removed, and a backoff without jitter is the thing this is not: at the
    /// 250-camera target every client that dropped together would retry
    /// together, making the recovery the second outage.
    /// </summary>
    [Fact]
    public void The_delay_doubles_from_one_second_and_caps_at_thirty()
    {
        MqttBackoff backoff = new();

        backoff.Next().ShouldBe(TimeSpan.Zero, "the first attempt is made immediately");

        double[] expected = [1, 2, 4, 8, 16, 30, 30, 30];
        foreach (double seconds in expected)
        {
            TimeSpan delay = backoff.Next();

            delay.TotalSeconds.ShouldBeInRange(
                seconds * 0.8,
                seconds * 1.2,
                $"expected roughly {seconds}s, jittered by a factor in [0.8, 1.2]");
        }
    }

    [Fact]
    public void A_reset_puts_the_next_wait_back_to_nothing()
    {
        MqttBackoff backoff = new();

        backoff.Next();
        backoff.Next();
        backoff.Next();
        backoff.Attempt.ShouldBe(3);

        backoff.Reset();

        backoff.Attempt.ShouldBe(0);
        backoff.Next().ShouldBe(
            TimeSpan.Zero,
            "a connect that worked clears the debt: the next drop reconnects at once rather than "
            + "inheriting the delay the outage before it had grown to.");
    }

    /// <summary>
    /// Growth is unbounded in attempts, not in delay. A loop that stopped after
    /// n attempts would turn go-auth's JWKS re-fetch after a broker restart
    /// (assumption A3) into an outage lasting until someone restarted the pod.
    /// </summary>
    [Fact]
    public void The_delay_stays_capped_however_long_the_outage_runs()
    {
        MqttBackoff backoff = new();

        for (int i = 0; i < 200; i++)
        {
            backoff.Next().TotalSeconds.ShouldBeLessThanOrEqualTo(30 * 1.2);
        }

        backoff.Attempt.ShouldBe(200);
    }
}
