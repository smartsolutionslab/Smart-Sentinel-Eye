using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using SmartSentinelEye.ServiceDefaults;
using SmartSentinelEye.Shared.Kernel;
using SmartSentinelEye.StreamDistribution.Application.Auth;
using SmartSentinelEye.StreamDistribution.Infrastructure.Auth;

namespace SmartSentinelEye.StreamDistribution.Infrastructure.Tests.Auth;

/// <summary>
/// Issue #2093 — the audience check is asserted on the object the runtime
/// validates with, not on the factory that builds it.
///
/// <para>
/// <b>The gap these close.</b> <see cref="WhepAudienceTests"/> pins
/// <c>WhepAuthValidator.CreateParameters</c> to the bearer pipeline, and nothing
/// pins that factory to what <c>ValidateAsync</c> actually hands the handler.
/// <c>ValidateAsync</c> takes <c>parameters.Clone()</c> and mutates the clone
/// before validating: adding <c>validationParameters.ValidateAudience = false;</c>
/// after that clone leaves all four factory tests, <c>BearerAudienceTests</c> and
/// every WHEP integration test green — the integration suite has only positive
/// cases — while the audience check spec 071 exists to restore is silently gone
/// again. The two tests below fail on that line, which is the whole point of
/// them; the counterfactual was run both ways before they were committed.
/// </para>
///
/// <para>
/// <b>Why they are a pair.</b> The refusal alone would also pass if the harness
/// were broken — an unsigned token, a stale <c>exp</c>, a mismatched issuer all
/// produce the same <c>None</c>. The acceptance case runs the identical harness
/// with only the <c>aud</c> claim changed, so the refusal is attributable to the
/// audience and to nothing else. It is the same over-correction guard
/// <c>WhepAudienceTests.A_whep_token_minted_for_this_api_is_accepted</c> holds one
/// level down.
/// </para>
///
/// <para>
/// <b>No Docker, no network, no realm.</b> The validator's
/// <see cref="ConfigurationManager{T}"/> is built in its constructor with a
/// non-injectable <c>HttpDocumentRetriever</c>, so the OIDC metadata is stubbed by
/// replacing the field: the discovery document and JWKS are served from memory and
/// the signing key is generated here. Reflecting on a private field of our own
/// type is the cost of that; a rename fails these tests loudly with a message
/// saying so, rather than skipping them.
/// </para>
///
/// <para>
/// <b>No spec directory.</b> A <c>tasks.md</c> would exceed the work — this is one
/// test file and one corrected comment against an already-analysed issue — so the
/// reasoning is recorded here instead, which is also where the next reader of
/// these tests will look for it.
/// </para>
/// </summary>
public sealed class WhepValidatorAudienceTests : IDisposable
{
    /// <summary>
    /// Any well-formed authority. Nothing resolves this host: the metadata is
    /// stubbed, and the stubbed discovery document reports this same string as
    /// <c>issuer</c> (see <see cref="DiscoveryDocument"/>), which
    /// <c>ValidateAsync</c> reads into <c>ValidIssuers</c> — there is no
    /// <c>ValidIssuer</c> any more (spec 089) — so issuer validation passes on
    /// its own merits.
    /// </summary>
    private const string Authority = "https://keycloak.invalid/realms/smart-sentinel-eye";

    private const string JwksUri = Authority + "/protocol/openid-connect/certs";

    private const string SigningKeyIdentifier = "whep-validator-audience-tests";

    /// <summary>
    /// The one private field these tests reach into. Resolved once, with a message
    /// that names the rename if it ever stops existing — a test that quietly stops
    /// covering the runtime object is the exact failure mode #2093 is about.
    /// </summary>
    private static readonly FieldInfo OidcField =
        typeof(WhepAuthValidator).GetField("oidc", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "WhepAuthValidator no longer has a private 'oidc' field. These tests stub the OIDC "
            + "metadata through it so ValidateAsync can run without a realm; point them at the "
            + "renamed field rather than deleting them (#2093).");

    private readonly RSA signingKey = RSA.Create(2048);

    public void Dispose() => signingKey.Dispose();

