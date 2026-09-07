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

        http.AddStandardResilienceHandler();
    }
}
