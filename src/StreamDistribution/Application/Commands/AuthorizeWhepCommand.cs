using SmartSentinelEye.Shared.CQRS;
using SmartSentinelEye.Shared.Kernel;
using SmartSentinelEye.StreamDistribution.Domain.Stream;

namespace SmartSentinelEye.StreamDistribution.Application.Commands;

/// <summary>
/// Authorizes a WHEP open. MediaMTX POSTs this on every WHEP handshake
/// (FR-007); the handler validates the forwarded bearer token, checks the
/// <c>sse.management</c> scope, and rejects when the target stream is
/// Offline.
///
/// <para>
/// <c>Action</c> carries the operation MediaMTX said it was asking about.
/// <see cref="Option{T}.None"/> means the field was absent or named an operation
/// this product does not model. It has no default value on purpose:
/// <c>Option&lt;T&gt;</c> is a readonly struct whose <c>None</c> is
/// <c>default</c>, so a defaulted parameter would silently read "unknown" at
/// every call site that forgot to pass one.
/// </para>
///
/// <para>
/// <c>ReportedAction</c> is that same field as text, kept because <c>Action</c>
/// collapses two diagnoses into one (spec 115): <see cref="Option{T}.None"/>
/// here means MediaMTX sent <em>no</em> <c>action</c> field, while a value means
/// it sent one this build does not recognise — and the refusal can then name it.
/// Same reason for no default value.
/// </para>
/// </summary>
public sealed record AuthorizeWhepCommand(
    MediaMtxPath Path,
    string BearerToken,
    Option<MediaMtxAction> Action,
    Option<ReportedMediaMtxAction> ReportedAction)
    : ICommand<Result<MediaMtxPath, AuthorizeWhepError>>;
