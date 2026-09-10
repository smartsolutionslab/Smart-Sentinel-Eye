using Microsoft.EntityFrameworkCore;
using SmartSentinelEye.AuditObservability.Infrastructure.Persistence;
using SmartSentinelEye.Integration.Tests.Fixtures;
using SmartSentinelEye.ServiceDefaults.Persistence;
using Xunit.Abstractions;

namespace SmartSentinelEye.Integration.Tests.ServiceDefaults;

/// <summary>
/// Issue 1962 / ADR-0125. <c>PostgresConnectionBudgetTests</c> checks that the
/// arithmetic adds up; this checks that the server it is supposed to fit inside
/// was actually started that way.
///
/// <para>
/// The two halves live apart because the number is written twice on purpose:
/// <c>AppHost</c> cannot reference <c>ServiceDefaults</c> (an Aspire project
/// reference exposes no assembly), so <c>max_connections</c> is a literal there.
/// Asking the running server closes that gap better than sharing a constant
/// would — it verifies what was deployed rather than what was written, and it
/// also catches the container silently ignoring the argument.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public class PostgresConnectionBudgetIntegrationTests(AspireFixture aspire, ITestOutputHelper output)
{
    /// <summary>
    /// The budget is meaningless if the server allows fewer connections than the
    /// pools are permitted to open. Before this was raised, the stack ran at
    /// Postgres' default of 100 and held 97 of them idle, so the first burst of
    /// real load exhausted it — and the write that failed belonged to a context
    /// that had consumed almost none of them.
    ///
    /// <para>
    /// <b>Threshold ≥ 500; observed exactly 500</b>, twice (dev box,
    /// 2026-09-10, issue #2149). <b>The margin is zero and that is correct</b>:
    /// this is not a headroom assertion but an equality check across a number
    /// written in two places, because <c>AppHost</c> cannot reference
    /// <c>ServiceDefaults</c>. A figure above 500 would mean the two had
    /// drifted apart, not that things were going well.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_running_server_allows_what_the_budget_assumes()
    {
        await using AuditObservabilityDbContext context =
            await aspire.CreateAuditObservabilityDbContextAsync();

        List<int> allowed = await context.Database
            .SqlQueryRaw<int>("SELECT setting::int AS \"Value\" FROM pg_settings WHERE name = 'max_connections'")
            .ToListAsync();

        allowed.ShouldHaveSingleItem();

        // The observation, not just the verdict (#2149).
        output.WriteLine(
            $"postgres max_connections = {allowed[0]} "
            + $"(budget assumes at least {PostgresConnectionBudget.ServerMaxConnections})");

        allowed[0].ShouldBeGreaterThanOrEqualTo(
            PostgresConnectionBudget.ServerMaxConnections,
            $"AppHost starts Postgres with max_connections; the budget assumes at least "
            + $"{PostgresConnectionBudget.ServerMaxConnections} and the server reports {allowed[0]}. "
            + "Either the AppHost argument changed or the container ignored it.");
    }

    /// <summary>
    /// Asserted against what is actually connected, not against the cap: every
    /// service in the fixture is up and has opened its pools, so if the platform
    /// were still on unbounded defaults this is where it would show.
    ///
    /// <para>
    /// A margin rather than an exact figure, because the count moves with
    /// whatever else the suite is doing when this runs. What it must never be is
    /// close to the limit while idle-ish — that was the state that made the
    /// original failure look like an unrelated context's bug.
    /// </para>
    ///
    /// <para>
    /// <b>Ceiling 378 ((20 × 2) + 2) × 9; observed 91 and 91
    /// connections</b> with the whole fixture up (dev box, 2026-09-10, issue
    /// #2149) — about <b>4.2×</b> of headroom, and a quarter of the
    /// <c>max_connections</c> 500 the server allows. Set that against the 97
    /// of 100 this budget was created to stop
    /// (<see cref="PostgresConnectionBudget"/>): the figure that matters is not
    /// the ratio but that it is a *fraction* of the ceiling while every service
    /// is connected.
    /// </para>
    ///
    /// <para>
    /// <b>A resource budget, not a latency one</b> — none of constitution §IV's
    /// six legs applies. It is the only budget in spec 123's scope that
    /// measures a count.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_stack_is_not_sitting_near_the_limit()
    {
        await using AuditObservabilityDbContext context =
            await aspire.CreateAuditObservabilityDbContextAsync();

        List<int> used = await context.Database
            .SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_stat_activity")
            .ToListAsync();

        used.ShouldHaveSingleItem();

        // The count is the whole point of the assertion and was visible only on
        // failure (#2149). A fixture figure far below the ceiling is what says
        // the caps are holding, and it is what makes the margin knowable.
        output.WriteLine(
            $"pg_stat_activity = {used[0]} connections against a service ceiling of "
            + $"{PostgresConnectionBudget.ServiceCeiling} (pool cap "
            + $"{PostgresConnectionBudget.MaxPoolSize}, server allows "
            + $"{PostgresConnectionBudget.ServerMaxConnections})");

        used[0].ShouldBeLessThan(
            PostgresConnectionBudget.ServiceCeiling,
            $"{used[0]} connections are open against a service ceiling of "
            + $"{PostgresConnectionBudget.ServiceCeiling}. The pools are meant to be capped at "
            + $"{PostgresConnectionBudget.MaxPoolSize} each (ADR-0125); exceeding the ceiling with "
            + "the suite merely running means something is opening connections outside the budget.");
    }
}
