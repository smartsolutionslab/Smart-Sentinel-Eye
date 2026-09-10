using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using SmartSentinelEye.ServiceDefaults;
using SmartSentinelEye.Shared.Kernel;
using SmartSentinelEye.StreamDistribution.Application.Auth;

namespace SmartSentinelEye.StreamDistribution.Infrastructure.Auth;

/// <summary>
/// Validates a bearer token forwarded by MediaMTX's external auth hook
/// against the same Keycloak realm as the standard JwtBearer pipeline.
/// Issuer + signing keys are fetched from the realm's OIDC discovery
/// document (cached by <see cref="ConfigurationManager{T}"/>).
/// </summary>
public sealed class WhepAuthValidator : IWhepAuthValidator
{
    private readonly IConfigurationManager<OpenIdConnectConfiguration> oidc;
    private readonly TokenValidationParameters parameters;
    // Diverges from the bearer pipeline, which uses JsonWebTokenHandler. That should
    // eventually back both sides: Microsoft positions this one as the legacy path, and
    // the ArgumentException catch below exists only to absorb its habit of surfacing
    // malformed-token paths as ArgumentException — a wart that goes away with it.
    // Deferred rather than done here because ValidateTokenAsync returns a result
    // instead of throwing, so the migration rewrites this method's control flow and
    // needs its own adversarial pass over malformed inputs (spec 089 D4).
    private readonly JwtSecurityTokenHandler handler = new();

    public WhepAuthValidator(IOptions<WhepAuthOptions> options)
        : this(MetadataSourceFor(options))
    {
    }

    /// <summary>
    /// The seam the unit tests construct through, so the OIDC metadata can be
    /// served from memory instead of a realm (#2099). Deliberately
    /// <c>internal</c>: a public constructor taking a metadata source would be a
    /// public way to point this token validator at another issuer.
    /// </summary>
    internal WhepAuthValidator(IConfigurationManager<OpenIdConnectConfiguration> metadata)
    {
        Ensure.That(metadata).IsNotNull();

        oidc = metadata;

        parameters = CreateParameters();

        handler.MapInboundClaims = false;
    }

    internal static ConfigurationManager<OpenIdConnectConfiguration> MetadataSourceFor(
        IOptions<WhepAuthOptions> options)
    {
        Ensure.That(options).IsNotNull();
        string authority = options.Value.Authority.TrimEnd('/');

        // Allow an http metadata authority (dev/test/Aspire) — there is no Helm
        // overlay enforcing https on Keycloak (deploy/helm/ has only the Mosquitto
        // chart); this is a permissive default, not one backed by deployment
        // config. Mirrors the standard JwtBearer pipeline's
        // RequireHttpsMetadata = false (AuthenticationDefaults, which carries the
        // same unbacked claim at :58). Without this the default HttpDocumentRetriever
        // requires https and throws IDX20108 on the dev/CI http authority — a 500 on
        // every WHEP authorize.
        return new ConfigurationManager<OpenIdConnectConfiguration>(
            $"{authority}/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever { RequireHttps = false });
    }

    internal static TokenValidationParameters CreateParameters() => new()
    {
        // No ValidIssuer/ValidIssuers here: the issuer is filled in ValidateAsync from
        // the discovery document, exactly as JwtBearerHandler fills it per request. The
        // configured authority addresses discovery and nothing else — behind an ingress
        // it is not the issuer the realm mints (spec 089, #2095).
        ValidateIssuer = true,
        // The audience arrives on the sse-audience client scope (spec 069). Read from
        // the constant the bearer pipeline reads, so this hook cannot accept a token
        // the nine APIs would refuse; WhepAudienceTests holds the pairing.
        ValidAudiences = [AuthenticationDefaults.ApiAudience],
        ValidateLifetime = true,
        // Deliberately stricter than the bearer pipeline, which leaves this false.
        // Against Keycloak it does run: the realm's JWKS carries x5c, so
        // JsonWebKeySet.GetSigningKeys yields an X509SecurityKey alongside the RSA one,
        // and ValidateIssuerSigningKeyLifeTime date-checks the realm's signing
        // certificate. That makes it a second copy of #2095's asymmetry — on a lapsed
        // realm certificate WHEP 401s and the nine REST APIs do not. Resolving it by
        // making those nine stricter is the correct direction and a separate slice;
        // relaxing this one to match would be parity bought by relaxation (spec 089 D2).
        ValidateIssuerSigningKey = true,
        // Inert: ValidateAsync reads "sub" and "scope" through FindFirst and never
        // touches Identity.Name, so this setting decides nothing here (spec 089 D3).
        NameClaimType = "preferred_username",
    };

    public async Task<Result<WhepAuthSubject, WhepAuthFailure>> ValidateAsync(string bearerToken, CancellationToken cancellationToken)
    {
        try
        {
            OpenIdConnectConfiguration configuration = await oidc.GetConfigurationAsync(cancellationToken);
            TokenValidationParameters validationParameters = parameters.Clone();
            validationParameters.ValidIssuers = [configuration.Issuer];
            validationParameters.IssuerSigningKeys = configuration.SigningKeys;

            ClaimsPrincipal principal = handler.ValidateToken(bearerToken, validationParameters, out _);

            string? subject = principal.FindFirst("sub")?.Value;
            if (subject is null)
            {
                return Result<WhepAuthSubject, WhepAuthFailure>.Failure(WhepAuthFailure.TokenRejected);
            }

            string scopeClaim = principal.FindFirst("scope")?.Value ?? string.Empty;
            string[] scopes = scopeClaim.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            return Result<WhepAuthSubject, WhepAuthFailure>.Success(new WhepAuthSubject(subject, scopes));
        }
        catch (SecurityTokenException)
        {
            return Result<WhepAuthSubject, WhepAuthFailure>.Failure(WhepAuthFailure.TokenRejected);
        }
        catch (ArgumentException)
        {
            // Some malformed-token paths in JwtSecurityTokenHandler surface
            // as ArgumentException rather than SecurityTokenException. Treat
            // both as anonymous so MediaMTX gets a clean 401.
            return Result<WhepAuthSubject, WhepAuthFailure>.Failure(WhepAuthFailure.TokenRejected);
        }
    }
}
