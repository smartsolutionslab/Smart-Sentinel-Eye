using Microsoft.Extensions.Http.Resilience;
using Shouldly;
using Xunit;

namespace SmartSentinelEye.Integration.Tests.Fixtures;

/// <summary>
/// #2129 — what the integration suite's own HttpClients retry, observed by
/// counting the attempts a real pipeline makes rather than by reading the
/// registration back.
///
/// <para>
/// The suite is a caller like any other, and ADR-0143's narrowing lives inside
/// <c>AddServiceDefaults</c>, which no test project calls. So there is no
/// <c>RetryEveryMethod()</c> to grep for: the fixture registers the bare handler
/// with the library's own predicate, which reads the outcome and never the
/// method. That is why a <c>POST /devices/register</c> carrying a freshly
/// generated Guid v7 came back <c>409 DEVICE_ALREADY_REGISTERED</c> — the
/// second attempt collided with the Keycloak client the first one created.
/// </para>
///
/// <para>
/// The GET and PUT cases are not padding. They are the half of the assertion
/// that says the remedy must be a <b>narrowing</b> and not a disabling: a fix
/// that simply dropped the resilience handler would satisfy the POST cases
/// alone, and they also prove the client was configured at all rather than left
/// bare.
/// </para>
///
/// <para>
/// No stack, no Docker, no saturating burst — a bare
/// <see cref="ServiceCollection"/> and the same
/// <see cref="FixtureHttpClients.Configure"/> the fixture uses. The
/// load-dependent alternative cannot be a gate: <c>ci.yml:179</c> excludes
/// <c>Category=Measurement</c>, so the burst never precedes the affected test in
/// the job that runs it, and a timing-shaped red proves only that the machine
/// was slow.
/// </para>
/// </summary>
[Trait("Category", "FixtureLogic")]
public class FixtureRetryPolicyTests
{
    private const string Client = "fixture-probe";

    /// <summary>
    /// The whole point. A POST that reached the server and lost its response is
    /// indistinguishable from one that never arrived, so a retry can apply the
    /// effect twice.
    /// </summary>
    [Fact]
    public async Task A_failing_POST_from_a_fixture_client_is_attempted_once()
    {
        (HttpClient client, CountingHandler server) = Build();

        using HttpResponseMessage response = await client.PostAsync(
            "https://x.test/devices/register", new StringContent("{}"), CancellationToken.None);

        server.Attempts.ShouldBe(1, "a POST gets one attempt; retrying it can duplicate the effect.");
    }

    /// <summary>
    /// A transport failure has no response to read the method off, so the
    /// predicate falls back to the resilience context. Asserted separately
    /// because the fallback is the half that can regress silently.
    /// </summary>
    [Fact]
    public async Task A_thrown_POST_from_a_fixture_client_is_attempted_once()
    {
        (HttpClient client, CountingHandler server) = Build(throwInstead: true);

        await Should.ThrowAsync<HttpRequestException>(
            () => client.PostAsync("https://x.test/devices/register", new StringContent("{}"), CancellationToken.None));

        server.Attempts.ShouldBe(1, "a POST that never got an answer is still a POST.");
    }

    [Fact]
    public async Task A_failing_GET_from_a_fixture_client_is_still_retried()
    {
        (HttpClient client, CountingHandler server) = Build();

        using HttpResponseMessage response = await client.GetAsync("https://x.test/devices", CancellationToken.None);

        server.Attempts.ShouldBe(4, "one attempt plus the standard handler's three retries.");
    }

    [Fact]
    public async Task A_failing_PUT_from_a_fixture_client_is_still_retried()
    {
        (HttpClient client, CountingHandler server) = Build();

        using HttpResponseMessage response = await client.PutAsync(
            "https://x.test/devices/one", new StringContent("{}"), CancellationToken.None);

        server.Attempts.ShouldBe(4, "PUT is idempotent under RFC 9110, so the retry must survive the fix.");
    }

    private static (HttpClient Client, CountingHandler Server) Build(bool throwInstead = false)
    {
        CountingHandler server = new(throwInstead);
        ServiceCollection services = new();

        IHttpClientBuilder builder = services
            .AddHttpClient(Client)
            .ConfigurePrimaryHttpMessageHandler(() => server);

        FixtureHttpClients.Configure(builder);

        // Zeroed after the fixture's own configuration, so the later Configure on
        // the same named options wins — the ordering IdempotentRetry.RetryEveryMethod
        // already depends on. The real 2/4/8 s backoff would cost ~14 s per
        // four-attempt case; the attempt count is what these facts are about, and
        // it is the one thing the delay does not change.
        services.Configure<HttpStandardResilienceOptions>(
            $"{builder.Name}-standard",
            options =>
            {
                options.Retry.Delay = TimeSpan.Zero;
                options.Retry.UseJitter = false;
            });

        return (services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>().CreateClient(Client), server);
    }

    private sealed class CountingHandler(bool throwInstead) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;

            return throwInstead
                ? Task.FromException<HttpResponseMessage>(new HttpRequestException("transport failed"))
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
}
