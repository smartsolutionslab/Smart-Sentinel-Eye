using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using SmartSentinelEye.EventIngestion.Application.Commands;
using SmartSentinelEye.EventIngestion.Application.Commands.Handlers;
using SmartSentinelEye.EventIngestion.Application.EventHandlers;
using SmartSentinelEye.EventIngestion.Application.Ingress;
using SmartSentinelEye.EventIngestion.Application.Queries;
using SmartSentinelEye.EventIngestion.Application.Queries.Handlers;
using SmartSentinelEye.EventIngestion.Domain.DeadLetter;
using SmartSentinelEye.EventIngestion.Domain.Event;
using SmartSentinelEye.EventIngestion.Domain.Event.Events;
using SmartSentinelEye.EventIngestion.Domain.WebhookIntegration;
using SmartSentinelEye.EventIngestion.Infrastructure.Ingress;
using SmartSentinelEye.EventIngestion.Infrastructure.Persistence;
using SmartSentinelEye.ServiceDefaults;
using SmartSentinelEye.ServiceDefaults.Idempotency;
using SmartSentinelEye.ServiceDefaults.Resilience;
using SmartSentinelEye.Shared.CQRS;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.EventIngestion.Infrastructure;

/// <summary>
/// Composition root for the EventIngestion Infrastructure layer
/// (ADR-0051). Wires the persistence stack, the bounded channel, the
/// MQTT subscriber, the persistence loop, and the Wolverine outbox.
/// </summary>
public static class EventIngestionInfrastructureModule
{
    public const string ContextName = "event-ingestion";
    public const string OutboxSchema = "wolverine_event_ingestion";

    public static IHostApplicationBuilder AddEventIngestionInfrastructure(this IHostApplicationBuilder builder)
    {
        Ensure.That(builder).IsNotNull();

        builder.AddEventIngestionPersistence();

        builder.Services.AddScoped<IEventRepository, EventRepository>();
        builder.Services.AddScoped<IWebhookIntegrationRepository, WebhookIntegrationRepository>();
        builder.Services.AddScoped<IDeadLetterRepository, DeadLetterRepository>();
        builder.Services.AddScoped<IEventQuerySource, EventQuerySource>();
        builder.Services.AddScoped<IDeadLetterQuerySource, DeadLetterQuerySource>();
        builder.Services.AddScoped<IWebhookIntegrationQuerySource, WebhookIntegrationQuerySource>();
        builder.Services.AddScoped<IDomainEventDispatcher, DomainEventDispatcher>();
        builder.Services.AddSingleton<IClock, SystemClock>();

        // ADR-0142. Scoped alongside the DbContext it writes through.
        builder.Services.AddScoped<IIdempotencyStore, IdempotencyStore<EventIngestionDbContext>>();
        builder.Services.AddSingleton(TimeProvider.System);

        // Spec 019: the write paths ask whether a fab can store anything before
        // touching the ingest channel. Registered here rather than in the
        // persistence module because MigrationRunner composes that one too and
        // has no use for it — and, registered there, it would need an IClock
        // that only the services register.
        //
        // Singleton so the provisioned-fab set is shared: read from the catalog
        // once per TTL, not once per request.
        builder.Services.AddSingleton<IFabStorageReadiness, CatalogFabStorageReadiness>();

        // Domain event handler — translates EventIngestedDomainEvent
        // into FabEventIngestedV1 on the integration bus.
        builder.Services.AddScoped<
            IDomainEventHandler<EventIngestedDomainEvent>,
            EventIngestedDomainEventHandler>();

        // Hand-rolled command handler registrations (ADR-0042 + ADR-0057).
        builder.Services.AddScoped<IngestEventCommandHandler>();
        builder.Services.AddScoped<IngestEventBatchCommandHandler>();
        builder.Services.AddScoped<
            ICommandHandler<IngestEventCommand, Result<EventIdentifier, IngestEventError>>,
            IngestEventCommandHandler>();
        builder.Services.AddScoped<RegisterWebhookIntegrationCommandHandler>();
        builder.Services.AddScoped<
            ICommandHandler<
                RegisterWebhookIntegrationCommand,
                Result<RegisterWebhookIntegrationResult, RegisterWebhookIntegrationError>>,
            RegisterWebhookIntegrationCommandHandler>();
        builder.Services.AddScoped<RevokeWebhookIntegrationCommandHandler>();
        builder.Services.AddScoped<
            ICommandHandler<
                RevokeWebhookIntegrationCommand,
                Result<WebhookIntegrationIdentifier, RevokeWebhookIntegrationError>>,
            RevokeWebhookIntegrationCommandHandler>();

        // Query handlers.
        builder.Services.AddScoped<GetEventQueryHandler>();
        builder.Services.AddScoped<ListEventsQueryHandler>();
        builder.Services.AddScoped<ListDeadLettersQueryHandler>();
        builder.Services.AddScoped<ListWebhookIntegrationsQueryHandler>();

        // Bounded channel + ingress.
        builder.Services.AddSingleton<IIngestChannel>(_ => new BoundedIngestChannel());

        // Spec 020: direct submissions store before answering, so they no longer
        // pass through the channel — and the 429 that used to mean "the channel
        // is full" needs something to bound. This is it.
        builder.Services.AddSingleton<IngestWriteLimiter>();

        // Spec 103 FR-004. A meter nobody registers records into nothing and
        // raises no error, so this line is the whole difference between an
        // instrument and a no-op — and no unit test can tell them apart.
        //
        // Here rather than beside the other three meters in ServiceDefaults:
        // that project is referenced by every context and references none of
        // them, so it cannot see IngestVolume.MeterName. Registering it there
        // would mean the same string in two projects, which is how a rename
        // silently unregisters a meter. ADR-0051 puts a context's own wiring in
        // the context's own DI extension anyway.
        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics.AddMeter(IngestVolume.MeterName));

