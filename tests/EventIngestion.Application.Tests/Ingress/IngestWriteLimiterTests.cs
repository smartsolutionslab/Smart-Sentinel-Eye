using SmartSentinelEye.EventIngestion.Application.Ingress;

namespace SmartSentinelEye.EventIngestion.Application.Tests.Ingress;

/// <summary>
/// Spec 104 (issue #625, half 1), characterising spec 020 FR-013 — the semaphore
/// that turns overload into <c>429 EVENT_INGEST_BACKPRESSURE</c>.
///
/// <para>
/// Two failure directions, two tests, deliberately. A limiter that never refuses
/// and a limiter that leaks its leases and refuses everything are opposite
/// defects, and a suite that only proves refusal happens is green on both a
/// working limiter and one that has jammed shut. The second is the one nobody
/// would notice until an endpoint started answering 429 for no visible reason.
/// </para>
///
/// <para>
/// Every test holds its slots explicitly rather than racing tasks. The count
/// <see cref="System.Threading.SemaphoreSlim"/> keeps is what is under test, and
/// a thread-timing dependency would trade that signal for flakiness. No xUnit
/// collection is needed either: the limiter is an instance, not a static meter.
/// </para>
/// </summary>
public class IngestWriteLimiterTests
{
    /// <summary>
    /// AS-1 — the "never rejects" direction. Refusing is what makes 429 mean
    /// something; without it the endpoint silently degrades to queueing behind
    /// the connection pool.
    ///
    /// <para>
    /// The scenario's "not left waiting" half has no assertion here on purpose:
    /// <c>TryAcquire</c> is synchronous, so the only way to express immediacy is
    /// a wall-clock bound, which is exactly the thread timing this file avoids.
    /// A limiter that waited without a timeout would hang this test rather than
    /// pass it.
    /// </para>
    /// </summary>
    [Fact]
    public void A_saturated_limiter_refuses_the_next_writer()
    {
        using IngestWriteLimiter limiter = new(concurrency: 2);

        IngestWriteLease first = limiter.TryAcquire();
        IngestWriteLease second = limiter.TryAcquire();
        first.Acquired.ShouldBeTrue();
        second.Acquired.ShouldBeTrue();

        IngestWriteLease third = limiter.TryAcquire();

        third.Acquired.ShouldBeFalse();
    }

    /// <summary>
    /// AS-2 — the "leaks leases" direction, and the half of the pair that a
    /// rejection-only suite misses. A slot handed back must become available
    /// again; a lease whose <c>Dispose</c> released nothing would leave the
    /// limiter permanently shut, and it is a DI singleton, so it would stay shut.
    /// </summary>
    [Fact]
    public void A_released_slot_is_handed_to_the_next_writer()
    {
        using IngestWriteLimiter limiter = new(concurrency: 2);

        IngestWriteLease held = limiter.TryAcquire();
        IngestWriteLease released = limiter.TryAcquire();
        held.Acquired.ShouldBeTrue();
        released.Acquired.ShouldBeTrue();
        limiter.TryAcquire().Acquired.ShouldBeFalse(
            "the limiter must be saturated first, or the release proves nothing");

        released.Dispose();

        limiter.TryAcquire().Acquired.ShouldBeTrue();
    }

    /// <summary>
    /// AS-3 — <c>StoreOrRefuseAsync</c> wraps the result of <c>TryAcquire</c> in
    /// a <c>using</c> before it checks <c>Acquired</c>, so a refused lease is
    /// disposed on every single 429. That is only harmless while
    /// <c>Refused</c> is <c>default</c> and its <c>Dispose</c> is a no-op; the
    /// day it stops being one, each refusal returns a slot it never took.
    /// </summary>
    [Fact]
    public void A_refused_lease_releases_nothing()
    {
        using IngestWriteLimiter limiter = new(concurrency: 1);

        IngestWriteLease held = limiter.TryAcquire();
        held.Acquired.ShouldBeTrue();

        IngestWriteLease refused = limiter.TryAcquire();
        refused.Acquired.ShouldBeFalse();

        refused.Dispose();

        limiter.TryAcquire().Acquired.ShouldBeFalse();
    }

    /// <summary>
    /// AS-4 — the parameterless constructor is the one DI registers, so 64 is
    /// the bound the endpoints actually run with. The constant is asserted as
    /// well as used, so lowering it silently is a failure rather than a loop
    /// that quietly counts to something else.
    /// </summary>
    [Fact]
    public void The_default_limiter_bounds_writes_at_sixty_four()
    {
        IngestWriteLimiter.DefaultConcurrency.ShouldBe(64);

        using IngestWriteLimiter limiter = new();

        List<IngestWriteLease> held = [];
        for (int writer = 0; writer < IngestWriteLimiter.DefaultConcurrency; writer++)
        {
            held.Add(limiter.TryAcquire());
        }

        held.ShouldAllBe(lease => lease.Acquired);
        limiter.TryAcquire().Acquired.ShouldBeFalse();
    }
}
