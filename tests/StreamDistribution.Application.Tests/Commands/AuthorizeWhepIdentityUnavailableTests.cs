using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSentinelEye.Shared.Kernel;
using SmartSentinelEye.StreamDistribution.Application.Auth;
using SmartSentinelEye.StreamDistribution.Application.Commands;
using SmartSentinelEye.StreamDistribution.Application.Commands.Handlers;
using SmartSentinelEye.StreamDistribution.Application.Tests.Fakes;
using SmartSentinelEye.StreamDistribution.Domain.Stream;

namespace SmartSentinelEye.StreamDistribution.Application.Tests.Commands;

/// <summary>
/// Spec 119 (issue #2160) — the answer when the realm cannot be reached.
///
/// <para>
/// The handler cannot see an outage; it sees what the validator reports. These
/// assert the mapping: an unreachable realm is refused with the same status a
/// bad token gets, and with a different code, because "we could not check" is a
/// different fact from "we checked and refused" — and the operator meeting it is
/// watching every viewer on the wall drop at once.
/// </para>
/// </summary>
public class AuthorizeWhepIdentityUnavailableTests
{
    [Fact]
    public async Task Authorize_when_the_realm_is_unreachable_is_refused_with_401()
    {
        Result<MediaMtxPath, AuthorizeWhepError> result = await AuthorizeWithAnUnreachableRealm();

        result.IsFailure.ShouldBeTrue(
            customMessage: "the WHEP hook admitted a viewer while the realm that vouches for it was "
            + "unreachable. Nothing may be authorized on a token nothing checked (spec 119).");
        result.Error.Status.ShouldBe(
            HttpStatusCode.Unauthorized,
            customMessage: "MediaMTX must receive the status the nine REST APIs measurably answer "
            + "for this condition. A 5xx is what issue #2160 exists to remove, and a 403 tells a "
            + "client to stop retrying at the moment retrying is what should happen (plan D1).");
    }

    /// <summary>
    /// The half that makes the refusal diagnosable. <c>WHEP_UNAUTHORIZED</c> reads
    /// "Bearer token is missing, malformed, or expired", which is <em>false</em>
    /// here and sends an operator hunting a token while Keycloak is the thing
    /// that is down (FR-002).
    /// </summary>
    [Fact]
    public async Task Authorize_when_the_realm_is_unreachable_does_not_blame_the_token()
    {
        Result<MediaMtxPath, AuthorizeWhepError> result = await AuthorizeWithAnUnreachableRealm();

        result.Error.Code.ShouldNotBe(
            "WHEP_UNAUTHORIZED",
            customMessage: "an unreachable realm was reported as a rejected token. The two are "
            + "different facts and the wire has to tell them apart (spec 119 FR-002).");
    }

    /// <summary>
    /// The control. Same handler, same command, only the validator's report
    /// differs — so the two assertions above are attributable to the outage and
    /// not to a command this handler would have refused anyway.
    /// </summary>
    [Fact]
    public async Task Authorize_with_a_rejected_token_is_still_WHEP_UNAUTHORIZED()
    {
        Result<MediaMtxPath, AuthorizeWhepError> result =
            await Authorize(WhepAuthFailure.TokenRejected);

        result.Error.ShouldBeOfType<AuthorizeWhepError.Unauthorized>();
    }

    private static Task<Result<MediaMtxPath, AuthorizeWhepError>> AuthorizeWithAnUnreachableRealm() =>
        Authorize(WhepAuthFailure.IdentityProviderUnavailable);

    private static Task<Result<MediaMtxPath, AuthorizeWhepError>> Authorize(WhepAuthFailure failure)
    {
        FakeWhepAuthValidator validator = new()
        {
            Subject = Option<WhepAuthSubject>.None,
            Failure = failure,
        };
        AuthorizeWhepCommandHandler handler = new(
            validator, new InMemoryStreamRepository(), NullLogger<AuthorizeWhepCommandHandler>.Instance);

        return handler.HandleAsync(
            new AuthorizeWhepCommand(
                MediaMtxPath.For(CameraIdentifier.From(Guid.CreateVersion7())),
                "Bearer.kiosk",
                Option<MediaMtxAction>.Some(MediaMtxAction.Read),
                ReportedMediaMtxAction.TryFrom("read")),
            CancellationToken.None);
    }
}
