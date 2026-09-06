using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace SmartSentinelEye.ServiceDefaults.Tests;

/// <summary>
/// What probe surface <c>MapDefaultEndpoints</c> leaves behind, per environment.
///
/// <para>
/// One method decides this for ten hosts — the gateway and all nine context
/// APIs — so the environment it is given is the only variable worth moving.
/// The host is built in process: no Docker, no Kubernetes, no socket. That is
/// not a convenience, it is the only way to observe the thing at all. Nothing
/// in <c>AppHost</c> or <c>Integration.Tests</c> sets
/// <c>ASPNETCORE_ENVIRONMENT</c>, so every fixture-booted service runs in
/// Development, where the endpoints are already mapped; an integration test
/// would come back green and prove nothing.
/// </para>
///
/// <para>
/// Most assertions read the route table, because whether the mapping happens
/// is the entire delta. The three that call the endpoint do so because the
/// route table cannot answer them: <c>MapHealthChecks</c> captures its
/// <c>HealthCheckOptions</c> inside the middleware and publishes none of it as
/// endpoint metadata, so the <c>live</c> predicate and the response writer are
/// invisible from outside. Each of those has a Development twin that passes
/// today — a red that only proves the harness is broken proves nothing.
/// </para>
///
/// <para>
/// The in-process host pattern is lifted from
/// <c>tests/Architecture.Tests/SystemVariableReadScopeTests.cs</c>.
/// </para>
/// </summary>
public class DefaultEndpointsTests
{
    private const string HealthPath = "/health";

    private const string AlivenessPath = "/alive";

    /// <summary>
    /// The failing <c>ready</c> check carries a payload shaped like the real
    /// <c>outbox-{module}</c> one — <c>OutboxBacklogHealthCheck</c>'s own
    /// description and its <c>pending</c>/<c>maxAttempts</c>/<c>schema</c>
    /// entries — because the two exact-string body assertions are the only
    /// automated guard on the spec's security argument, and a hollow
    /// <c>new HealthCheckResult(status)</c> cannot carry it: a
    /// <c>ResponseWriter</c> that rendered check names, descriptions and
    /// <c>data</c> would emit the same single word for a check that has none,
    /// and both tests would stay green while the guarantee was gone.
    /// </summary>
    private const string ReadyCheckDescription =
        "4211 announcement(s) waiting; most-retried has failed 9 time(s).";

