namespace SmartSentinelEye.StreamDistribution.Application.Auth;

/// <summary>
/// Why <see cref="IWhepAuthValidator"/> produced no subject. Two facts, kept
/// apart because they are different facts: the product judged the token and
/// refused it, or the product could not judge it at all.
///
/// <para>
/// An enum rather than a string value object (constitution §II): this never
/// crosses the wire, is never parsed and is never persisted, so a
/// <c>IValueObject&lt;string&gt;</c> would buy nothing that <c>==</c> does not.
/// The same shape as <c>BearerValidationMode</c> and <c>IdempotencyOutcome</c>.
/// </para>
/// </summary>
public enum WhepAuthFailure
{
    /// <summary>
    /// The token was read and found wanting — absent, malformed, expired, signed
    /// by a key this realm does not publish, or minted for another API.
    /// </summary>
    TokenRejected,

    /// <summary>
    /// The realm's OIDC discovery document could not be obtained, so no token
    /// could be judged at all. Nothing is admitted; the refusal simply says
    /// something different (spec 119, issue #2160).
    /// </summary>
    IdentityProviderUnavailable,
}
