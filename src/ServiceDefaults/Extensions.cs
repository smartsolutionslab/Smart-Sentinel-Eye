using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ServiceDiscovery;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using SmartSentinelEye.ServiceDefaults;
using SmartSentinelEye.ServiceDefaults.Resilience;
using Wolverine.Runtime;

namespace Microsoft.Extensions.Hosting;

// Adds common Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
// This project should be referenced by each service project in your solution.
// To learn more about using this project, see https://aka.ms/aspire/service-defaults
public static class Extensions
{
    private const string HealthEndpointPath = "/health";

    private const string AlivenessEndpointPath = "/alive";

    /// <summary>
    /// Wolverine's own <c>ActivitySource</c>, taken from Wolverine rather than
    /// spelled out. The obvious form is <c>AddSource("Wolverine")</c>, and its
    /// failure mode is the reason this reads the name off the source instead: a
    /// typo registers nothing, raises nothing, and leaves a journey that looks
    /// exactly as untraced as it did before — silence that cannot be told from
    /// working. Reading it off the object makes a rename a build error.
    /// </summary>
    private static readonly string WolverineActivitySource =
        WolverineTracing.ActivitySource.Name;

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default, retrying only methods a retry
            // cannot damage (ADR-0143). The handler's own predicate reads the
            // outcome and never the method, so a POST was retried exactly like a
            // GET — and a POST whose response was lost is indistinguishable from
            // one that never arrived, which is how a caller came to be told its
            // own successful registration was a conflict (#2039).
            http.AddStandardResilienceHandler(IdempotentRetry.RetryIdempotentMethodsOnly);

            // Turn on service discovery by default
            http.AddServiceDiscovery();
        });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()

                    // Without this the latency instrument records into nothing
                    // and says nothing about it — the same silence an
                    // unregistered trace source produces (spec 024, §VII).
                    .AddMeter(LatencyBudget.MeterName)
                    // Spec 045: a wall's skew is a spread, not a duration, so it
                    // rides its own meter rather than the latency segments.
                    .AddMeter(WallSkew.MeterName)
                    // Spec 046: a label delay is a duration, but one the kiosk
                    // chose to add rather than one a frame spent travelling.
                    .AddMeter(LabelDelay.MeterName);
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    // Without this the event-to-overlay journey is invisible.
                    // The two sources below cover inbound and outbound HTTP,
                    // which is the part of this system that is *not* on that
                    // path: every hop of it — publish, broker transit, handler
                    // execution — travels by message. Constitution §VII makes a
                    // dashboard mandatory for a budgeted leg, and the ≤ 200 ms
                    // event → overlay leg had neither dashboard nor spans
                    // through six features (spec 023, #1655).
                    .AddSource(WolverineActivitySource)
                    .AddAspNetCoreInstrumentation(tracing =>
                        // Exclude health check requests from tracing
                        tracing.Filter = context =>
                            !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                            && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath)
                    )
                    // Uncomment the following line to enable gRPC instrumentation (requires the OpenTelemetry.Instrumentation.GrpcNetClient package)
                    //.AddGrpcClientInstrumentation()
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static void AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        bool useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            // Add a default liveness check to ensure app is responsive
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Mapped in every environment, production included, because a probe an
        // orchestrator cannot reach is not a probe. What that exposes, and to
        // anyone who can reach the port — a kubelet presents no credentials, so
        // these are anonymous by design:
        //
        //   - "self", tag "live", returns Healthy and touches nothing.
        //   - "outbox-{module}", tag "ready", one Postgres query.
        //
        // No ResponseWriter is set here or at any call site, so the framework
        // default writes a single word — Healthy, Degraded or Unhealthy — and
        // never the check names, their descriptions, or the data dictionary.
        // That last part is load-bearing: outbox-{module}'s data carries the
        // outbox schema name and its pending and attempt counts, and the default
        // writer is the only thing keeping them off the wire.
        //
        // So this departure from the template's Development-only default is
        // conditional on both halves staying true. A ResponseWriter that renders
        // detail, or a check whose *name* encodes a dependency, turns an
        // aggregate word into an unauthenticated inventory of the system's
        // internals, and the analysis has to be redone before it ships.
        // See https://aka.ms/aspire/healthchecks.

        // All health checks must pass for app to be considered ready to accept traffic after starting
        app.MapHealthChecks(HealthEndpointPath);

        // Only health checks tagged with the "live" tag must pass for app to be considered alive
        app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("live")
        });

        return app;
    }
}
