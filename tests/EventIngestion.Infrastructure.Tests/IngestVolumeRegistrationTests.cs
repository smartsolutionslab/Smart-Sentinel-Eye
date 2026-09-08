using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using OpenTelemetry;
using OpenTelemetry.Metrics;

using SmartSentinelEye.EventIngestion.Application.Ingress;
using SmartSentinelEye.EventIngestion.Domain.Event;

namespace SmartSentinelEye.EventIngestion.Infrastructure.Tests;

/// <summary>
/// Spec 103 FR-004. <c>AddEventIngestionInfrastructure</c> must call
/// <c>.AddMeter(IngestVolume.MeterName)</c>; without that line the counter
/// records into nothing and raises no error.
///
/// <para>
/// Spec 103 §5 originally claimed a unit test <em>cannot</em> reach this. It
/// can: <c>MeterProvider</c>, <c>BaseExporter&lt;Metric&gt;</c> and
/// <c>BaseExportingMetricReader</c> are public SDK types that already flow in
/// transitively (Infrastructure → ServiceDefaults →
/// <c>OpenTelemetry.Extensions.Hosting</c>), so no package is added and FR-008
/// still holds. What a unit test still cannot prove is that the figure is
/// <b>readable in the sink</b> — that the OTLP exporter ships it and the Aspire
/// dashboard shows the three series. That is what §5's procedure is for.
/// </para>
///
/// <para>
/// <b>The cost, stated rather than discovered.</b> This test composes the real
/// module, so it couples to the five configuration keys the module resolves
/// (the two Postgres connections, RabbitMQ, Keycloak, the Mosquitto endpoint).
/// A change to any of those config shapes breaks this test in a way that reads
/// as unrelated to metrics. That is the trade: the failure is <em>loud and
/// named</em> instead of the silent one FR-004 exists for. Nothing here opens a
/// connection — only <see cref="MeterProvider"/> is resolved, the host is never
/// started, and no hosted service runs.
/// </para>
/// </summary>
public class IngestVolumeRegistrationTests
{
    [Fact]
    public void The_ingest_volume_meter_is_registered_with_the_meter_provider()
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection(Configuration);

        builder.AddEventIngestionInfrastructure();

        CapturingExporter exporter = new();
        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics.AddReader(new BaseExportingMetricReader(exporter)));

        using ServiceProvider provider = builder.Services.BuildServiceProvider();
        MeterProvider meterProvider = provider.GetRequiredService<MeterProvider>();

        IngestVolume.Record(Source.Plc);
        meterProvider.ForceFlush();

        exporter.Names.ShouldContain(
            IngestVolume.MetricName,
            $"{IngestVolume.MeterName} is not registered, so {IngestVolume.MetricName} records into nothing");
    }

    /// <summary>
    /// The keys the module resolves before it returns. Values are syntactically
    /// valid and never dialled: the module parses them, it does not connect.
    /// </summary>
    private static Dictionary<string, string?> Configuration =>
        new()
        {
            ["ConnectionStrings:event-ingestion-db"] = "Host=localhost;Database=x;Username=u;Password=p",
            ["ConnectionStrings:rabbitmq"] = "amqp://guest:guest@localhost:5672",
            ["ConnectionStrings:messaging"] = "amqp://guest:guest@localhost:5672",
            ["ConnectionStrings:keycloak"] = "http://localhost:8080",
            ["Mosquitto:Endpoint"] = "localhost:1883",
        };

    /// <summary>
    /// Records the names of the metrics the provider exports. The SDK only ever
    /// hands an exporter instruments whose meter is registered, so the presence
    /// of the name <em>is</em> the assertion.
    /// </summary>
    private sealed class CapturingExporter : BaseExporter<Metric>
    {
        public List<string> Names { get; } = [];

        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (Metric metric in batch)
            {
                Names.Add(metric.Name);
            }

            return ExportResult.Success;
        }
    }
}