        // FR-005: how long a failing write keeps being retried before the
        // delivery is recorded and released. Bound out here rather than in the
        // loop because the right answer is how long a plant's outages last.
        builder.Services.AddOptions<IngestRetryOptions>()
            .Bind(builder.Configuration.GetSection(IngestRetryOptions.SectionName));
        // Resolve the broker + Keycloak from the Aspire-injected endpoints, and
        // fail fast when they are absent. Defaulting these (it used to be
        // localhost:1883) turns a wiring gap into a subscriber that silently
        // retries a dead address forever while /health stays green.
        (string mqttHost, int mqttPort) = ResolveBroker(builder.Configuration);
        string keycloakUrl = ResolveKeycloak(builder.Configuration);

        builder.Services.AddOptions<MosquittoOptions>()
            .Bind(builder.Configuration.GetSection(MosquittoOptions.SectionName))
            .PostConfigure(opts =>
            {
                opts.Host = mqttHost;
                opts.Port = mqttPort;
                opts.KeycloakUrl = keycloakUrl;
            });
        // Singleton over a named client (#2037): the cache is only worth having if
        // the provider outlives a request, and asking the factory per mint keeps
        // handler rotation that a captive typed client would have pinned.
        // A token mint is a POST, but an idempotent one: a second token simply
        // supersedes the first. Losing its retries would make a transient
        // Keycloak blip at startup fail the broker connection outright (ADR-0143).
        builder.Services.AddHttpClient(MqttTokenProvider.HttpClientName).RetryEveryMethod();
        builder.Services.AddSingleton<MqttTokenProvider>();
        builder.Services.AddSingleton<MosquittoConnectionFactory>();
        builder.Services.AddHostedService<MqttSubscriberHostedService>();
        builder.Services.AddHostedService<PersistenceLoopHostedService>();

        builder.AddWolverineForContext<EventIngestionDbContext>(
            moduleQueuePrefix: ContextName,
            outboxSchema: OutboxSchema,
            postgresConnectionName: EventIngestionPersistenceModule.DatabaseConnectionName);

        return builder;
    }

    /// <summary>
    /// Broker host + port from the Aspire mosquitto endpoint, e.g.
    /// <c>tcp://localhost:52643</c>. Throws rather than defaulting: the
    /// managed MQTT client retries a bad address indefinitely without
    /// surfacing anything, so a wrong value is far more expensive than a
    /// failed startup.
    /// </summary>
    private static (string Host, int Port) ResolveBroker(IConfiguration configuration)
    {
        string endpoint =
            configuration["services:mosquitto:mqtt:0"]
            ?? configuration[$"{MosquittoOptions.SectionName}:Endpoint"]
            ?? throw new InvalidOperationException(
                "Mosquitto endpoint not found. Looked for services:mosquitto:mqtt:0 and "
                + $"{MosquittoOptions.SectionName}:Endpoint.");

        string value = endpoint;
        int scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            value = value[(scheme + 3)..];
        }

        string[] parts = value.Split(':');
        if (parts.Length != 2 || parts[0].Length == 0 || !int.TryParse(parts[1], out int port))
        {
            throw new InvalidOperationException($"Mosquitto endpoint '{endpoint}' is not host:port.");
        }

        return (parts[0], port);
    }

    /// <summary>
    /// Keycloak base URL the subscriber mints its MQTT token from. Accepts
    /// the same three keys as <c>AddBearerAuthentication</c>, so the token
    /// issuer matches the JWKS the broker plugin validates against.
    /// </summary>
    private static string ResolveKeycloak(IConfiguration configuration) =>
        configuration.GetConnectionString("keycloak")
        ?? configuration["services:keycloak:http:0"]
        ?? configuration["services:keycloak:https:0"]
        ?? throw new InvalidOperationException(
            "Keycloak base URL not found. Looked for ConnectionStrings:keycloak, "
            + "services:keycloak:http:0, services:keycloak:https:0.");
}
