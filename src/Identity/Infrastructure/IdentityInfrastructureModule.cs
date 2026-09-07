using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartSentinelEye.Identity.Application.Commands;
using SmartSentinelEye.Identity.Application.Commands.Handlers;
using SmartSentinelEye.Identity.Application.DTOs;
using SmartSentinelEye.Identity.Application.EventHandlers;
using SmartSentinelEye.Identity.Application.KeycloakAdmin;
using SmartSentinelEye.Identity.Application.Queries;
using SmartSentinelEye.Identity.Application.Queries.Handlers;
using SmartSentinelEye.Identity.Domain.RegisteredClient;
using SmartSentinelEye.Identity.Domain.RegisteredClient.Events;
using SmartSentinelEye.Identity.Infrastructure.KeycloakAdmin;
using SmartSentinelEye.Identity.Infrastructure.Persistence;
using SmartSentinelEye.ServiceDefaults;
using SmartSentinelEye.ServiceDefaults.Idempotency;
using SmartSentinelEye.ServiceDefaults.Resilience;
using SmartSentinelEye.Shared.CQRS;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.Identity.Infrastructure;

/// <summary>
/// Composition root for the Identity Infrastructure layer
/// (ADR-0051). Wires persistence, Keycloak admin REST client,
/// command handlers, and the Wolverine outbox.
/// </summary>
public static class IdentityInfrastructureModule
{
    public const string ContextName = "identity";
    public const string OutboxSchema = "wolverine_identity";
    public const string KeycloakResourceName = "keycloak";

    public static IHostApplicationBuilder AddIdentityInfrastructure(this IHostApplicationBuilder builder)
    {
        Ensure.That(builder).IsNotNull();

        builder.AddIdentityPersistence();

        builder.Services.AddScoped<IRegisteredClientRepository, RegisteredClientRepository>();
        builder.Services.AddScoped<IRegisteredClientQuerySource, RegisteredClientQuerySource>();
        builder.Services.AddScoped<IDomainEventDispatcher, DomainEventDispatcher>();
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton(TimeProvider.System);

        builder.AddKeycloakAdminClient();

        // Spec 092 / ADR-0134 Decision 1, which names a startup sweep and had
        // nothing behind it. Scoped, because the pass takes the scoped
        // IKeycloakAdminClient; the startup service is a singleton and resolves
        // it through a scope rather than taking it.
        //
        // Here and deliberately not in AddKeycloakAdminClient, which
        // MigrationRunner also calls: MigrationRunner runs to completion and
        // exits, and a sweep there would race the migration it exists to run.
        builder.Services.AddScoped<KioskPrivilegeSweep>();
        builder.Services.AddHostedService<KioskPrivilegeSweepHostedService>();

        // Domain event handler — fans out DeviceRegisteredV1 /
        // KioskEnrolledV1.
        builder.Services.AddScoped<
            IDomainEventHandler<ClientRegisteredDomainEvent>,
            ClientRegisteredDomainEventHandler>();

        // Hand-rolled command handler registrations.
        builder.Services.AddScoped<EnrollKioskCommandHandler>();
        builder.Services.AddScoped<
            ICommandHandler<EnrollKioskCommand, Result<KioskCredentialsDto, EnrollKioskError>>,
            EnrollKioskCommandHandler>();
        builder.Services.AddScoped<DisableKioskCommandHandler>();
        builder.Services.AddScoped<
            ICommandHandler<DisableKioskCommand, Result<RegisteredClientIdentifier, DisableKioskError>>,
            DisableKioskCommandHandler>();
        builder.Services.AddScoped<RegisterDeviceCommandHandler>();
        builder.Services.AddScoped<
            ICommandHandler<RegisterDeviceCommand, Result<DeviceCredentialsDto, RegisterDeviceError>>,
            RegisterDeviceCommandHandler>();
        builder.Services.AddScoped<DisableDeviceCommandHandler>();
        builder.Services.AddScoped<
            ICommandHandler<DisableDeviceCommand, Result<RegisteredClientIdentifier, DisableDeviceError>>,
            DisableDeviceCommandHandler>();
        builder.Services.AddScoped<RotateWebhookClientCommandHandler>();
        builder.Services.AddScoped<
            ICommandHandler<
                RotateWebhookClientCommand,
                Result<WebhookClientCredentialsDto, RotateWebhookClientError>>,
            RotateWebhookClientCommandHandler>();

        // Hand-rolled query handler registrations (read side; issues #826/#827).
        builder.Services.AddScoped<ListDevicesQueryHandler>();

        // ADR-0142. Scoped alongside the DbContext it writes through; the store is
        // per context because there is no shared database to hold one table in.
        builder.Services.AddScoped<IIdempotencyStore, IdempotencyStore<IdentityDbContext>>();
        builder.Services.AddScoped<ReplayRegisteredClientQueryHandler>();
        builder.Services.AddScoped<
            IQueryHandler<ListDevicesQuery, Result<IReadOnlyList<RegisteredClientSummaryDto>, ListClientsError>>,
            ListDevicesQueryHandler>();
        builder.Services.AddScoped<ListKiosksQueryHandler>();
        builder.Services.AddScoped<
            IQueryHandler<ListKiosksQuery, Result<IReadOnlyList<RegisteredClientSummaryDto>, ListClientsError>>,
            ListKiosksQueryHandler>();
        builder.Services.AddScoped<ListWebhookClientsQueryHandler>();
        builder.Services.AddScoped<
            IQueryHandler<ListWebhookClientsQuery, Result<IReadOnlyList<RegisteredClientSummaryDto>, ListClientsError>>,
            ListWebhookClientsQueryHandler>();

        builder.AddWolverineForContext<IdentityDbContext>(
            moduleQueuePrefix: ContextName,
            outboxSchema: OutboxSchema,
            postgresConnectionName: IdentityPersistenceModule.DatabaseConnectionName);

        return builder;
    }

