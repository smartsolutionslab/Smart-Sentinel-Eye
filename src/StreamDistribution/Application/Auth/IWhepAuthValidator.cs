using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.StreamDistribution.Application.Auth;

/// <summary>
/// Validates a bearer token forwarded by MediaMTX's external auth hook
/// (FR-007). Implementation lives in Infrastructure and reuses the same
/// Keycloak configuration as the standard JWT bearer pipeline; the
/// Application layer depends only on this abstraction so it stays
/// framework-free.
///
/// <para>
/// Returns a <see cref="Result{TValue,TError}"/> rather than an
/// <c>Option</c> because the absence of a subject has two causes and the
/// caller answers them differently (spec 119): a token this product judged and
/// refused, and a realm it could not reach.
/// </para>
/// </summary>
public interface IWhepAuthValidator
{
    Task<Result<WhepAuthSubject, WhepAuthFailure>> ValidateAsync(string bearerToken, CancellationToken cancellationToken);
}
