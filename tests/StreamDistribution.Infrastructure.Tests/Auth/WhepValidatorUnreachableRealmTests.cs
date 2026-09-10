using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using SmartSentinelEye.ServiceDefaults;
using SmartSentinelEye.Shared.Kernel;
using SmartSentinelEye.StreamDistribution.Application.Auth;
using SmartSentinelEye.StreamDistribution.Infrastructure.Auth;
using SmartSentinelEye.StreamDistribution.Infrastructure.Tests.Fakes;

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

    private readonly CapturingLogger<WhepAuthValidator> logs = new();

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
    /// The outage is swallowed into a refusal, so the exception is the only thing
    /// left that says <em>why</em> the realm was unreachable. A swallowed
    /// exception with no trace is a review blocker; this is the assertion that
    /// keeps it from becoming one (FR-005).
    /// </summary>
    [Fact]
    public async Task An_unreachable_realm_is_logged_once_with_the_exception()
    {
        WhepAuthValidator validator = ValidatorOver(new UnreachableRealm());

        await validator.ValidateAsync(AToken(), CancellationToken.None);

        (LogLevel Level, string Message, Exception? Exception) entry = logs.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Exception.ShouldNotBeNull().Message.ShouldContain(
            "IDX20803",
            customMessage: "the log records that the realm was unreachable but not why. IDX20803 "
            + "names the address and wraps the transport failure — DNS, refused, TLS — and nothing "
            + "else survives the refusal (spec 119 FR-005).");
    }

    /// <summary>
    /// <b>Cancellation stays cancellation.</b> A caller that went away must not be
    /// counted as a refused viewer. What protects it is the <em>narrowness</em> of
    /// the catch, so this test is aimed at the edit that would remove that: with
    /// <c>catch (Exception)</c> in place of <c>catch (InvalidOperationException)</c>
    /// it fails, and it was run against exactly that mistake before being
    /// committed.
    /// </summary>
    [Fact]
    public async Task A_cancelled_request_stays_cancelled()
    {
        WhepAuthValidator validator = ValidatorOver(new CancellingRealm());
        using CancellationTokenSource cancelled = new(TimeSpan.FromMilliseconds(50));

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


    /// <summary>
    /// <b>One line per outage, not one per refused viewer.</b>
    /// <c>/streams/authorize</c> is <c>AllowAnonymous</c> and nothing rate-limits
    /// it, so a wall of kiosks retrying through a cold-cache outage would write a
    /// Warning and a full exception chain per request into the single OTLP sink —
    /// drowning the very diagnosis this change exists to provide (phase 6).
    /// </summary>
    [Fact]
    public async Task An_outage_is_logged_once_however_many_viewers_are_refused()
    {
        ScriptedRealm realm = new(SigningKey()) { Reachable = false };
        WhepAuthValidator validator = ValidatorOver(realm);

        for (int viewer = 0; viewer < 25; viewer++)
        {
            await validator.ValidateAsync(AToken(), CancellationToken.None);
        }

        logs.Entries.Count(entry => entry.Level == LogLevel.Warning).ShouldBe(
            1,
            customMessage: "the outage was logged once per refused viewer. Twenty-five WHEP opens "
            + "wrote twenty-five stack traces, and a fab has 250 kiosks (spec 119, phase 6).");
    }

    /// <summary>
    /// The transition is a transition, not a latch: an outage that ends and
    /// returns is two outages, and the second one has to say so. A flag that was
    /// only ever set would make the second outage silent — the worse failure of
    /// the two, because by then the operator has seen the recovery.
    /// </summary>
    [Fact]
    public async Task A_recovery_is_logged_and_a_second_outage_speaks_again()
    {
        ScriptedRealm realm = new(SigningKey()) { Reachable = false };
        WhepAuthValidator validator = ValidatorOver(realm);

        await validator.ValidateAsync(AToken(), CancellationToken.None);
        realm.Reachable = true;
        await validator.ValidateAsync(AToken(), CancellationToken.None);
        realm.Reachable = false;
        await validator.ValidateAsync(AToken(), CancellationToken.None);

        logs.Entries.Count(entry => entry.Level == LogLevel.Warning).ShouldBe(
            2,
            customMessage: "the second outage was swallowed. The flag latched instead of tracking "
            + "the transition, so an operator who saw the recovery is told nothing when it breaks "
            + "again (spec 119, phase 6).");
        logs.Entries.Count(entry => entry.Level == LogLevel.Information).ShouldBe(
            1,
            customMessage: "the recovery was not logged, so the outage warning has no closing "
            + "bracket and an operator cannot tell a resolved outage from an ongoing one.");
    }

    private WhepAuthValidator ValidatorOver(IConfigurationManager<OpenIdConnectConfiguration> metadata) =>
        new(metadata, logs);

    private WhepAuthValidator ValidatorOver(IDocumentRetriever retriever) =>
        new(
            new ConfigurationManager<OpenIdConnectConfiguration>(
                $"{Authority}/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever(),
                retriever),
            logs);

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

    private RsaSecurityKey SigningKey() =>
        new(signingKey) { KeyId = SigningKeyIdentifier };

    private string AToken()
    {
        SigningCredentials credentials = new(SigningKey(), SecurityAlgorithms.RsaSha256);

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

    /// <summary>
    /// A cancelled fetch as the production retriever delivers it.
    /// <c>HttpDocumentRetriever.GetDocumentAsync</c> wraps <em>every</em> exception
    /// its request threw in <c>IOException(IDX20804)</c> — a cancellation
    /// included — and <c>ConfigurationManager</c> then wraps that in
    /// <c>InvalidOperationException(IDX20803)</c>. So a caller that went away and
    /// a realm that is down arrive at the catch as one type, and only the token
    /// tells them apart.
    ///
    /// <para>
    /// Measured, not assumed: an <c>OperationCanceledException</c> thrown
    /// <em>straight</em> out of a retriever propagates unwrapped and needs no
    /// guard at all. It is the wrapping that creates the confusion, so it is the
    /// wrapping this reproduces — a stub that threw the bare exception would pass
    /// with the guard deleted.
    /// </para>
    /// </summary>
    private sealed class CancellingRealm : IDocumentRetriever
    {
        public async Task<string> GetDocumentAsync(string address, CancellationToken cancel)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancel);
            }
            catch (Exception exception)
            {
                throw new IOException($"IDX20804: Unable to retrieve document from: '{address}'.", exception);
            }

            return DiscoveryDocument;
        }
    }

    /// <summary>
    /// A metadata source whose reachability is scripted. Used where the real
    /// <see cref="ConfigurationManager{T}"/> cannot be: it caches a document once
    /// obtained, so an outage <em>following</em> a success never reaches the
    /// validator through it — and the transition back is exactly what these two
    /// tests are about. The failure is the exception ConfigurationManager raises,
    /// IDX20803 and all.
    /// </summary>
    private sealed class ScriptedRealm(SecurityKey signingKey) : IConfigurationManager<OpenIdConnectConfiguration>
    {
        public bool Reachable { get; set; }

        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
        {
            if (!Reachable)
            {
                throw new InvalidOperationException(
                    $"IDX20803: Unable to obtain configuration from: '{Authority}/.well-known/openid-configuration'.",
                    new IOException("IDX20804: Unable to retrieve document."));
            }

            OpenIdConnectConfiguration configuration = new() { Issuer = Authority };
            configuration.SigningKeys.Add(signingKey);

            return Task.FromResult(configuration);
        }

        public void RequestRefresh()
        {
        }
    }

    private sealed class ReachableRealm(string discovery, string keys) : IDocumentRetriever
    {
        public Task<string> GetDocumentAsync(string address, CancellationToken cancel) =>
            Task.FromResult(address == JwksUri ? keys : discovery);
    }
}
