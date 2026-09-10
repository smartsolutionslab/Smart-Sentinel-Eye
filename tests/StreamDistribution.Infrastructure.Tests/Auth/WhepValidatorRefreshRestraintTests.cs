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
/// Issue #2161, the restraint half. <see cref="WhepValidatorRotationTests"/>
/// says the hook must ask for a fresher discovery document when a fresher one
/// would cure the failure. These say it must not ask when nothing would.
///
/// <para>
/// <b>Why the restraint is the load-bearing half.</b> The obvious fix — call
/// <c>RequestRefresh()</c> in the <c>catch</c>, on every failure — passes every
/// test in the rotation file. It also turns a token flood into a refresh storm
/// against Keycloak: an expired kiosk token replayed by a reconnect loop, or a
/// scanner spraying garbage at the MediaMTX hook, would drive a JWKS re-read on
/// the heels of every rejection. The five-minute <c>RefreshInterval</c> floor
/// throttles that to twelve reads an hour per instance rather than stopping it,
/// which is precisely why the floor is not a substitute for asking only when it
/// helps. <c>JwtBearerHandler</c> discriminates the same way: it requests a
/// refresh on signature-key-not-found, not on every rejected token.
/// </para>
///
/// <para>
/// <b>Colours on arrival</b> (ADR-0139). All three are <b>GREEN</b>, and that is
/// correct: today the hook refreshes on nothing at all, so it trivially does not
/// refresh on these. They are written now because they are what stops the
/// rotation reds being paid for with a blanket refresh — they go <b>RED</b>
/// against exactly that implementation, which is the discrimination they exist
/// to make.
/// </para>
///
/// <para>
/// <b>Why the assertion cannot pass vacuously.</b> It observes the stub
/// retriever's discovery-fetch count, not a mock's call log, so only a real
/// re-read moves it. Each test builds a fresh <see cref="ConfigurationManager{T}"/>
/// that has had no prior refresh request, and 8.19.2's <c>RequestRefresh()</c>
/// never throttles the <em>first</em> request on an instance — measured on this
/// harness, along with the throttling of the second inside the five-minute
/// window. So a blanket-refresh implementation would be refreshing on its
/// unthrottled first opportunity, and the count would move. The second
/// <c>ValidateAsync</c> is what makes it observable at all: <c>RequestRefresh()</c>
/// only marks the cache due, and the fetch happens on the next
/// <c>GetConfigurationAsync</c>.
/// </para>
///
/// <para>
/// <b>The settle window.</b> 8.19.2 performs a due refresh on a background task,
/// so a count read immediately after the second call would read one whether or
/// not a refresh was requested — a vacuous green. <see cref="FetchesAfterSettlingAsync"/>
/// waits for the count to move, up to a bounded window, and only then reads it:
/// re-reading two in-memory documents takes milliseconds, so a window in seconds
/// is decisive.
/// </para>
///
/// <para>
/// <b>A known limit, stated rather than left implied.</b> One failure that a
/// fresher document <em>could</em> in principle cure is deliberately not
/// refreshed on and is <b>not covered by any test here</b>: a rotation that
/// reuses its <c>kid</c>, which raises
/// <c>SecurityTokenInvalidSignatureException</c> (IDX10511). There the key
/// resolved and only the signature did not match, so the document the hook holds
/// is the one the realm published. Keycloak issues a new <c>kid</c> on rotation,
/// which is why this is a limit and not a defect — but it is a limit, and the
/// evidence for the discriminator stops short of it (#2161).
/// </para>
/// </summary>
public sealed class WhepValidatorRefreshRestraintTests : IDisposable
{
    private const string DialledAuthority = "http://keycloak:8080/realms/smart-sentinel-eye";
    private const string RealmIssuer = "https://keycloak.fab.example/realms/smart-sentinel-eye";
    private const string JwksUri = DialledAuthority + "/protocol/openid-connect/certs";
    private const string SigningKeyIdentifier = "whep-restraint-tests-key";

    /// <summary>
    /// How long a re-read is given to show up before the count is read. Well
    /// beyond what two in-memory documents take, and it is only ever waited out
    /// in full when the correct answer is "no re-read happened".
    /// </summary>
    private static readonly TimeSpan RefetchWindow = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    private readonly RSA signingKey = RSA.Create(2048);

    public void Dispose() => signingKey.Dispose();

    /// <summary>
    /// A token the realm really minted, with a real signature and a live
    /// lifetime, for a different API. Nothing in a fresher discovery document
    /// changes the audience this hook requires, so nothing about it is worth a
    /// round trip to Keycloak. Fails with
    /// <c>SecurityTokenInvalidAudienceException</c> (IDX10214).
    /// </summary>
    [Fact]
    public async Task A_token_minted_for_another_audience_does_not_provoke_a_discovery_refetch()
    {
        await DiscoveryIsNotRefetchedAfterAsync(
            TokenFor("smart-sentinel-eye-reporting", DateTime.UtcNow.AddMinutes(5)),
            "minted for another API's audience");
    }

    /// <summary>
    /// The kiosk reconnect loop's worst case: a token that was valid and is not
    /// any more, replayed. The realm's keys are not in question and a re-read
    /// would return the document the hook already holds. Fails with
    /// <c>SecurityTokenExpiredException</c> (IDX10223).
    /// </summary>
    [Fact]
    public async Task An_expired_token_does_not_provoke_a_discovery_refetch()
    {
        await DiscoveryIsNotRefetchedAfterAsync(
            TokenFor(AuthenticationDefaults.ApiAudience, DateTime.UtcNow.AddMinutes(-5)),
            "past its expiry");
    }

