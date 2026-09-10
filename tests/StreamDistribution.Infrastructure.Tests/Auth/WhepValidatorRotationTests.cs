using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using SmartSentinelEye.ServiceDefaults;
using SmartSentinelEye.Shared.Kernel;
using SmartSentinelEye.StreamDistribution.Application.Auth;
using SmartSentinelEye.StreamDistribution.Infrastructure.Auth;

namespace SmartSentinelEye.StreamDistribution.Infrastructure.Tests.Auth;

/// <summary>
/// Issue #2161 — a signing-key rotation must not take twelve hours to reach the
/// wall.
///
/// <para>
/// <b>What is broken.</b> <c>WhepAuthValidator</c> reads issuer and signing keys
/// from a <see cref="ConfigurationManager{T}"/> whose
/// <c>AutomaticRefreshInterval</c> is twelve hours. <c>JwtBearerHandler</c> —
/// the pipeline the nine REST APIs sit behind — calls <c>RequestRefresh()</c>
/// when a token fails with signature-key-not-found, so those APIs re-read JWKS
/// inside the five-minute throttle floor. This hook never calls it. After
/// Keycloak rotates, every management page recovers within five minutes and the
/// wall keeps 401ing until the twelve-hour timer expires.
/// </para>
///
/// <para>
/// <b>Colours on arrival</b> (ADR-0139).
/// <see cref="A_token_signed_with_the_rotated_key_is_authorized_on_a_later_call"/>
/// and
/// <see cref="A_token_carrying_the_rotated_issuer_is_authorized_on_a_later_call"/>
/// are <b>RED</b>: nothing asks the manager to refresh, so the cached document is
/// still the pre-rotation one on every later call.
/// <see cref="A_token_signed_with_the_superseded_key_is_refused_once_the_rotation_has_landed"/>
/// is <b>GREEN</b>, and drives the rotation by calling <c>RequestRefresh()</c> on
/// the manager itself rather than through the validator — so it is a control that
/// proves the harness really rotates, not a second copy of the red. The restraint
/// half of #2161 — the failures a refresh cannot cure — lives in
/// <see cref="WhepValidatorRefreshRestraintTests"/>.
/// </para>
///
/// <para>
/// <b>Two key identifiers, deliberately.</b> The rotated key carries a
/// <c>kid</c> of its own, in the JWKS and in the token header, because that is
/// what Keycloak rotation does and it is what makes the failure
/// <c>SecurityTokenSignatureKeyNotFoundException</c> (IDX10503) — the "a fresher
/// document could cure this" condition. Reusing key A's <c>kid</c> would resolve
/// the key and fail with <c>SecurityTokenInvalidSignatureException</c> (IDX10511)
/// instead, which is a different condition and must <em>not</em> provoke a
/// refresh. Both were put through the real handler before these tests were
/// written. The issuer case keeps the key and its <c>kid</c> unchanged and moves
/// only <c>iss</c>, so it fails with
/// <c>SecurityTokenInvalidIssuerException</c> (IDX10205) and nothing else.
/// </para>
///
/// <para>
/// <b>Why acceptance is awaited rather than asserted on the literal second
/// call.</b> Microsoft.IdentityModel 8.19.2's <c>GetConfigurationAsync</c>
/// performs a due refresh on a <em>background</em> task and hands the caller that
/// triggered it the stale document. Measured on this harness: after an explicit
/// <c>RequestRefresh()</c> the second and third calls still returned the
/// pre-rotation document, and the fetch count only moved once the background task
/// had run. <see cref="ValidateUntilAcceptedAsync"/> therefore polls within a
/// bounded window instead of sleeping for a guessed interval. The poll cannot buy
/// a green on its own: with no refresh requested the manager's next sync is
/// twelve hours out, so repeated calls return the cached document and never
/// re-enter the retriever — also measured.
/// </para>
///
/// <para>
/// <b>Neither refresh interval is touched.</b> Setting
/// <c>AutomaticRefreshInterval</c> or <c>RefreshInterval</c> would make these
/// pass against the unfixed validator, which is exactly the false green #2161
/// forbids. The harness is <see cref="WhepValidatorIssuerTests"/>'s: a real
/// validator through the #2099 internal seam, a real
/// <see cref="ConfigurationManager{T}"/> over an in-memory
/// <see cref="IDocumentRetriever"/>, real RSA keys and real tokens. No socket is
/// opened and no hostname below is resolved.
/// </para>
/// </summary>
public sealed class WhepValidatorRotationTests : IDisposable
{
    private const string DialledAuthority = "http://keycloak:8080/realms/smart-sentinel-eye";
    private const string RealmIssuer = "https://keycloak.fab.example/realms/smart-sentinel-eye";
    private const string RotatedIssuer = "https://sso.fab.example/realms/smart-sentinel-eye";
    private const string JwksUri = DialledAuthority + "/protocol/openid-connect/certs";
    private const string OriginalKeyIdentifier = "whep-rotation-tests-key-a";
    private const string RotatedKeyIdentifier = "whep-rotation-tests-key-b";

