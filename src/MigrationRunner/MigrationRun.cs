using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartSentinelEye.ServiceDefaults;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.MigrationRunner;

/// <summary>
/// Runs every registered <see cref="IMigrator"/> and reports how it went.
///
/// <para>
/// Extracted from <c>Program</c>'s top-level statements for issue #2062. It
/// had no try/catch anywhere, so anything thrown left the run through the
/// runtime — which on Linux prints the reason to stderr and ends the process.
/// Nothing read that stderr, and the nine services gated on <c>migrations</c>
/// each reported FailedToStart with nothing to say why. Routing the failure
/// through <see cref="ILogger"/> puts it in the structured pipeline and on the
/// Aspire dashboard instead.
/// </para>
///
/// <para>
/// A class rather than more top-level statements because top-level statements
/// cannot be called: <c>Program.&lt;Main&gt;$</c> is not reachable from a test,
/// and reaching it would stand up nine <c>DbContext</c>s and a Keycloak client.
/// </para>
/// </summary>
internal static class MigrationRun
{
    internal const int Succeeded = 0;

    internal const int Failed = 1;

    public static async Task<int> ExecuteAsync(
        IServiceProvider services, ILogger logger, CancellationToken cancellationToken)
    {
        Ensure.That(services).IsNotNull();
        Ensure.That(logger).IsNotNull();

        string reached = "resolving the migrators";
        try
        {
            // Resolved from a scope rather than the root provider. Every migrator was a
            // singleton until spec 019 added one that depends on the Keycloak admin client,
            // which is scoped — and resolving a scoped service from the root throws under
            // the scope validation the Development environment turns on.
            await using AsyncServiceScope scope = services.CreateAsyncScope();

            foreach (IMigrator migrator in scope.ServiceProvider.GetServices<IMigrator>())
            {
                reached = migrator.ContextName;
                logger.RunningMigrations(reached);
                await migrator.RunAsync(cancellationToken);
            }

            logger.AllMigrationsApplied();
            return Succeeded;
        }
        catch (OperationCanceledException cancelled) when (cancellationToken.IsCancellationRequested)
        {
            // A SIGTERM to the container is not a fault, and the exit code is the
            // same either way: nothing after this point was migrated. What differs
            // is what the reader should do about it, so it gets its own line.
            // Filtered on our own token — an OperationCanceledException raised by
            // an inner timeout inside a migrator is a genuine failure and falls
            // through to the clause below.
            logger.MigrationRunStopped(reached, cancelled);
            return Failed;
        }
        catch (Exception exception)
        {
            // Unfiltered on purpose. Every filter here is a way back out to
            // stderr, which is the hole this closes; the clause above narrows
            // what is *said*, not what is caught.
            logger.MigrationRunFailed(reached, exception);
            return Failed;
        }
    }
}
