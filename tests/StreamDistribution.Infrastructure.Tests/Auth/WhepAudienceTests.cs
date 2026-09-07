using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SmartSentinelEye.ServiceDefaults;
using SmartSentinelEye.StreamDistribution.Infrastructure.Auth;

namespace SmartSentinelEye.StreamDistribution.Infrastructure.Tests.Auth;

/// <summary>
/// Spec 071 — the WHEP hook checks the audience too.
///
/// <para>
/// <b>What is broken.</b> <c>WhepAuthValidator</c> builds its
/// <see cref="TokenValidationParameters"/> with <c>ValidateAudience = false</c>,
/// under a comment claiming it mirrors the standard bearer pipeline. That comment
/// stopped being true when #91 merged and set <c>ValidAudiences</c> on
/// <c>AuthenticationDefaults</c>, leaving <c>POST /streams/authorize</c> as the one
/// authenticated HTTP surface that still accepts a token minted for another API.
/// </para>
///
/// <para>
/// <b>Why these tests exist at all.</b> This construction has never had unit-level
/// cover: it was reachable only through <c>WhepAuthIntegrationTests</c>, which needs
/// Docker — a large part of why the flag could go stale with nothing noticing. These
/// run in seconds, with no stack, no signing key, no minted token, and no network
/// call: the authority below is never dialled.
/// </para>
///
/// <para>
/// <b>The pairing, not a comment.</b> A comment was the only thing binding the hook
/// to the pipeline and it did not survive one change. The parity tests below compare
/// <see cref="WhepAuthValidator.CreateParameters"/> against the pipeline directly, so
/// either side moving is a failure here rather than a discovery in production
/// (spec 071 FR-005).
/// </para>
///
/// <para>
/// <b>What they do not cover, and where that lives</b> (#2093). They compare the
/// <em>factory</em>. <c>ValidateAsync</c> clones its parameters and mutates the clone
/// before validating, and nothing here reaches that clone: setting
/// <c>ValidateAudience = false</c> on it leaves all four tests below green. Saying
/// otherwise would be the same overstatement — a claim about coverage that nothing
/// checks — that caused #2090. <see cref="WhepValidatorAudienceTests"/> closes it by
/// driving the real <c>ValidateAsync</c>; keep the two files together.
/// </para>
///
/// <para>
/// <b>The same limit applies to the issuer parity below</b> (spec 089, #2095).
/// <see cref="The_whep_hook_leaves_the_issuer_to_discovery_exactly_as_the_bearer_pipeline_does"/>
/// binds the <em>factory</em> and nothing more: it cannot see the clone
/// <c>ValidateAsync</c> hands the handler, so it would stay green if the issuer were
/// re-hardcoded onto that clone, and it says nothing about which issuer a token is
/// actually checked against. <see cref="WhepValidatorIssuerTests"/> is its runtime
/// half, driving the real <c>ValidateAsync</c> with the dialled URL and the realm's
/// issuer deliberately split. Neither stands alone — that pairing is #2093's whole
/// lesson, and it now spans three files rather than two.
/// </para>
/// </summary>
public class WhepAudienceTests
{
    /// <summary>
    /// <b>The refusal itself, as a pure function.</b> Calls the same
    /// <see cref="Validators.ValidateAudience"/> the bearer handler calls, exactly as
    /// spec 069's <c>BearerAudienceTests.A_token_minted_for_another_api_is_refused</c>
    /// does. With <c>ValidateAudience</c> off the function returns without looking,
    /// which is the honest reason this fails today.
    /// </summary>
    [Fact]
    public void A_whep_token_minted_for_another_api_is_refused()
    {
        Should.Throw<SecurityTokenInvalidAudienceException>(() =>
            Validators.ValidateAudience(
                ["some-other-api"],
                securityToken: null,
                WhepAuthValidator.CreateParameters()));
    }

    /// <summary>
    /// Parity on the switch. Compared against the pipeline rather than asserted as
    /// <c>true</c>, so the two cannot drift apart in either direction.
    /// </summary>
    [Fact]
    public void The_whep_hook_validates_the_audience_exactly_as_the_bearer_pipeline_does()
    {
        TokenValidationParameters whep = WhepAuthValidator.CreateParameters();
        TokenValidationParameters bearer = BearerOptions().TokenValidationParameters;

        whep.ValidateAudience.ShouldBe(
            bearer.ValidateAudience,
            customMessage: "the WHEP hook accepts tokens the nine APIs would refuse. A comment "
            + "claiming it mirrors them is the only thing that ever bound them, and it did not "
            + "survive one change (spec 071 FR-005).");
    }