    /// <summary>
    /// Generous enough for the library's background refresh of two in-memory
    /// documents, short enough that an unfixed validator fails in seconds.
    /// </summary>
    private static readonly TimeSpan RefreshLandingWindow = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    private readonly RSA originalKey = RSA.Create(2048);
    private readonly RSA rotatedKey = RSA.Create(2048);

    public void Dispose()
    {
        originalKey.Dispose();
        rotatedKey.Dispose();
    }

    /// <summary>
    /// <b>The red, and the whole of #2161.</b> The realm has rotated: the JWKS
    /// served on every fetch after the first carries key B alone. A token signed
    /// with key B is refused on the call that meets the stale document — correct,
    /// and deliberately pinned here, because the fix mirrors
    /// <c>JwtBearerHandler</c> and requests a refresh rather than retrying
    /// in-request. What must change is what happens next.
    /// </summary>
    [Fact]
    public async Task A_token_signed_with_the_rotated_key_is_authorized_on_a_later_call()
    {
        WhepAuthValidator validator = new(MetadataOver(RotatingKeys()), NullLogger<WhepAuthValidator>.Instance);
        string token = TokenFor(rotatedKey, RotatedKeyIdentifier, RealmIssuer);

        Result<WhepAuthSubject, WhepAuthFailure> firstCall = await validator.ValidateAsync(token, CancellationToken.None);
        Result<WhepAuthSubject, WhepAuthFailure> laterCall = await ValidateUntilAcceptedAsync(validator, token);

        firstCall.IsSuccess.ShouldBeFalse(
            customMessage: "the WHEP hook accepted a token signed with a key its cached discovery "
            + "document does not carry. Either the harness is not serving the pre-rotation JWKS, or "
            + "signing-key validation is off — in which case the acceptance below proves nothing.");
        laterCall.IsSuccess.ShouldBeTrue(
            customMessage: "the WHEP hook was still refusing a token signed with the realm's current "
            + "key seconds after meeting it. It never asked its ConfigurationManager to refresh, so "
            + "the rotated JWKS is not read until the twelve-hour automatic interval expires. The "
            + "nine REST APIs recover inside five minutes because JwtBearerHandler calls "
            + "RequestRefresh(); until this one does, every WHEP open 401s and the wall stays dark "
            + "for up to half a day after a routine Keycloak key rotation (#2161).");
    }

    /// <summary>
    /// <b>The second red — same mechanism, second field.</b> The realm moved
    /// behind a new hostname, so discovery reports a new <c>issuer</c> while the
    /// signing key is untouched. A stale issuer is as curable by a fresh
    /// discovery document as a stale key, and is refused for just as long today.
    /// </summary>
    [Fact]
    public async Task A_token_carrying_the_rotated_issuer_is_authorized_on_a_later_call()
    {
        WhepAuthValidator validator = new(MetadataOver(RotatingIssuer()), NullLogger<WhepAuthValidator>.Instance);
        string token = TokenFor(originalKey, OriginalKeyIdentifier, RotatedIssuer);

        Result<WhepAuthSubject, WhepAuthFailure> firstCall = await validator.ValidateAsync(token, CancellationToken.None);
        Result<WhepAuthSubject, WhepAuthFailure> laterCall = await ValidateUntilAcceptedAsync(validator, token);

        firstCall.IsSuccess.ShouldBeFalse(
            customMessage: "the WHEP hook accepted a token whose issuer its cached discovery document "
            + "does not name. Issuer validation is off, and the acceptance below proves nothing.");
        laterCall.IsSuccess.ShouldBeTrue(
            customMessage: "the WHEP hook was still refusing a token carrying the issuer the realm's "
            + "current discovery document reports. An issuer mismatch is curable by a fresher "
            + "document exactly as a missing signing key is, and the hook asks for neither — so a "
            + "realm that moves hostname darkens the wall for up to twelve hours (#2161).");
    }

    /// <summary>
    /// <b>The control, green on arrival and green after.</b> Rotation means the
    /// superseded key stops working, not that both keys work. This test drives
    /// the rotation through the manager's own <c>RequestRefresh()</c> — the call
    /// the fix will make — so it does not depend on the fix and cannot be the red
    /// in disguise. It fails if anyone reaches green by accumulating keys across
    /// refreshes rather than replacing them.
    /// </summary>
    [Fact]
    public async Task A_token_signed_with_the_superseded_key_is_refused_once_the_rotation_has_landed()
    {
        ConfigurationManager<OpenIdConnectConfiguration> metadata = MetadataOver(RotatingKeys());
        WhepAuthValidator validator = new(metadata, NullLogger<WhepAuthValidator>.Instance);
        string superseded = TokenFor(originalKey, OriginalKeyIdentifier, RealmIssuer);

        Result<WhepAuthSubject, WhepAuthFailure> beforeRotation = await validator.ValidateAsync(superseded, CancellationToken.None);
        metadata.RequestRefresh();
        Result<WhepAuthSubject, WhepAuthFailure> rotated = await ValidateUntilAcceptedAsync(
            validator, TokenFor(rotatedKey, RotatedKeyIdentifier, RealmIssuer));
        Result<WhepAuthSubject, WhepAuthFailure> afterRotation = await validator.ValidateAsync(superseded, CancellationToken.None);

        beforeRotation.IsSuccess.ShouldBeTrue(
            customMessage: "the harness never accepted key A even before the rotation, so refusing it "
            + "afterwards would be for a reason that has nothing to do with rotation.");
        rotated.IsSuccess.ShouldBeTrue(
            customMessage: "the rotation never landed after an explicit RequestRefresh(), so the stub "
            + "retriever is not serving a second JWKS and both red tests above are unprovable.");
        afterRotation.IsSuccess.ShouldBeFalse(
            customMessage: "the WHEP hook still accepts a token signed with the key the realm has "
            + "retired. A refresh must replace the signing keys, not add to them — otherwise the key "
            + "a rotation was meant to retire keeps opening streams (#2161).");
    }

