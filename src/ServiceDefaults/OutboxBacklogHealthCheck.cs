using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace SmartSentinelEye.ServiceDefaults;

/// <summary>
/// Reports how many announcements are waiting to be delivered, and whether
/// delivery is stuck (spec 021 FR-008, FR-009).
///
/// <para>
/// This exists because the feature it belongs to is invisible when it works.
/// Before it, a failed announcement vanished and left nothing to look at; after
/// it, a failed announcement is a durable row that will be retried — and an
/// outbox quietly growing looks exactly like an empty one until the disk fills.
/// Trading a silent loss for a silent backlog would not be much of a trade.
/// </para>
///
/// <para>
/// <b>Degraded, not Unhealthy.</b> A backlog means delivery is behind, which is
/// what the outbox is for; nothing has been lost and the write path is still
/// serving. Failing the readiness probe would take a service out of rotation for
/// a condition it is currently handling correctly.
/// </para>
/// </summary>
public sealed class OutboxBacklogHealthCheck<TDbContext>(
    TDbContext database,
    ILogger<OutboxBacklogHealthCheck<TDbContext>> logger,
    string outboxSchema)
    : IHealthCheck
    where TDbContext : DbContext
{
    /// <summary>
    /// Past this many failed attempts on a single announcement, delivery is not
    /// merely behind — something about that message or its destination is wrong,
    /// and a human should look before the retries become the only thing the
    /// sending agent is doing.
    /// </summary>
    public const int ConcerningAttempts = 5;

    /// <summary>
    /// Past this many waiting announcements, something is not draining.
    ///
    /// <para>
    /// Generous on purpose: the ingest path commits batches of 200, so a healthy
    /// system under load is briefly in the hundreds. What this catches is a
    /// backlog that keeps climbing — in particular one nothing is retrying,
    /// which the attempts counter cannot see because an unreleased message is
    /// never attempted.
    /// </para>
    /// </summary>
    public const int ConcerningBacklog = 5_000;

    /// <summary>
    /// How many times the most-retried announcement has failed to go out.
    ///
    /// <para>
    /// FR-008 asked for the <i>age</i> of the oldest pending message, and that
    /// is not obtainable: Wolverine's outgoing table holds
    /// <c>id, owner_id, destination, deliver_by, body, attempts, message_type</c>
    /// and records no enqueue time. Discovered by asking the database rather
    /// than assumed from documentation — the first version of this check read a
    /// column that does not exist and reported Healthy about it.
    /// </para>
    ///
    /// <para>
    /// Attempts answer the question the age was a proxy for. "Delivery is stuck"
    /// shows up as a climbing retry count sooner and less ambiguously than as an
    /// old row, because a message queued a while ago and delivered first time is
    /// not a problem at all.
    /// </para>
    /// </summary>
    public const string AttemptsColumn = "attempts";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            (long pending, int attempts) = await ReadBacklogAsync(cancellationToken);

            Dictionary<string, object> data = new(StringComparer.Ordinal)
            {
                ["pending"] = pending,
                ["maxAttempts"] = attempts,
                ["schema"] = outboxSchema,
            };

            if (pending == 0)
            {
                return HealthCheckResult.Healthy("No announcements are waiting.", data);
            }

            string description = string.Create(
                CultureInfo.InvariantCulture,
                $"{pending} announcement(s) waiting; most-retried has failed {attempts} time(s).");

            // Both signals, because they catch different failures and this check
            // exists for the one nobody would think to look for.
            //
            // Attempts climb when delivery is failing. But a message that is
            // captured and never *released* — the "sits in the outbox for ever"
            // case the IEventBus documentation describes, and the one a publish
            // outside a write produces — is never attempted at all, so attempts
            // stays 0 while pending grows without bound. Watching only the retry
            // counter would report Healthy right up until the disk filled, which
            // is the silent backlog this feature promised not to trade a silent
            // loss for.
            if (attempts < ConcerningAttempts && pending < ConcerningBacklog)
            {
                return HealthCheckResult.Healthy(description, data);
            }

            // Logged as well as reported, because the two carry different
            // things and the log carries the only one an operator can act on.
            // The readiness probe is reachable in production now, but a Degraded
            // aggregate answers 200 — the framework's default ResultStatusCodes
            // map Degraded to 200 and only Unhealthy to 503 — which is the
            // behaviour this check wants: a service behind on delivery is still
            // serving and must not leave rotation. So the probe says nothing an
            // orchestrator reacts to, and its default writer emits one word, so
            // the schema, the pending count and the attempt count appear nowhere
            // on that surface. They exist here or not at all (FR-009).
            logger.OutboxBacklogConcerning(outboxSchema, pending, attempts);
            return HealthCheckResult.Degraded(description, data: data);
        }
        catch (DbException ex) when (IsUnreachable(ex))
        {
            // Nothing else reports the database being unreachable. The check
            // set is exactly two members — "self", which returns Healthy
            // unconditionally, and this one — so there is no connection check
            // to defer to, and the Healthy below is not deduplication. It is a
            // deliberate choice not to fail readiness on a dependency every
            // replica shares: draining all of them at once helps nobody, and a
            // backlog monitor is the wrong place to own that signal. Whether
            // that is the right answer, and what should own it instead, is
            // #2125.
            return HealthCheckResult.Healthy(
                "Backlog not readable; the database check owns this.",
                new Dictionary<string, object>(StringComparer.Ordinal) { ["error"] = ex.GetType().Name });
        }
        catch (DbException ex)
        {
            // Anything else is this check being wrong rather than the database
            // being away — a renamed column, a missing schema, a query that no
            // longer parses.
            //
            // It used to report Healthy. That is a backlog monitor that lies
            // about being fine, which is worse than not having one: the first
            // version read a column that does not exist and would have said
            // "no announcements are waiting" for ever, about an outbox nobody
            // was watching. An integration test now asserts the column name and
            // this reports the truth if it ever drifts again.
            return HealthCheckResult.Degraded(
                $"The outbox backlog could not be read: {ex.Message}",
                data: new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["error"] = ex.GetType().Name,
                    ["schema"] = outboxSchema,
                });
        }
    }

    /// <summary>
    /// Whether this is the database being away rather than the query being
    /// wrong. Npgsql reports connection failures with no SQLSTATE — a server
    /// that answered well enough to reject a query has told us something about
    /// our query, not about its availability.
    /// </summary>
    private static bool IsUnreachable(DbException exception) =>
        string.IsNullOrEmpty(exception.SqlState);

    /// <summary>
    /// Wolverine owns this table; nothing in this repository writes it, which is
    /// why reading it is the only way to see a pending announcement at all.
    ///
    /// <para>
    /// Read through EF rather than a hand-rolled command, because the first
    /// version called <c>OpenConnectionAsync</c> with no matching close. EF
    /// counts explicit opens, so the connection was held until the scope was
    /// disposed — once per readiness probe, across nine services, against the
    /// Postgres that Keycloak and every context share. A health check that adds
    /// load to what it is checking is not a good trade for two numbers.
    /// </para>
    /// </summary>
    private async Task<(long Pending, int Attempts)> ReadBacklogAsync(CancellationToken cancellationToken)
    {
        string table = $"{Identifier(outboxSchema)}.wolverine_outgoing_envelopes";

        // EF1002 is suppressed because a schema name cannot be a parameter —
        // SQL takes identifiers literally — and it is earned rather than
        // asserted: Identifier() refuses anything that is not a plain
        // lower-case identifier, so nothing that reaches here can carry SQL.
        // The value comes from AddWolverineForContext's caller, a constant per
        // module, and never from a request.
#pragma warning disable EF1002
        long pending = await database.Database
            .SqlQueryRaw<long>($"SELECT count(*) AS \"Value\" FROM {table}")
            .SingleAsync(cancellationToken);

        if (pending == 0)
        {
            // Nothing waiting means nothing is being retried, so the second
            // query would always answer zero. Skipped rather than asked,
            // because this runs on every probe and the common case is empty.
            return (0, 0);
        }

        int attempts = await database.Database
            .SqlQueryRaw<int>($"SELECT COALESCE(max(\"{AttemptsColumn}\"), 0) AS \"Value\" FROM {table}")
            .SingleAsync(cancellationToken);
#pragma warning restore EF1002

        return (pending, attempts);
    }

    /// <summary>
    /// Refuses anything that is not a plain identifier, so the interpolation
    /// above cannot carry SQL. A format check rather than an argument guard —
    /// it is what makes the suppression honest instead of a note saying the
    /// value is probably fine.
    /// </summary>
    private static string Identifier(string value) =>
        value.All(character => char.IsAsciiLetterLower(character) || character == '_')
            ? value
            : throw new ArgumentException(
                $"'{value}' is not a plain outbox schema identifier.", nameof(value));
}
