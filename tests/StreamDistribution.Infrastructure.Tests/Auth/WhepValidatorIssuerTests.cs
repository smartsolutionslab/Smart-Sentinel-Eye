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
/// Spec 089 / issue #2095 — the WHEP hook takes its issuer from the realm's
/// discovery document, not from the URL it dials the realm on.
///
/// <para>
/// <b>What is broken.</b> <c>WhepAuthValidator.CreateParameters</c> sets
/// <c>ValidIssuer</c> to the configured authority — the URL the service dials
/// Keycloak on, derived from <c>ConnectionStrings:keycloak</c>. The bearer
/// pipeline sets neither <c>ValidIssuer</c> nor <c>ValidIssuers</c> and lets
/// <c>JwtBearerHandler</c> fill them, per request, from the discovery
/// document's <c>issuer</c>. The two strings are equal only while Keycloak's
/// <c>iss</c> equals the URL it is reached on. Behind an ingress
/// (<c>KC_HOSTNAME=https://keycloak.fab.example</c>, connection string
/// <c>http://keycloak:8080</c>) they are not, and every WHEP open 401s — the
/// whole wall goes dark while every management page still loads.
/// </para>
///
/// <para>
/// <b>Why the fix must move the issuer rather than widen it.</b>
/// <c>JwtBearerHandler</c> <em>concatenates</em> the discovery issuer onto
/// whatever <c>ValidIssuers</c> already holds, and because the bearer side
/// starts null, the nine REST APIs today accept <em>exactly one</em> issuer.
/// Keeping the dialled URL alongside the discovery issuer would make this hook
/// <b>more</b> permissive than the nine APIs — the inverse of spec 071's rule.
/// <see cref="A_token_carrying_the_dialled_url_as_its_issuer_is_refused"/> is
/// what pins that, and it also fails if anyone reaches parity by setting
/// <c>ValidateIssuer = false</c>.
/// </para>
///
/// <para>
/// <b>Colours on arrival, stated rather than left to be inferred</b>
/// (ADR-0139). Two of the three cases below are <b>RED</b> on
/// <c>develop</c>: the ingress acceptance, and the dialled-URL refusal — the
/// latter because <c>ValidIssuer</c> <em>is</em> the dialled URL today, so a
/// token carrying it is accepted. plan.md predicted that second case green;
/// it is not, and the correction is recorded here rather than in the file it
/// would have to be re-derived from.
/// <see cref="A_token_carrying_an_unrelated_issuer_is_refused"/> is the only
/// one green on arrival, and it is green for the wrong reason — that issuer is
/// neither string, so it fails whichever of the two the hook is comparing
/// against. It is the control that stops the acceptance case from being bought
/// by validating nothing.
/// </para>
///
/// <para>
/// <b>Why this is a unit test and not an Aspire-fixture one</b> (ADR-0103).
/// The fixture reaches Keycloak through the Aspire proxy and the realm's
/// <c>iss</c> is that same proxied endpoint by construction — that is the
/// recorded "Keycloak issuer must match the proxied port" trap. There is no
/// token in that stack whose <c>iss</c> differs from the dialled URL, and
/// provoking one means setting <c>KC_HOSTNAME</c> in <c>AppHost</c>, which
/// every test in <c>AspireCollection</c> shares and which would break token
/// minting for all of them. The divergence is two string constants here.
/// </para>
///
/// <para>
/// <b>The harness is <see cref="WhepValidatorAudienceTests"/>'s</b>, deliberately
/// reused rather than reinvented: a real <c>WhepAuthValidator</c>, its private
/// <c>oidc</c> field replaced with a <see cref="ConfigurationManager{T}"/> over
/// an in-memory <see cref="IDocumentRetriever"/>, and real tokens signed by a
/// generated RSA key. No Docker, no network, no realm — and neither hostname
/// below is ever resolved. Keep the three files together: this one and
/// <see cref="WhepValidatorAudienceTests"/> drive the real <c>ValidateAsync</c>,
/// while <see cref="WhepAudienceTests"/> binds the factory that seeds it.
/// </para>
/// </summary>
public sealed class WhepValidatorIssuerTests : IDisposable
{
    /// <summary>
    /// The in-cluster URL the service dials Keycloak on — what
    /// <c>WhepAuthOptions.Authority</c> is set to, and the address the
    /// <see cref="ConfigurationManager{T}"/> asks for discovery at. It is
    /// <b>not</b> an accepted issuer (FR-002): it addresses discovery and
    /// nothing else.
    /// </summary>
    private const string DialledAuthority = "http://keycloak:8080/realms/smart-sentinel-eye";

    /// <summary>
    /// The issuer the realm actually mints, as the discovery document reports
    /// it — the ingress hostname, deliberately a different host <em>and</em> a
    /// different scheme from <see cref="DialledAuthority"/>.
    /// </summary>
    private const string RealmIssuer = "https://keycloak.fab.example/realms/smart-sentinel-eye";

    /// <summary>
    /// Stays on the dialled address: the JWKS is fetched over the same
    /// in-cluster connection the discovery document was, exactly as it is in a
    /// real ingress deployment.
    /// </summary>
    private const string JwksUri = DialledAuthority + "/protocol/openid-connect/certs";

    private const string SigningKeyIdentifier = "whep-validator-issuer-tests";

    /// <summary>
    /// The one private field these tests reach into, resolved once with a
    /// message that names the rename if it stops existing. Same lookup as
    /// <see cref="WhepValidatorAudienceTests"/>; a second spelling of it would
    /// be the drift the two files are meant to avoid.
    /// </summary>
    private static readonly FieldInfo OidcField =
        typeof(WhepAuthValidator).GetField("oidc", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "WhepAuthValidator no longer has a private 'oidc' field. These tests stub the OIDC "
            + "metadata through it so ValidateAsync can run without a realm; point them at the "
            + "renamed field rather than deleting them (#2095).");

