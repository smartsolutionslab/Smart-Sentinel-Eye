using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.ServiceDefaults.Persistence;

/// <summary>
/// Converts a Postgres unique-constraint violation into a
/// <c>409 RESOURCE_ALREADY_EXISTS</c> problem-details response, mirroring
/// <see cref="ConcurrencyConflictExceptionHandler"/>.
///
/// <para>
/// The product's posture is that every uniqueness rule is enforced twice: an
/// application-level check that produces an answer an operator can act on, and
/// a unique index that guarantees the invariant. The two are <b>not atomic</b>,
/// so another writer can take the name in between. Before this handler existed
/// the loser of that race got a <c>500</c> — a server fault, for asking about a
/// name that was free when they asked.
/// </para>
///
/// <para>
/// That posture did <b>not</b> hold for two contexts, from the day this comment
/// was written until spec 086. Layout and overlay names — the only two
/// revision-chain aggregates, whose rule is about a state living on a child
/// table — had the check and no index, so the case this handler exists for could
/// not arise for them and a second writer simply won.
/// <c>ux_layouts_fab_name_active</c> and <c>ux_overlays_name_active</c> close
/// <b>those two known gaps</b>, and that is the whole of what spec 086
/// establishes. It does not re-establish the word "every": the search that found
/// these two counted <c>.IsUnique()</c> <i>indexes</i>, so it can say which
/// rules have an index and cannot say which application checks lack one.
/// Recorded rather than quietly corrected — a doc comment asserting a
/// product-wide posture is exactly the kind of claim that goes unchecked, and it
/// went unchecked for two contexts. Replacing one unchecked claim with another
/// would not be the fix.
/// </para>
///
/// <para>
/// The refusal is deliberately generic. This layer knows a constraint was
/// violated; it does not know which domain concept collided, and teaching it
/// would mean giving shared code the vocabulary of nine contexts. It does not
/// need to: every context with a user-facing uniqueness rule already answers
/// specifically — <c>CAMERA_NAME_TAKEN</c>, <c>RULE_NAME_TAKEN</c>,
/// <c>VARIABLE_NAME_TAKEN</c> and the rest — and those fire on the common path.
/// This one is what a caller sees only when such a check was told the name was
/// free and lost the race before its write landed. Those checks stay; this is a
/// backstop for a race, not a replacement for a check.
/// </para>
/// </summary>
public sealed class UniqueConstraintExceptionHandler : IExceptionHandler
{
    /// <summary>
    /// Named for the remedy, not the cause.
    ///
    /// <para>
    /// This failure <em>is</em> caused by concurrency, and a code saying so
    /// would be caught by <c>StaleCodeConventionTests</c> — correctly. ADR-0119
    /// reserves that vocabulary for <b>lost updates</b>, where re-reading and
    /// reapplying is the answer. Here the caller's own resource was never
    /// touched: their version is fine, re-reading shows them exactly what they
    /// already had, and the only thing that helps is a different name.
    /// </para>
    /// </summary>
    public const string ErrorCode = "RESOURCE_ALREADY_EXISTS";

    /// <summary>
    /// Says "name or key" rather than "name": not every unique index guards a
    /// value an operator chose. Some guarantee a structural rule, and telling
    /// someone to pick a different name would be false there.
    ///
    /// <para>
    /// The last clause is what separates this from the stale-version refusal in
    /// the caller's hands. Both are <c>409</c>-shaped conflicts; only one is
    /// resolved by re-reading. Saying that retrying unchanged will fail again
    /// stops the retry loop that conflating them produces.
    /// </para>
    /// </summary>
    private const string Explanation =
        "Something with that name or key already exists. Choose a different one — "
        + "retrying this request unchanged will be refused again.";

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        Ensure.That(httpContext).IsNotNull();

        if (!IsUniqueViolation(exception))
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;

        // Nothing from the exception reaches the response — not ConstraintName,
        // not TableName, and above all not MessageText/Detail/Hint, which quote
        // the colliding values verbatim. In a multi-fab deployment those values
        // can belong to a fab the caller cannot see, so echoing them would turn
        // this refusal into the enumeration oracle several contexts are built to
        // prevent. Satisfied by never reading the fields rather than by
        // stripping them: a stripped field is one edit from being unstripped.
        ProblemDetails problem = new()
        {
            Title = ErrorCode,
            Detail = Explanation,
            Status = StatusCodes.Status409Conflict,
        };
        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken: cancellationToken);

        return true;
    }

    /// <summary>
    /// Matched on the <b>SQLSTATE</b>, never on the exception type.
    ///
    /// <para>
    /// <see cref="DbUpdateConcurrencyException"/> derives from
    /// <see cref="DbUpdateException"/>. A type-based match would therefore
    /// answer for every lost update as well, reporting it as a name collision —
    /// telling a caller to choose a different name for a change that only
    /// needed re-reading. Matching the SQLSTATE removes that trap rather than
    /// documenting it: a concurrency exception arises from an unexpected
    /// affected-row count and carries no Postgres error at all, so it cannot
    /// match however this handler is registered.
    /// </para>
    ///
    /// <para>
    /// Both arms, mirroring
    /// <c>PersistenceLoopHostedService.IsMissingPartition</c>. EF wraps every
    /// provider exception, so the bare arm alone never fires — that was
    /// discovered the hard way once already and the comment there records it.
    /// </para>
    /// </summary>
    private static bool IsUniqueViolation(Exception exception) => exception switch
    {
        PostgresException postgres => postgres.SqlState == PostgresErrorCodes.UniqueViolation,
        DbUpdateException { InnerException: PostgresException inner } =>
            inner.SqlState == PostgresErrorCodes.UniqueViolation,
        _ => false,
    };
}