    /// <summary>
    /// Registers the Keycloak Admin REST client on its own, without the rest of
    /// Identity's infrastructure. The base URL comes from the Aspire-injected
    /// <c>ConnectionStrings:keycloak</c>; the client id and secret are
    /// configuration values, so a host can present a different service account
    /// than Identity's own.
    ///
    /// <para>
    /// Public because <c>MigrationRunner</c> composes it (spec 019): event
    /// partitions are provisioned per fab, and the fabs come from the realm's
    /// group tree. It presents the narrower <c>migration-runner</c> credential,
    /// which holds <c>query-groups</c> and <c>view-users</c> — read-only, and
    /// narrower than Identity's own credential. This is a composition
    /// root reusing Identity's client — not a bounded context reaching into
    /// another, which <c>BoundaryTests</c> forbids and would catch.
    /// </para>
    /// </summary>
    public static IHostApplicationBuilder AddKeycloakAdminClient(this IHostApplicationBuilder builder)
    {
        Ensure.That(builder).IsNotNull();

        builder.Services.AddOptions<KeycloakAdminOptions>()
            .Bind(builder.Configuration.GetSection(KeycloakAdminOptions.SectionName))
            .Configure<IConfiguration>((opts, config) =>
            {
                opts.BaseUrl = config.GetConnectionString(KeycloakResourceName)
                    ?? config[$"services:{KeycloakResourceName}:http:0"]
                    ?? config[$"services:{KeycloakResourceName}:https:0"]
                    ?? throw new InvalidOperationException(
                        "Keycloak base URL not found; expected ConnectionStrings:keycloak or services:keycloak:*.");
            });

        // The handler goes on the admin client and deliberately not on the token
        // provider's own client: the provider is what mints the token the handler
        // attaches, so applying it to both would have the mint authorise itself.
        builder.Services.AddTransient<KeycloakAdminAuthorizationHandler>();
        // The mint is an idempotent POST — a second token supersedes the first —
        // so it keeps its retries (ADR-0143). The admin client below does not:
        // creating a Keycloak client and rotating a secret are both POSTs that a
        // retry would duplicate, which is what ADR-0142 exists to handle.
        builder.Services.AddHttpClient(KeycloakAdminTokenProvider.HttpClientName, ConfigureHttpClient)
            .RetryEveryMethod();
        builder.Services.AddSingleton<KeycloakAdminTokenProvider>();
        builder.Services.AddHttpClient<HttpKeycloakAdminClient>(ConfigureHttpClient)
            .AddHttpMessageHandler<KeycloakAdminAuthorizationHandler>();
        builder.Services.AddScoped<IKeycloakAdminClient>(sp => sp.GetRequiredService<HttpKeycloakAdminClient>());

        return builder;
    }

    private static void ConfigureHttpClient(IServiceProvider sp, HttpClient client)
    {
        KeycloakAdminOptions opts = sp.GetRequiredService<
            Microsoft.Extensions.Options.IOptions<KeycloakAdminOptions>>().Value;
#pragma warning disable S1075
        string baseUrl = opts.BaseUrl.EndsWith('/') ? opts.BaseUrl : opts.BaseUrl + '/';
#pragma warning restore S1075
        client.BaseAddress = new Uri(baseUrl);

        // HttpClient.Timeout wraps the entire handler chain, retries included, so
        // it is not a per-attempt cap — it is a ceiling over the whole resilience
        // pipeline. At the 10 s it used to hold, a Keycloak slow enough to be
        // worth retrying spent the entire budget on the first attempt and the
        // configured retries never ran: the client looked resilient and behaved
        // like it had none. The per-attempt cap it was reaching for already
        // exists, at the same 10 s, inside the standard handler ServiceDefaults
        // applies; the pipeline's own 30 s total budget bounds the retries.
        client.Timeout = Timeout.InfiniteTimeSpan;
    }

}
