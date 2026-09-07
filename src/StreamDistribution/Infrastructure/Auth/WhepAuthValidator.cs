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
    private readonly ConfigurationManager<OpenIdConnectConfiguration> oidc;
    private readonly TokenValidationParameters parameters;
    private readonly JwtSecurityTokenHandler handler = new();

    public WhepAuthValidator(IOptions<WhepAuthOptions> options)
    {
        Ensure.That(options).IsNotNull();
        string authority = options.Value.Authority.TrimEnd('/');

        // Allow an http metadata authority (dev/test/Aspire); production
        // Keycloak is https, enforced by the Helm overlay. Mirrors the standard
        // JwtBearer pipeline's RequireHttpsMetadata = false (AuthenticationDefaults).
        // Without this the default HttpDocumentRetriever requires https and throws
        // IDX20108 on the dev/CI http authority — a 500 on every WHEP authorize.
        oidc = new ConfigurationManager<OpenIdConnectConfiguration>(
            $"{authority}/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever { RequireHttps = false });

        parameters = CreateParameters();

        handler.MapInboundClaims = false;
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
        ValidateIssuerSigningKey = true,
        NameClaimType = "preferred_username",
    };

    public async Task<Option<WhepAuthSubject>> ValidateAsync(string bearerToken, CancellationToken cancellationToken)
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
                return Option<WhepAuthSubject>.None;
            }

            string scopeClaim = principal.FindFirst("scope")?.Value ?? string.Empty;
            string[] scopes = scopeClaim.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            return Option<WhepAuthSubject>.Some(new WhepAuthSubject(subject, scopes));
        }
        catch (SecurityTokenException)
        {
            return Option<WhepAuthSubject>.None;
        }
        catch (ArgumentException)
        {
            // Some malformed-token paths in JwtSecurityTokenHandler surface
            // as ArgumentException rather than SecurityTokenException. Treat
            // both as anonymous so MediaMTX gets a clean 401.
            return Option<WhepAuthSubject>.None;
        }
    }
}