    /// <summary>
    /// Calls the validator until it accepts or the window expires. Bounded, and
    /// never a fixed sleep: see the class note on 8.19.2's background refresh.
    /// </summary>
    private static async Task<Result<WhepAuthSubject, WhepAuthFailure>> ValidateUntilAcceptedAsync(
        WhepAuthValidator validator, string bearerToken)
    {
        DateTime deadline = DateTime.UtcNow.Add(RefreshLandingWindow);
        Result<WhepAuthSubject, WhepAuthFailure> subject =
            Result<WhepAuthSubject, WhepAuthFailure>.Failure(WhepAuthFailure.TokenRejected);

        while (!subject.IsSuccess && DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollInterval);
            subject = await validator.ValidateAsync(bearerToken, CancellationToken.None);
        }

        return subject;
    }

    /// <summary>
    /// A real manager on its shipped intervals — twelve hours automatic, five
    /// minutes throttle. Neither is touched; see the class note.
    /// </summary>
    private static ConfigurationManager<OpenIdConnectConfiguration> MetadataOver(IDocumentRetriever retriever) =>
        new($"{DialledAuthority}/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            retriever);

    /// <summary>Key A on the first fetch, key B on every later one.</summary>
    private RotatingMetadata RotatingKeys() => new(
        DiscoveryDocumentFor(RealmIssuer),
        DiscoveryDocumentFor(RealmIssuer),
        JsonWebKeySetFor(originalKey, OriginalKeyIdentifier),
        JsonWebKeySetFor(rotatedKey, RotatedKeyIdentifier));

    /// <summary>The old issuer on the first fetch, the new one after; key unchanged.</summary>
    private RotatingMetadata RotatingIssuer() => new(
        DiscoveryDocumentFor(RealmIssuer),
        DiscoveryDocumentFor(RotatedIssuer),
        JsonWebKeySetFor(originalKey, OriginalKeyIdentifier),
        JsonWebKeySetFor(originalKey, OriginalKeyIdentifier));

    private static string DiscoveryDocumentFor(string issuer) =>
        $$"""{"issuer":"{{issuer}}","jwks_uri":"{{JwksUri}}"}""";

    private static string JsonWebKeySetFor(RSA key, string keyIdentifier)
    {
        RSAParameters publicKey = key.ExportParameters(includePrivateParameters: false);
        string modulus = Base64UrlEncoder.Encode(publicKey.Modulus!);
        string exponent = Base64UrlEncoder.Encode(publicKey.Exponent!);

        return $$"""
            {"keys":[{"kty":"RSA","use":"sig","alg":"RS256","kid":"{{keyIdentifier}}","n":"{{modulus}}","e":"{{exponent}}"}]}
            """;
    }

    /// <summary>
    /// Audience, lifetime and claims are held constant across every case, so an
    /// outcome is attributable to the key or the issuer and to nothing else.
    /// </summary>
    private static string TokenFor(RSA key, string keyIdentifier, string issuer)
    {
        SigningCredentials credentials = new(
            new RsaSecurityKey(key) { KeyId = keyIdentifier },
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
    /// Serves one pair of documents on the first discovery fetch and another on
    /// every later one — the realm rotating between two reads. The retriever is
    /// asked for discovery before the keys within a refresh cycle, so the
    /// discovery count selects the generation for both.
    /// </summary>
    private sealed class RotatingMetadata(
        string discoveryBefore,
        string discoveryAfter,
        string keysBefore,
        string keysAfter) : IDocumentRetriever
    {
        private int discoveryFetches;

        public Task<string> GetDocumentAsync(string address, CancellationToken cancel)
        {
            if (address == JwksUri)
            {
                return Task.FromResult(Volatile.Read(ref discoveryFetches) <= 1 ? keysBefore : keysAfter);
            }

            return Task.FromResult(
                Interlocked.Increment(ref discoveryFetches) <= 1 ? discoveryBefore : discoveryAfter);
        }
    }
}