    private static readonly IReadOnlyDictionary<string, object> ReadyCheckData =
        new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["pending"] = 4211L,
            ["maxAttempts"] = 9,
            ["schema"] = "wolverine_camera_catalog",
        };

    [Fact]
    public void A_production_host_maps_the_readiness_probe()
    {
        using WebApplication app = ProbeHost(Environments.Production);

        RoutesOf(app).ShouldContain(HealthPath);
    }

    [Fact]
    public void A_production_host_maps_the_liveness_probe()
    {
        using WebApplication app = ProbeHost(Environments.Production);

        RoutesOf(app).ShouldContain(AlivenessPath);
    }

    [Fact]
    public void A_development_host_maps_both_probes()
    {
        using WebApplication app = ProbeHost(Environments.Development);

        RoutesOf(app).ShouldBe([HealthPath, AlivenessPath], ignoreOrder: true);
    }

    [Fact]
    public void An_environment_that_is_neither_development_nor_production_maps_both_probes()
    {
        using WebApplication app = ProbeHost(Environments.Staging);

        RoutesOf(app).ShouldBe([HealthPath, AlivenessPath], ignoreOrder: true);
    }

    [Fact]
    public void A_production_liveness_probe_carries_no_authorization_metadata()
    {
        using WebApplication app = ProbeHost(Environments.Production);

        AuthorizationOn(app, AlivenessPath).ShouldBeEmpty(
            "a kubelet cannot present a bearer token, so the probes are anonymous by design. "
            + "This fails if someone puts them behind authentication — which FR-006 places "
            + "outside this change and which would need an ADR of its own.");
    }

    [Fact]
    public void A_production_readiness_probe_carries_no_authorization_metadata()
    {
        using WebApplication app = ProbeHost(Environments.Production);

        AuthorizationOn(app, HealthPath).ShouldBeEmpty(
            "the gateway forwards /{context}/health unauthenticated for all nine contexts, "
            + "and GatewayRoutingIntegrationTests depends on exactly that.");
    }

    [Fact]
    public void A_development_readiness_probe_carries_no_authorization_metadata()
    {
        using WebApplication app = ProbeHost(Environments.Development);

        AuthorizationOn(app, HealthPath).ShouldBeEmpty();
    }

    /// <summary>
    /// The <c>live</c> predicate, observed rather than read. A ready-tagged
    /// check is failed on purpose so the two endpoints can disagree: the real
    /// <c>outbox-{module}</c> check registers <c>failureStatus: Degraded</c>,
    /// which answers 200 exactly as Healthy does and so would prove nothing
    /// about which checks ran.
    /// </summary>
    [Fact]
    public async Task A_development_liveness_probe_ignores_a_failing_ready_tagged_check()
    {
        await using WebApplication app = ProbeHost(Environments.Development, withFailingReadyCheck: true);

        (int status, string body) = await CallAsync(app, AlivenessPath);

        status.ShouldBe(StatusCodes.Status200OK);
        body.ShouldBe("Healthy");
    }

    /// <summary>
    /// The other half of that contrast, and the spec's security argument in one
    /// assertion: no <c>ResponseWriter</c> is set anywhere in the chain, so the
    /// framework default writes the aggregate status and nothing else — no
    /// check names, no descriptions, and not the <c>data</c> dictionary, which
    /// does carry the outbox schema name and its pending counts.
    ///
    /// <para>
    /// The check is failed with the payload a real one carries
    /// (<see cref="ReadyCheckDescription"/>, <see cref="ReadyCheckData"/>), so
    /// "one word" is a claim about the writer and not about an empty result.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_development_readiness_probe_reports_a_failing_ready_tagged_check_as_one_word()
    {
        await using WebApplication app = ProbeHost(Environments.Development, withFailingReadyCheck: true);

        (int status, string body) = await CallAsync(app, HealthPath);

        status.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        body.ShouldBe("Unhealthy");
    }

    [Fact]
    public async Task A_production_liveness_probe_ignores_a_failing_ready_tagged_check()
    {
        await using WebApplication app = ProbeHost(Environments.Production, withFailingReadyCheck: true);

        (int status, string body) = await CallAsync(app, AlivenessPath);

        status.ShouldBe(StatusCodes.Status200OK);
        body.ShouldBe("Healthy");
    }

    /// <summary>
    /// The premise the Production mapping rests on, driven rather than asserted
    /// in prose. The real <c>outbox-{module}</c> check registers
    /// <c>failureStatus: Degraded</c> (<c>WolverineDefaults</c>), and the
    /// framework default maps Degraded to 200. Were it 503, a genuine outbox
    /// backlog — a condition this system deliberately calls non-fatal — would
    /// fail the readiness probe on every replica at once, and Kubernetes would
    /// take the whole service out of rotation for it.
    ///
    /// <para>
    /// Production rather than Development, though the mapping itself is
    /// environment-independent: only in Production is <c>/health</c> newly
    /// reachable and read by a kubelet, so only there does the consequence
    /// exist — and no other test in this file invokes a Production
    /// <c>/health</c> at all.
    /// </para>
    ///
    /// <para>
    /// The body is asserted as an exact word for the same reason as its
    /// Development sibling: a check name, a description or a <c>data</c> entry
    /// reaching the payload fails this — the check carries all three
    /// (<see cref="ReadyCheckDescription"/>, <see cref="ReadyCheckData"/>), so
    /// there is something for a detailed writer to leak.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_production_readiness_probe_answers_two_hundred_for_a_degraded_ready_tagged_check()
    {
        await using WebApplication app = ProbeHost(
            Environments.Production,
            withFailingReadyCheck: true,
            readyCheckFailureStatus: HealthStatus.Degraded);

        (int status, string body) = await CallAsync(app, HealthPath);

        status.ShouldBe(StatusCodes.Status200OK);
        body.ShouldBe("Degraded");
    }

    private static WebApplication ProbeHost(
        string environmentName,
        bool withFailingReadyCheck = false,
        HealthStatus readyCheckFailureStatus = HealthStatus.Unhealthy)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(
            new WebApplicationOptions { EnvironmentName = environmentName });

        builder.AddDefaultHealthChecks();

        if (withFailingReadyCheck)
        {
            builder.Services.AddHealthChecks()
                .AddCheck(
                    "probe-ready",
                    () => new HealthCheckResult(
                        readyCheckFailureStatus,
                        ReadyCheckDescription,
                        exception: null,
                        ReadyCheckData),
                    ["ready"]);
        }

        WebApplication app = builder.Build();
        app.MapDefaultEndpoints();

        // WebApplicationOptions is applied after the ASPNETCORE_* configuration
        // source, so an ambient ASPNETCORE_ENVIRONMENT does not win here. Said
        // out loud because if it ever did, every assertion in this file would
        // quietly be testing an environment nobody asked for.
        if (!string.Equals(app.Environment.EnvironmentName, environmentName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Asked for environment '{environmentName}' but the host built '{app.Environment.EnvironmentName}'.");
        }

        return app;
    }

    private static IReadOnlyList<string> RoutesOf(WebApplication app) =>
        [.. ProbeEndpoints(app).Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)];

    private static IReadOnlyList<IAuthorizeData> AuthorizationOn(WebApplication app, string route) =>
        [.. EndpointFor(app, route).Metadata.OfType<IAuthorizeData>()];

    private static IEnumerable<RouteEndpoint> ProbeEndpoints(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>();

    private static RouteEndpoint EndpointFor(WebApplication app, string route) =>
        ProbeEndpoints(app)
            .FirstOrDefault(endpoint => string.Equals(endpoint.RoutePattern.RawText, route, StringComparison.Ordinal))
        ?? throw new InvalidOperationException(
            $"MapDefaultEndpoints registered no {route} in the {app.Environment.EnvironmentName} environment. "
            + $"Registered: {string.Join(", ", RoutesOf(app).DefaultIfEmpty("(none)"))}.");

    /// <summary>
    /// Drives the mapped endpoint's own pipeline in process. No
    /// <c>Microsoft.AspNetCore.TestHost</c> and no socket — the endpoint's
    /// <c>RequestDelegate</c> is the health-check middleware, and that
    /// middleware is terminal, so invoking it directly is the whole request.
    /// </summary>
    private static async Task<(int Status, string Body)> CallAsync(WebApplication app, string route)
    {
        RouteEndpoint endpoint = EndpointFor(app, route);

        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        using MemoryStream body = new();
        DefaultHttpContext context = new() { RequestServices = scope.ServiceProvider };
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = route;
        context.Response.Body = body;

        RequestDelegate handler = endpoint.RequestDelegate
            ?? throw new InvalidOperationException($"{route} is mapped but carries no request delegate.");

        await handler(context);

        return (context.Response.StatusCode, Encoding.UTF8.GetString(body.ToArray()));
    }
}
