using SmartSentinelEye.Shared.Kernel;
using SmartSentinelEye.StreamDistribution.Application.Auth;

namespace SmartSentinelEye.StreamDistribution.Application.Tests.Fakes;

/// <summary>
/// Scripted <see cref="IWhepAuthValidator"/> for handler tests. Configure
/// the response per test by setting <see cref="Subject"/> to a result, or
/// leave it empty to simulate a token this product judged and refused.
/// <see cref="Failure"/> says which refusal an empty <see cref="Subject"/>
/// stands for — a rejected token by default, so every test written before
/// spec 119 keeps scripting exactly what it scripted.
/// </summary>
public sealed class FakeWhepAuthValidator : IWhepAuthValidator
{
    public Option<WhepAuthSubject> Subject { get; set; } = Option<WhepAuthSubject>.None;

    public WhepAuthFailure Failure { get; set; } = WhepAuthFailure.TokenRejected;

    public Task<Result<WhepAuthSubject, WhepAuthFailure>> ValidateAsync(string bearerToken, CancellationToken cancellationToken) =>
        Task.FromResult(Subject.HasValue
            ? Result<WhepAuthSubject, WhepAuthFailure>.Success(Subject.Value)
            : Result<WhepAuthSubject, WhepAuthFailure>.Failure(Failure));
}
