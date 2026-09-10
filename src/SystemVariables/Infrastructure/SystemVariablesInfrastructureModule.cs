using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartSentinelEye.ServiceDefaults;
using SmartSentinelEye.ServiceDefaults.Idempotency;
using SmartSentinelEye.ServiceDefaults.Resilience;
using SmartSentinelEye.Shared.CQRS;
using SmartSentinelEye.Shared.Kernel;
using SmartSentinelEye.SystemVariables.Application.Commands;
using SmartSentinelEye.SystemVariables.Application.Commands.Handlers;
using SmartSentinelEye.SystemVariables.Application.EventHandlers;
using SmartSentinelEye.SystemVariables.Application.Queries;
using SmartSentinelEye.SystemVariables.Application.Resolution;
using SmartSentinelEye.SystemVariables.Domain.Variable;
using SmartSentinelEye.SystemVariables.Domain.Variable.Events;
using SmartSentinelEye.SystemVariables.Infrastructure.Persistence;
using SmartSentinelEye.SystemVariables.Infrastructure.Resolution;

namespace SmartSentinelEye.SystemVariables.Infrastructure;

/// <summary>
/// Composition root for the SystemVariables Infrastructure layer
/// (ADR-0051). Wires EF Core, the Wolverine outbox + cross-context
/// subscribers, the variable repository, the in-memory reverse-index
/// (singleton) and its seeder hosted service, domain-event handlers,
/// and the standard ServiceDefaults.
///
/// <para>
/// The variable-changed / archived domain-event handlers publish a
/// <c>ResolvedOverlayTextChangedV1</c> per affected overlay; the SignalR
/// push lives in LayoutComposition (the hub owner), which subscribes to
/// that event. This context therefore has no dependency on
/// LayoutComposition.
/// </para>
/// </summary>
public static class SystemVariablesInfrastructureModule
{
    public const string ContextName = "system-variables";
    public const string OutboxSchema = "wolverine_system_variables";

    public static IHostApplicationBuilder AddSystemVariablesInfrastructure(this IHostApplicationBuilder builder)
    {
        Ensure.That(builder).IsNotNull();

        builder.AddSystemVariablesPersistence();

        builder.Services.AddScoped<IVariableRepository, VariableRepository>();
        builder.Services.AddScoped<IVariableQuerySource, VariableQuerySource>();
        builder.Services.AddScoped<IDomainEventDispatcher, DomainEventDispatcher>();
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton(TimeProvider.System);

        // ADR-0142. Scoped alongside the DbContext it writes through; TimeProvider
        // above drives the executor's wait for an in-flight attempt.
        builder.Services.AddScoped<IIdempotencyStore, IdempotencyStore<SystemVariablesDbContext>>();

        // Reverse-index + resolver are process-wide singletons (the
        // index is mutated concurrently; the resolver is stateless).
        builder.Services.AddSingleton<IReverseIndex, InMemoryReverseIndex>();
        builder.Services.AddSingleton<IResolver, Resolver>();

        // Domain event handlers (in-process, fan out to V1 + broadcaster).
        builder.Services.AddScoped<
            IDomainEventHandler<VariableDefinedDomainEvent>,
            VariableDefinedDomainEventHandler>();
        builder.Services.AddScoped<
            IDomainEventHandler<VariableValueChangedDomainEvent>,
            VariableValueChangedDomainEventHandler>();
        builder.Services.AddScoped<
            IDomainEventHandler<VariableArchivedDomainEvent>,
            VariableArchivedDomainEventHandler>();

        // Hand-rolled command handler registrations (ADR-0042 + ADR-0057).
        builder.Services.AddScoped<
            ICommandHandler<DefineVariableCommand, Result<VariableIdentifier, DefineVariableError>>,
            DefineVariableCommandHandler>();
        builder.Services.AddScoped<SetVariableValueCommandHandler>();
        builder.Services.AddScoped<
            ICommandHandler<SetVariableValueCommand, Result<VariableIdentifier, SetVariableValueError>>,
            SetVariableValueCommandHandler>();
        builder.Services.AddScoped<
            ICommandHandler<ArchiveVariableCommand, Result<VariableIdentifier, ArchiveVariableError>>,
            ArchiveVariableCommandHandler>();

        // Spec 007 bridge: subscribe to SystemVariableValueRequestedV1
        // from Automation. Dedup on (variableName, causingEventIdentifier).
        builder.Services.AddScoped<IVariableValueRequestDedupStore, VariableValueRequestDedupStore>();
        builder.Services.AddScoped<SystemVariableValueRequestedV1Handler>();

        BindReverseIndexSeeder(builder);

        builder.AddWolverineForContext<SystemVariablesDbContext>(
            moduleQueuePrefix: ContextName,
            outboxSchema: OutboxSchema,
            postgresConnectionName: SystemVariablesPersistenceModule.DatabaseConnectionName);

        return builder;
    }

    /// <summary>
    /// The startup reverse-index seed and the credential it presents (spec 126,
    /// #2158). Mirrors StreamDistribution's <c>BindStreamFabAttribution</c> —
    /// same shape of call, same shape of wiring.
    /// </summary>
    private static void BindReverseIndexSeeder(IHostApplicationBuilder builder)
    {
        string keycloakBaseUrl =
            builder.Configuration.GetConnectionString("keycloak")
            ?? builder.Configuration["services:keycloak:http:0"]
            ?? builder.Configuration["services:keycloak:https:0"]
            ?? string.Empty;

        builder.Services.Configure<ReverseIndexSeederOptions>(options =>
        {
            builder.Configuration.GetSection(ReverseIndexSeederOptions.SectionName).Bind(options);
            options.KeycloakUrl = keycloakBaseUrl;
            options.Realm = builder.Configuration["Keycloak:Realm"] ?? options.Realm;
        });

        // Idempotent POST — see ADR-0143; a second token supersedes the first.
        // Without the opt-in, a Keycloak blip at host start would leave the index
        // empty for the life of the process, which is the defect this fixes.
        builder.Services.AddHttpClient(OverlayDesignerTokenProvider.HttpClientName).RetryEveryMethod();
        // Singleton, which is what makes the token cache mean anything (#2037).
        builder.Services.AddSingleton<OverlayDesignerTokenProvider>();
        // On the seed client only. The token provider's own client mints the
        // token this handler attaches, so authorising it would make the mint
        // depend on itself.
        builder.Services.AddTransient<OverlayDesignerAuthorizationHandler>();

        // Named rather than typed, because the seeder is a hosted service and so
        // a singleton: a typed client injected into one is held for the process
        // lifetime, which pins a single HttpMessageHandler and defeats the
        // factory's rotation. The base address belongs here rather than at the
        // call site — S1075 flags the literal URI and S5332 the clear-text
        // scheme, and neither applies: Aspire service discovery treats
        // "overlay-designer" as a logical resource name and rewrites the whole
        // URI, scheme included, in dev and on k3s alike.
#pragma warning disable S1075, S5332
        builder.Services.AddHttpClient("overlay-designer", client =>
                client.BaseAddress = new Uri("http://overlay-designer"))
#pragma warning restore S1075, S5332
            .AddHttpMessageHandler<OverlayDesignerAuthorizationHandler>();

        builder.Services.AddHostedService<ReverseIndexSeederHostedService>();
    }
}