    /// <summary>
    /// <b>The binding itself.</b> Drives the real <c>ValidateAsync</c>, so the
    /// assertion is about the <see cref="TokenValidationParameters"/> the handler
    /// receives — the clone — and not the factory that seeded them.
    /// </summary>
    [Fact]
    public async Task The_validator_refuses_a_token_minted_for_another_api()
    {
        WhepAuthValidator validator = ValidatorWithStubbedMetadata();

        Option<WhepAuthSubject> subject =
            await validator.ValidateAsync(TokenFor("some-other-api"), CancellationToken.None);

        subject.HasValue.ShouldBeFalse(
            customMessage: "the WHEP hook authorized a token minted for another API. The audience "
            + "settings must survive from CreateParameters into the parameters ValidateAsync hands "
            + "the handler; WhepAudienceTests only inspects the factory, so a mutation on the clone "
            + "leaves it green (#2093).");
    }

    /// <summary>
    /// The over-correction guard, and the control for the refusal above: same
    /// harness, same key, same issuer, only the <c>aud</c> claim differs. Without
    /// it the refusal could be bought by any broken token and every kiosk in the
    /// fab would 401.
    /// </summary>
    [Fact]
    public async Task The_validator_accepts_a_token_minted_for_this_api()
    {
        WhepAuthValidator validator = ValidatorWithStubbedMetadata();

        Option<WhepAuthSubject> subject = await validator.ValidateAsync(
            TokenFor(AuthenticationDefaults.ApiAudience), CancellationToken.None);

        subject.HasValue.ShouldBeTrue(
            customMessage: "the WHEP hook refused a token minted for this API. Either the audience "
            + "the hook names has drifted from AuthenticationDefaults.ApiAudience, or the refusal "
            + "test above is passing for a reason other than the audience (#2093).");
    }

    /// <summary>
    /// A real validator, with only its metadata source replaced. The constructor
    /// dials nothing, so the swap happens before the first token is validated.
    /// </summary>
    private WhepAuthValidator ValidatorWithStubbedMetadata()
    {
        WhepAuthValidator validator = new(Options.Create(new WhepAuthOptions { Authority = Authority }));

        ConfigurationManager<OpenIdConnectConfiguration> metadata = new(
            $"{Authority}/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            new StubbedMetadata(DiscoveryDocument, JsonWebKeySet()));

        OidcField.SetValue(validator, metadata);
        return validator;
    }

    private static string DiscoveryDocument =>
        $$"""{"issuer":"{{Authority}}","jwks_uri":"{{JwksUri}}"}""";

    /// <summary>
    /// The public half of <see cref="signingKey"/>, in the shape the OIDC
    /// retriever expects, so <c>ValidateIssuerSigningKey</c> passes for real.
    /// </summary>
    private string JsonWebKeySet()
    {
        RSAParameters publicKey = signingKey.ExportParameters(includePrivateParameters: false);
        string modulus = Base64UrlEncoder.Encode(publicKey.Modulus!);
        string exponent = Base64UrlEncoder.Encode(publicKey.Exponent!);

        return $$"""
            {"keys":[{"kty":"RSA","use":"sig","alg":"RS256","kid":"{{SigningKeyIdentifier}}","n":"{{modulus}}","e":"{{exponent}}"}]}
            """;
    }

    private string TokenFor(string audience)
    {
        SigningCredentials credentials = new(
            new RsaSecurityKey(signingKey) { KeyId = SigningKeyIdentifier },
            SecurityAlgorithms.RsaSha256);

        JwtSecurityToken token = new(
            issuer: Authority,
            audience: audience,
            claims: [new Claim("sub", "kiosk-operator"), new Claim("scope", "sse.streams.view")],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Serves the two documents the OIDC retriever asks for, by address. No
    /// socket is opened and no host is resolved.
    /// </summary>
    private sealed class StubbedMetadata(string discovery, string keys) : IDocumentRetriever
    {
        public Task<string> GetDocumentAsync(string address, CancellationToken cancel) =>
            Task.FromResult(address == JwksUri ? keys : discovery);
    }
}
