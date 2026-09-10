using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using SmartSentinelEye.ServiceDefaults;
using SmartSentinelEye.Shared.Kernel;
using SmartSentinelEye.StreamDistribution.Application.Auth;
using SmartSentinelEye.StreamDistribution.Infrastructure.Auth;

namespace SmartSentinelEye.StreamDistribution.Infrastructure.Tests.Auth;

/// <summary>
/// Spec 119 (issue #2160) — a realm that cannot be reached is a refusal, not a
/// crash.
///
/// <para>
/// <b>What is broken.</b> <c>ValidateAsync</c> awaits
/// <c>GetConfigurationAsync</c> directly. <c>ConfigurationManager</c> wraps
/// whatever the retriever threw in <c>InvalidOperationException</c> (IDX20803),
/// which is neither <c>SecurityTokenException</c> nor <c>ArgumentException</c>,
/// so it escapes both catches, escapes the handler, and escapes the endpoint —
/// MediaMTX gets a 500 while every other refusal on this hook is a typed
/// <c>ApiError</c>.
/// </para>
///
/// <para>
/// <b>No Docker, no network, no realm</b> — the metadata seam from #2099. The
/// retriever here throws the same <c>IOException</c> shape
/// <c>HttpDocumentRetriever</c> throws on a refused connection, so the exception
/// the validator meets is the production one, built by the production
/// <c>ConfigurationManager</c>.
/// </para>
/// </summary>
public sealed class WhepValidatorUnreachableRealmTests : IDisposable
{
    private const string Authority = "https://keycloak.invalid/realms/smart-sentinel-eye";

    private const string JwksUri = Authority + "/protocol/openid-connect/certs";

    private const string SigningKeyIdentifier = "whep-validator-unreachable-tests";

    private readonly RSA signingKey = RSA.Create(2048);

    public void Dispose() => signingKey.Dispose();

    /// <summary>
    /// <b>The red.</b> Today this does not fail an assertion — it throws
    /// IDX20803 out of <c>ValidateAsync</c>, which is the defect itself.
    /// </summary>
    [Fact]
    public async Task An_unreachable_realm_refuses_instead_of_throwing()
    {
        WhepAuthValidator validator = ValidatorOver(new UnreachableRealm());

        Result<WhepAuthSubject, WhepAuthFailure> outcome =
            await validator.ValidateAsync(AToken(), CancellationToken.None);

        outcome.IsFailure.ShouldBeTrue(
            customMessage: "a token was authorized while the realm that vouches for it could not be "
            + "reached. Nothing may be admitted on a token nothing checked (spec 119 FR-001).");
        outcome.Error.ShouldBe(
            WhepAuthFailure.IdentityProviderUnavailable,
            customMessage: "an unreachable realm was reported as a rejected token, so the hook tells "
            + "MediaMTX the credential is bad while Keycloak is the thing that is down "
            + "(spec 119 FR-002).");
    }

    /// <summary>
    /// <b>Cancellation stays cancellation.</b> Separate from the case above
    /// because the two arrive as the <em>same</em> exception type:
    /// <c>ConfigurationManager</c> rewraps a cancelled fetch as IDX20803 too. A
    /// fix written as a bare catch turns a caller that went away into a 401 —
    /// this is the test that says so, and it was run against exactly that
    /// mistake before being committed.
    /// </summary>
    [Fact]
    public async Task A_cancelled_request_stays_cancelled()
    {
        WhepAuthValidator validator = ValidatorOver(new CancellingRealm());
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => validator.ValidateAsync(AToken(), cancelled.Token));
    }

    /// <summary>
    /// The control: the identical harness with a realm that answers accepts the
    /// identical token. Without it, the refusal above could be bought by a
    /// broken token, a stale <c>exp</c> or a mismatched issuer rather than by
    /// the outage.
    /// </summary>
    [Fact]
    public async Task A_reachable_realm_still_authorizes_the_same_token()
    {
        WhepAuthValidator validator = ValidatorOver(new ReachableRealm(DiscoveryDocument, JsonWebKeySet()));

        Result<WhepAuthSubject, WhepAuthFailure> outcome =
            await validator.ValidateAsync(AToken(), CancellationToken.None);

        outcome.IsSuccess.ShouldBeTrue(
            customMessage: "the harness itself refuses this token, so the outage cases above prove "
            + "nothing about the outage.");
    }

    private static WhepAuthValidator ValidatorOver(IDocumentRetriever retriever) =>
        new(new ConfigurationManager<OpenIdConnectConfiguration>(
            $"{Authority}/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            retriever));

    private static string DiscoveryDocument =>
        $$"""{"issuer":"{{Authority}}","jwks_uri":"{{JwksUri}}"}""";

    private string JsonWebKeySet()
    {
        RSAParameters publicKey = signingKey.ExportParameters(includePrivateParameters: false);
        string modulus = Base64UrlEncoder.Encode(publicKey.Modulus!);
        string exponent = Base64UrlEncoder.Encode(publicKey.Exponent!);

        return $$"""
            {"keys":[{"kty":"RSA","use":"sig","alg":"RS256","kid":"{{SigningKeyIdentifier}}","n":"{{modulus}}","e":"{{exponent}}"}]}
            """;
    }

    private string AToken()
    {
        SigningCredentials credentials = new(
            new RsaSecurityKey(signingKey) { KeyId = SigningKeyIdentifier },
            SecurityAlgorithms.RsaSha256);

        JwtSecurityToken token = new(
            issuer: Authority,
            audience: AuthenticationDefaults.ApiAudience,
            claims: [new Claim("sub", "kiosk-operator"), new Claim("scope", "sse.streams.read")],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// The shape <c>HttpDocumentRetriever</c> throws when the socket is refused:
    /// IDX20804 wrapping the transport failure. <c>ConfigurationManager</c> turns
    /// it into IDX20803, exactly as it does in a fab whose Keycloak is restarting.
    /// </summary>
    private sealed class UnreachableRealm : IDocumentRetriever
    {
        public Task<string> GetDocumentAsync(string address, CancellationToken cancel) =>
            throw new IOException(
                $"IDX20804: Unable to retrieve document from: '{address}'.",
                new HttpRequestException("No connection could be made because the target machine actively refused it."));
    }

    private sealed class CancellingRealm : IDocumentRetriever
    {
        public Task<string> GetDocumentAsync(string address, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            return Task.FromResult(DiscoveryDocument);
        }
    }

    private sealed class ReachableRealm(string discovery, string keys) : IDocumentRetriever
    {
        public Task<string> GetDocumentAsync(string address, CancellationToken cancel) =>
            Task.FromResult(address == JwksUri ? keys : discovery);
    }
}
