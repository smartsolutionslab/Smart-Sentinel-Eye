using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartSentinelEye.MigrationRunner.Tests.Fakes;
using SmartSentinelEye.ServiceDefaults;

namespace SmartSentinelEye.MigrationRunner.Tests;

/// <summary>
/// Issue #2062, deliverable A. The migration run had no try/catch anywhere:
/// anything thrown left <c>Program</c>'s top-level statements for the runtime,
/// which on Linux ends the process with the reason on <b>stderr only</b> — and
/// every service gated on <c>migrations</c> then reports FailedToStart with
/// nothing to say why. The lost message is what the issue is about.
///
/// <para>
/// <c>Program.&lt;Main&gt;$</c> cannot be called from a test and booting it
/// stands up nine <c>DbContext</c>s and a Keycloak client, so the run moved
/// into <see cref="MigrationRun"/> where it can be driven with migrators that
/// do nothing but succeed or throw. <c>ILogger</c> is the point of the seam:
/// it reaches the structured pipeline and the Aspire dashboard, which stderr
/// did not.
/// </para>
///
/// <para>
/// Same shape as <c>ScenarioSimulator.Tests.ScenarioSeederResilienceTests</c>,
/// which covers #1900: a collaborator that throws, a capturing logger, and an
/// assertion on what was said rather than on how it was said.
/// </para>
/// </summary>
public sealed class MigrationRunTests
{
    [Fact]
    public async Task A_migrator_that_throws_is_reported_rather_than_escaping_the_process()
    {
        CapturingLogger<Program> logger = new();
        using ServiceProvider services = Services(new ThrowingMigrator("EventIngestion"));

        int exitCode = await MigrationRun.ExecuteAsync(services, logger, CancellationToken.None);

        exitCode.ShouldNotBe(0);
        logger.Entries.ShouldContain(entry => entry.Level >= LogLevel.Error && entry.Exception != null);
    }

    /// <summary>
    /// Which one failed, not merely that something did. Nine contexts migrate
    /// in one process and the reader's next question is always which.
    /// </summary>
    [Fact]
    public async Task The_failure_names_the_context_it_happened_in()
    {
        CapturingLogger<Program> logger = new();
        using ServiceProvider services = Services(
            new SucceedingMigrator("CameraCatalog"), new ThrowingMigrator("EventIngestion"));

        await MigrationRun.ExecuteAsync(services, logger, CancellationToken.None);

        logger.Entries.ShouldContain(entry =>
            entry.Level >= LogLevel.Error &&
            entry.Message.Contains("EventIngestion", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_run_that_applies_every_migration_reports_success()
    {
        SucceedingMigrator camera = new("CameraCatalog");
        SucceedingMigrator events = new("EventIngestion");
        CapturingLogger<Program> logger = new();
        using ServiceProvider services = Services(camera, events);

        int exitCode = await MigrationRun.ExecuteAsync(services, logger, CancellationToken.None);

        exitCode.ShouldBe(0);
        camera.Ran.ShouldBeTrue();
        events.Ran.ShouldBeTrue();
    }

    /// <summary>
    /// The migrators are scoped in the host for the reason the run's own
    /// comment records, so they are scoped here too — resolving them from the
    /// root provider is the throw this seam has to survive, not avoid.
    /// </summary>
    private static ServiceProvider Services(params IMigrator[] migrators)
    {
        ServiceCollection services = new();
        foreach (IMigrator migrator in migrators)
        {
            services.AddScoped(_ => migrator);
        }

        return services.BuildServiceProvider(validateScopes: true);
    }

    private sealed class SucceedingMigrator(string contextName) : IMigrator
    {
        public string ContextName => contextName;

        public bool Ran { get; private set; }

        public Task RunAsync(CancellationToken cancellationToken)
        {
            Ran = true;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingMigrator(string contextName) : IMigrator
    {
        public string ContextName => contextName;

        public Task RunAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("relation \"events\" does not exist");
    }
}
