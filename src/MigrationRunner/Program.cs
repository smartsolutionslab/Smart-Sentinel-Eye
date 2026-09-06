using Microsoft.Extensions.DependencyInjection;
using SmartSentinelEye.AuditObservability.Infrastructure.Persistence;
using SmartSentinelEye.Automation.Infrastructure.Persistence;
using SmartSentinelEye.CameraCatalog.Infrastructure.Persistence;
using SmartSentinelEye.EventIngestion.Infrastructure.Persistence;
using SmartSentinelEye.Identity.Infrastructure.Persistence;
using SmartSentinelEye.LayoutComposition.Infrastructure.Persistence;
using SmartSentinelEye.OverlayDesigner.Infrastructure.Persistence;
using SmartSentinelEye.StreamDistribution.Infrastructure.Persistence;
using SmartSentinelEye.SystemVariables.Infrastructure.Persistence;
using SmartSentinelEye.AuditObservability.Infrastructure;
using SmartSentinelEye.Automation.Infrastructure;
using SmartSentinelEye.CameraCatalog.Infrastructure;
using SmartSentinelEye.EventIngestion.Infrastructure;
using SmartSentinelEye.Identity.Infrastructure;
using SmartSentinelEye.LayoutComposition.Infrastructure;
using SmartSentinelEye.EventIngestion.Application.Ingress;
using SmartSentinelEye.MigrationRunner;
using SmartSentinelEye.OverlayDesigner.Infrastructure;
using SmartSentinelEye.ServiceDefaults;
using SmartSentinelEye.StreamDistribution.Infrastructure;
using SmartSentinelEye.SystemVariables.Infrastructure;

// MigrationRunner orchestrates all bounded-context database migrations and exits (ADR-0067).
// Each IMigrator runs sequentially before any Api service starts.

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();

builder.Services.AddSingleton<PostgresNoticeLoggingInterceptor>();

builder.AddCameraCatalogPersistence();
builder.AddStreamDistributionPersistence();
builder.AddLayoutCompositionPersistence();
builder.AddOverlayDesignerPersistence();
builder.AddSystemVariablesPersistence();
builder.AddEventIngestionPersistence();
builder.AddAutomationPersistence();
builder.AddIdentityPersistence();
builder.AddAuditObservabilityPersistence();

// Applied per context type: EF reads IDbContextOptionsConfiguration<T> after
// the options delegate each Add<Context>Persistence supplies, which is the
// only hook that reaches those options without editing all nine modules — and
// keeps this to MigrationRunner rather than every service (#1394).
builder.AddPostgresNoticeLogging<CameraCatalogDbContext>();
builder.AddPostgresNoticeLogging<StreamDistributionDbContext>();
builder.AddPostgresNoticeLogging<LayoutCompositionDbContext>();
builder.AddPostgresNoticeLogging<OverlayDesignerDbContext>();
builder.AddPostgresNoticeLogging<SystemVariablesDbContext>();
builder.AddPostgresNoticeLogging<EventIngestionDbContext>();
builder.AddPostgresNoticeLogging<AutomationDbContext>();
builder.AddPostgresNoticeLogging<IdentityDbContext>();
builder.AddPostgresNoticeLogging<AuditObservabilityDbContext>();

// Spec 019: event partitions are provisioned per fab, and the fabs come from
// the realm's group tree. Identity owns Keycloak and EventIngestion may not
// reference it, so the two meet here — in the composition root, which is
// allowed to know both — and nowhere else.
builder.AddKeycloakAdminClient();
builder.Services.AddScoped<IProvisionedFabSource, KeycloakProvisionedFabSource>();

// Registered last so it runs after every context's EF migrations, including
// EventIngestion's own: the rollover needs the parent `events` table to exist.
builder.Services.AddScoped<IMigrator, EventPartitionRolloverMigrator>();

// `using` for the OTLP exporters, which flush when the provider is disposed
// rather than when the host stops — this process registers no hosted service,
// so StopAsync has nothing to stop. It is not what delivers the Critical
// below: that reaches a reader over stdout, which Aspire's
// ResourceLoggerService tails and which needs no flush.
using IHost host = builder.Build();
ILogger<Program> logger = host.Services.GetRequiredService<ILogger<Program>>();

await host.StartAsync();

int exitCode = await MigrationRun.ExecuteAsync(
    host.Services,
    logger,
    host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);

// Guarded for the reason the run itself is (#2062): an exception escaping
// this line ends the process through the runtime — stderr, and an exit code
// nobody chose — with `return exitCode` never reached. The verdict on the
// migrations was decided above and is not revised here: a shutdown that
// stumbles after nine contexts migrated cleanly must not gate nine services
// on it.
try
{
    await host.StopAsync();
}
catch (Exception exception)
{
    logger.MigrationHostShutdownFailed(exception);
}

return exitCode;