    /// <summary>
    /// Nothing that reaches the signature stage at all — the shape a scanner
    /// sends. Fails with <c>SecurityTokenMalformedException</c> (IDX12741), which
    /// no discovery document can address.
    ///
    /// <para>
    /// <b>This case does not exercise the discriminator, and must not be cited as
    /// if it did.</b> In Microsoft.IdentityModel 8.19.2
    /// <c>SecurityTokenMalformedException</c> derives from <c>ArgumentException</c>
    /// (via <c>SecurityTokenArgumentException</c>), not from
    /// <c>SecurityTokenException</c>, so it is caught by the second arm of
    /// <c>ValidateAsync</c> and never reaches
    /// <c>RequestRefreshIfAStaleDocumentCouldExplain</c> at all. Measured rather
    /// than read off the hierarchy: under an unconditional refresh in the
    /// <c>SecurityTokenException</c> arm the other two cases here move to two
    /// fetches and this one stays at one. What it guards is the
    /// <c>ArgumentException</c> arm — a refresh added there fails it — which is a
    /// real guard over a different line of code.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_malformed_token_does_not_provoke_a_discovery_refetch()
    {
        await DiscoveryIsNotRefetchedAfterAsync("not-a-token", "not a JWT at all");
    }

    /// <summary>
    /// Two calls, because a refresh request only marks the cache due and the
    /// re-read happens on the call after. Then the fetch count, read once the
    /// background task has had its window.
    /// </summary>
    private async Task DiscoveryIsNotRefetchedAfterAsync(string bearerToken, string failure)
    {
        CountingMetadata retriever = new(DiscoveryDocument, JsonWebKeySet());
        WhepAuthValidator validator = new(MetadataOver(retriever), NullLogger<WhepAuthValidator>.Instance);

        Result<WhepAuthSubject, WhepAuthFailure> firstCall = await validator.ValidateAsync(bearerToken, CancellationToken.None);
        Result<WhepAuthSubject, WhepAuthFailure> secondCall = await validator.ValidateAsync(bearerToken, CancellationToken.None);
        int fetches = await FetchesAfterSettlingAsync(retriever);

        firstCall.IsSuccess.ShouldBeFalse(
            customMessage: $"the WHEP hook authorized a stream for a token {failure}. The fetch-count "
            + "assertion below is then meaningless: it would be measuring the restraint of a hook "
            + "that never rejected anything.");
        secondCall.IsSuccess.ShouldBeFalse(
            customMessage: $"the WHEP hook authorized a stream for a token {failure} on the second "
            + "attempt, having refused the first. Validation is not deterministic for a fixed token.");
        fetches.ShouldBe(
            1,
            customMessage: $"the WHEP hook re-read the realm's discovery document after a token {failure} "
            + "— a failure no fresher document can cure. Refreshing on every rejection turns a "
            + "reconnect loop replaying a stale token, or a scanner spraying garbage at the MediaMTX "
            + "hook, into a JWKS storm that the five-minute RefreshInterval floor throttles rather "
            + "than stops. Ask only when a fresher document would help, as JwtBearerHandler does "
            + "(#2161).");
    }

    /// <summary>
    /// Waits for the count to move rather than sleeping a fixed interval, so the
    /// window is only ever paid in full when nothing re-read.
    /// </summary>
    private static async Task<int> FetchesAfterSettlingAsync(CountingMetadata retriever)
    {
        DateTime deadline = DateTime.UtcNow.Add(RefetchWindow);

        while (retriever.DiscoveryFetches == 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollInterval);
        }

        return retriever.DiscoveryFetches;
    }

    /// <summary>
    /// A real manager on its shipped intervals, with no prior refresh request —
    /// see the class note on why that matters.
    /// </summary>
    private static ConfigurationManager<OpenIdConnectConfiguration> MetadataOver(IDocumentRetriever retriever) =>
        new($"{DialledAuthority}/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            retriever);

    private static string DiscoveryDocument =>
        $$"""{"issuer":"{{RealmIssuer}}","jwks_uri":"{{JwksUri}}"}""";

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
    /// Signed by the key the served JWKS carries, and issued by the issuer the
    /// served document names, so audience and lifetime are the only things left
    /// to decide the outcome.
    /// </summary>
    private string TokenFor(string audience, DateTime expires)
    {
        SigningCredentials credentials = new(
            new RsaSecurityKey(signingKey) { KeyId = SigningKeyIdentifier },
            SecurityAlgorithms.RsaSha256);

        JwtSecurityToken token = new(
            issuer: RealmIssuer,
            audience: audience,
            claims: [new Claim("sub", "kiosk-operator"), new Claim("scope", "sse.streams.read")],
            notBefore: expires.AddMinutes(-6),
            expires: expires,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Serves a fixed realm and counts how often the discovery address is asked
    /// for. The count is the whole assertion: only a real re-read moves it.
    /// </summary>
    private sealed class CountingMetadata(string discovery, string keys) : IDocumentRetriever
    {
        private int discoveryFetches;

        public int DiscoveryFetches => Volatile.Read(ref discoveryFetches);

        public Task<string> GetDocumentAsync(string address, CancellationToken cancel)
        {
            if (address == JwksUri)
            {
                return Task.FromResult(keys);
            }

            Interlocked.Increment(ref discoveryFetches);
            return Task.FromResult(discovery);
        }
    }
}