    private readonly RSA signingKey = RSA.Create(2048);

    public void Dispose() => signingKey.Dispose();

    /// <summary>
    /// <b>The red.</b> A token carrying the issuer the realm actually mints,
    /// which is not the URL the service dials it on. This is the ingress shape,
    /// and it is the whole of #2095.
    /// </summary>
    [Fact]
    public async Task A_token_carrying_the_realms_issuer_is_authorized_when_it_differs_from_the_dialled_url()
    {
        WhepAuthValidator validator = ValidatorWithStubbedMetadata();

        Option<WhepAuthSubject> subject =
            await validator.ValidateAsync(TokenIssuedBy(RealmIssuer), CancellationToken.None);

        subject.HasValue.ShouldBeTrue(
            customMessage: "the WHEP hook refused a token minted by the realm it validates against. "
            + "It is comparing 'iss' to the URL it dials Keycloak on instead of to the issuer the "
            + "discovery document reports, so behind an ingress every WHEP open 401s and the whole "
            + "wall goes dark while every management page still loads (#2095 FR-001).");
    }

    /// <summary>
    /// <b>The anti-relaxation guard.</b> Same harness, same key, same audience,
    /// only <c>iss</c> differs: the dialled URL, which discovery says is not the
    /// issuer. It fails if the fix keeps the configured URL as an additional
    /// accepted issuer, and it fails if the fix turns <c>ValidateIssuer</c> off.
    /// Red on <c>develop</c> — see the colour note on the class.
    /// </summary>
    [Fact]
    public async Task A_token_carrying_the_dialled_url_as_its_issuer_is_refused()
    {
        WhepAuthValidator validator = ValidatorWithStubbedMetadata();

        Option<WhepAuthSubject> subject =
            await validator.ValidateAsync(TokenIssuedBy(DialledAuthority), CancellationToken.None);

        subject.HasValue.ShouldBeFalse(
            customMessage: "the WHEP hook accepted a token whose issuer is the URL it dials "
            + "Keycloak on, which the discovery document says is not the realm's issuer. The nine "
            + "REST APIs accept exactly one issuer — JwtBearerHandler concatenates onto a null — so "
            + "keeping the dialled URL alongside the discovery issuer makes this hook more "
            + "permissive than they are, which inverts spec 071's rule (#2095 FR-002/FR-003).");
    }

    /// <summary>
    /// The control: an issuer that is neither string, signed by the same key.
    /// It stops the acceptance case above from being satisfied by a hook that
    /// validates the issuer not at all.
    /// </summary>
    [Fact]
    public async Task A_token_carrying_an_unrelated_issuer_is_refused()
    {
        WhepAuthValidator validator = ValidatorWithStubbedMetadata();

        Option<WhepAuthSubject> subject = await validator.ValidateAsync(
            TokenIssuedBy("https://keycloak.attacker.example/realms/smart-sentinel-eye"),
            CancellationToken.None);

        subject.HasValue.ShouldBeFalse(
            customMessage: "the WHEP hook accepted a token from an issuer neither the discovery "
            + "document nor the configuration names. Issuer validation is off, or is comparing "
            + "against something that matches anything (#2095 FR-003).");
    }

    /// <summary>
    /// A real validator, with only its metadata source replaced. The
    /// <see cref="ConfigurationManager{T}"/> is addressed at
    /// <see cref="DialledAuthority"/> — as the constructor addresses it — while
    /// the document served from there reports <see cref="RealmIssuer"/>. That
    /// split is the ingress shape, and it is the only thing these tests change.
    /// </summary>
    private WhepAuthValidator ValidatorWithStubbedMetadata()
    {
        WhepAuthValidator validator =
            new(Options.Create(new WhepAuthOptions { Authority = DialledAuthority }));

        ConfigurationManager<OpenIdConnectConfiguration> metadata = new(
            $"{DialledAuthority}/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            new StubbedMetadata(DiscoveryDocument, JsonWebKeySet()));

        OidcField.SetValue(validator, metadata);
        return validator;
    }

    /// <summary>
    /// Served from the dialled address, and reporting a different host as the
    /// issuer. This is precisely what Keycloak does behind an ingress.
    /// </summary>
    private static string DiscoveryDocument =>
        $$"""{"issuer":"{{RealmIssuer}}","jwks_uri":"{{JwksUri}}"}""";

    /// <summary>
    /// The public half of <see cref="signingKey"/>, in the shape the OIDC
    /// retriever expects, so signature and signing-key validation pass for real
    /// and only the issuer is left to decide the outcome.
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

    /// <summary>
    /// Everything but <c>iss</c> is held constant across the three cases —
    /// audience, key, lifetime and claims — so each outcome is attributable to
    /// the issuer and to nothing else.
    /// </summary>
    private string TokenIssuedBy(string issuer)
    {
        SigningCredentials credentials = new(
            new RsaSecurityKey(signingKey) { KeyId = SigningKeyIdentifier },
            SecurityAlgorithms.RsaSha256);

        JwtSecurityToken token = new(
            issuer: issuer,
            audience: AuthenticationDefaults.ApiAudience,
            claims: [new Claim("sub", "kiosk-operator"), new Claim("scope", "sse.streams.read")],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Serves the two documents the OIDC retriever asks for, by address. No
    /// socket is opened and neither hostname is resolved.
    /// </summary>
    private sealed class StubbedMetadata(string discovery, string keys) : IDocumentRetriever
    {
        public Task<string> GetDocumentAsync(string address, CancellationToken cancel) =>
            Task.FromResult(address == JwksUri ? keys : discovery);
    }
}
