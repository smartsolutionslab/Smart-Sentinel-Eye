using SmartSentinelEye.ServiceDefaults.Resilience;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.Integration.Tests.Fixtures;

/// <summary>
/// The client defaults every <see cref="HttpClient"/> the fixture hands out is
/// built with.
///
/// <para>
/// Extracted from <see cref="AspireFixture"/> so that what the suite's clients
/// are configured with can be constructed — and therefore asserted on — without
/// booting the stack. As an anonymous lambda inside <c>InitializeAsync</c> it was
/// reachable only by starting every container, which is why the wiring itself
/// had no cheap test.
/// </para>
/// </summary>
internal static class FixtureHttpClients
{
    internal static void Configure(IHttpClientBuilder http)
    {
        Ensure.That(http).IsNotNull();

        // ADR-0143's narrowing lives inside AddServiceDefaults, which no test
        // project calls. Unless the fixture applies it here, its clients keep the
        // library's own predicate, which reads the outcome and never the method —
        // so a POST is retried exactly like a GET (#2129).
        http.AddStandardResilienceHandler(IdempotentRetry.RetryIdempotentMethodsOnly);
    }
}
