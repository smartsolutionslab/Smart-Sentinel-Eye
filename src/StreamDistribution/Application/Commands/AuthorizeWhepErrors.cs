using System.Net;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.StreamDistribution.Application.Commands;

/// <summary>
/// Sealed-record failure hierarchy for <see cref="AuthorizeWhepCommand"/>.
/// MediaMTX uses the HTTP status to allow or reject the WHEP handshake.
/// </summary>
public abstract record AuthorizeWhepError(string Code, string Message, HttpStatusCode Status)
    : ApiError(Code, Message, Status)
{
    public sealed record Unauthorized()
        : AuthorizeWhepError(
            "WHEP_UNAUTHORIZED",
            "Bearer token is missing, malformed, or expired.",
            HttpStatusCode.Unauthorized);

    public sealed record Forbidden()
        : AuthorizeWhepError(
            "WHEP_FORBIDDEN",
            "Bearer token does not grant the sse.streams.read scope.",
            HttpStatusCode.Forbidden);

    public sealed record StreamUnavailable()
        : AuthorizeWhepError(
            "WHEP_STREAM_UNAVAILABLE",
            "The requested stream is offline; cannot open a WHEP session.",
            HttpStatusCode.Forbidden);

    /// <summary>
    /// The hook named an operation this product never grants through it.
    /// <c>403</c> and never <c>401</c>: upstream documents <c>401</c> as how an
    /// auth server asks a client to come back with credentials, and no
    /// credential makes a publish acceptable here — so the refusal has to be
    /// terminal rather than an invitation to retry.
    /// </summary>
    public sealed record ActionNotPermitted()
        : AuthorizeWhepError(
            "WHEP_ACTION_NOT_PERMITTED",
            "The requested action is not permitted through this hook.",
            HttpStatusCode.Forbidden);

    /// <summary>
    /// The hook named no action at all, or one this product does not model.
    /// Refused rather than assumed to be a read — the same <c>403</c>, for the
    /// same reason.
    /// </summary>
    public sealed record ActionUnknown()
        : AuthorizeWhepError(
            "WHEP_ACTION_UNKNOWN",
            "The request named no recognised action.",
            HttpStatusCode.Forbidden);

    /// <summary>
    /// The realm could not be reached, so the bearer token was never judged.
    /// Nothing is admitted — this is a refusal like every other member here.
    ///
    /// <para>
    /// <c>401</c> and not <c>403</c>: the outage is not terminal, and telling a
    /// client to stop trying is wrong at the moment retrying is what should
    /// happen. <c>401</c> and not a <c>5xx</c>: MediaMTX handles those
    /// differently and nobody here has observed how, which is the whole of issue
    /// #2160 — and the nine REST APIs were <em>measured</em> answering <c>401</c>
    /// for this exact condition, so this keeps the hook and the APIs agreed
    /// (spec 119 D1).
    /// </para>
    ///
    /// <para>
    /// Distinct from <see cref="Unauthorized"/> because that one says the token is
    /// "missing, malformed, or expired", which is false here and sends an
    /// operator hunting a credential while Keycloak is the thing that is down.
    /// </para>
    /// </summary>
    public sealed record IdentityProviderUnavailable()
        : AuthorizeWhepError(
            "WHEP_IDENTITY_PROVIDER_UNAVAILABLE",
            "The identity provider could not be reached; the bearer token was not checked.",
            HttpStatusCode.Unauthorized);
}

/// <summary>
/// Builds a <see cref="AuthorizeWhepError"/> as the base rather than the variant.
/// Generics are invariant, so an outcome inferred from a variant does not
/// convert to the Result a handler returns — failure call sites go through
/// here (ADR-0047).
/// </summary>
public static class AuthorizeWhepFailures
{
    public static AuthorizeWhepError Unauthorized() =>
        new AuthorizeWhepError.Unauthorized();

    public static AuthorizeWhepError Forbidden() =>
        new AuthorizeWhepError.Forbidden();

    public static AuthorizeWhepError StreamUnavailable() =>
        new AuthorizeWhepError.StreamUnavailable();

    public static AuthorizeWhepError ActionNotPermitted() =>
        new AuthorizeWhepError.ActionNotPermitted();

    public static AuthorizeWhepError ActionUnknown() =>
        new AuthorizeWhepError.ActionUnknown();

    public static AuthorizeWhepError IdentityProviderUnavailable() =>
        new AuthorizeWhepError.IdentityProviderUnavailable();
}