    /// <summary>
    /// Parity on the audience itself. Both sides are materialised through a
    /// null-coalesce first: "no audience configured" arrives as a <b>null</b>
    /// collection, and letting Shouldly dereference it reports an
    /// <c>ArgumentNullException</c> instead of the missing audience — a trap that
    /// already cost spec 069 a debugging round.
    /// </summary>
    [Fact]
    public void The_whep_hook_names_the_same_api_as_the_bearer_pipeline()
    {
        IReadOnlyCollection<string> whep =
            [.. WhepAuthValidator.CreateParameters().ValidAudiences ?? []];
        IReadOnlyCollection<string> bearer =
            [.. BearerOptions().TokenValidationParameters.ValidAudiences ?? []];

        whep.ShouldBe(
            bearer,
            ignoreOrder: true,
            customMessage: "the WHEP hook and the nine APIs must name the same API. Read the "
            + "audience off the constant the bearer pipeline reads, so this hook cannot accept "
            + "a token they would refuse (spec 071 FR-002).");
    }

    /// <summary>
    /// Parity on where the issuer comes from (spec 089 FR-006). The bearer pipeline
    /// sets neither property and lets <c>JwtBearerHandler</c> fill them per request
    /// from the discovery document's <c>issuer</c>; the WHEP factory must leave the
    /// same two slots in the same state, so the request path is the only thing that
    /// decides which issuer a token is checked against.
    ///
    /// <para>
    /// <b>Both sides are read, neither is asserted as a <c>null</c> literal.</b> A
    /// constant asserted against itself passes whatever it is later changed to
    /// (spec 069's finding); comparing means a future edit that re-hardcodes the
    /// authority into <c>CreateParameters</c> fails here, and so does the bearer side
    /// starting to configure an issuer of its own. Materialised through a
    /// null-coalesce for the same reason the audience parity above is: an unset
    /// collection arrives as <b>null</b>, and letting Shouldly dereference it reports
    /// an <c>ArgumentNullException</c> instead of the difference.
    /// </para>
    ///
    /// <para>
    /// <b>It binds the factory, not the runtime object</b>, and does not stand alone —
    /// see the class doc comment and <see cref="WhepValidatorIssuerTests"/>, which
    /// drives the real <c>ValidateAsync</c>. This case cannot see the clone, so on its
    /// own it would be exactly the proof-of-nothing #2093 was filed about.
    /// </para>
    /// </summary>
    [Fact]
    public void The_whep_hook_leaves_the_issuer_to_discovery_exactly_as_the_bearer_pipeline_does()
    {
        TokenValidationParameters whep = WhepAuthValidator.CreateParameters();
        TokenValidationParameters bearer = BearerOptions().TokenValidationParameters;

        whep.ValidIssuer.ShouldBe(
            bearer.ValidIssuer,
            customMessage: "the WHEP hook pins an issuer the bearer pipeline leaves to discovery. "
            + "The configured authority is the URL the service dials Keycloak on; behind an "
            + "ingress that is not the issuer the realm mints, so every WHEP open 401s while the "
            + "nine REST APIs keep working (#2095 FR-001/FR-006).");

        IReadOnlyCollection<string> whepIssuers = [.. whep.ValidIssuers ?? []];
        IReadOnlyCollection<string> bearerIssuers = [.. bearer.ValidIssuers ?? []];

        whepIssuers.ShouldBe(
            bearerIssuers,
            ignoreOrder: true,
            customMessage: "the WHEP hook and the nine APIs must enter the request path with the "
            + "same set of pre-configured issuers. JwtBearerHandler concatenates the discovery "
            + "issuer onto this collection, so anything left here is accepted in addition to it "
            + "(#2095 FR-002).");
    }

    /// <summary>
    /// <b>The over-correction guard — green on arrival, by declaration</b> (plan.md
    /// Declaration 3). Green today for the wrong reason (validation is off) and green
    /// after the fix for the right one. It exists so the refusal above cannot be
    /// bought by validating an audience nothing names, which would 401 every kiosk in
    /// the fab.
    /// </summary>
    [Fact]
    public void A_whep_token_minted_for_this_api_is_accepted()
    {
        Should.NotThrow(() =>
            Validators.ValidateAudience(
                [AuthenticationDefaults.ApiAudience],
                securityToken: null,
                WhepAuthValidator.CreateParameters()));
    }

    /// <summary>
    /// The options the nine APIs receive, built through the real extension method.
    /// An <em>empty</em> builder deliberately: the default
    /// <c>Host.CreateApplicationBuilder</c> reads <c>appsettings.json</c> and the
    /// environment, either of which could supply an audience this test would then
    /// credit to <c>AddBearerAuthentication</c>. Mirrors
    /// <c>BearerAudienceTests.BearerOptions</c>.
    /// </summary>
    private static JwtBearerOptions BearerOptions()
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration["ConnectionStrings:keycloak"] = "https://keycloak.invalid";
        builder.AddBearerAuthentication();

        using ServiceProvider provider = builder.Services.BuildServiceProvider();
        return provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);
    }
}
