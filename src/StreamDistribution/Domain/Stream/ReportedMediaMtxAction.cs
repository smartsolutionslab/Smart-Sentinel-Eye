using SmartSentinelEye.Shared.Kernel;
using SmartSentinelEye.Shared.Kernel.Primitives;

namespace SmartSentinelEye.StreamDistribution.Domain.Stream;

/// <summary>
/// The text MediaMTX put in the external-auth hook body's <c>action</c> field,
/// before <see cref="MediaMtxAction"/> tried to recognise it. Its whole job is
/// to survive as far as the refusal log, so an operator reads
/// <c>MediaMTX sent 'stream'</c> rather than a description of two possible
/// cases (spec 115, issue #2105).
///
/// <para>
/// <b>It exists so that "bounded" is a property of the type, not a rule at the
/// call site.</b> The field is attacker-influenced only insofar as MediaMTX
/// composes the body, but an unbounded value in a log is a finding whoever put
/// it there: <see cref="From"/> caps the length and neutralises control
/// characters, so there is no way to hold one of these that is not already safe
/// to log.
/// </para>
///
/// <para>
/// Distinct from <see cref="MediaMtxAction"/> on purpose. That one is the
/// vocabulary this product grants and refuses on; this one is evidence, and
/// recognising it is not its business. <see cref="TryFrom"/> is
/// <see cref="Option{T}.None"/> for a <em>missing</em> field only — an empty or
/// blank <c>action</c> is a value that arrived, and "absent" and "present but
/// empty" are two different diagnoses.
/// </para>
/// </summary>
public sealed record ReportedMediaMtxAction : StringValueObject
{
    /// <summary>
    /// Every action MediaMTX has ever posted is one lowercase word of at most
    /// eight characters — <c>read</c>, <c>publish</c>, <c>playback</c>,
    /// <c>api</c>, <c>metrics</c>, <c>pprof</c>. 64 is eight times the longest,
    /// with room for a namespaced or hyphenated successor, because a cap that
    /// truncates the honest case destroys the diagnosis it exists to provide.
    /// It is still far below anything that could bloat a log record or push a
    /// message past a sink's line limit.
    /// </summary>
    public const int MaximumLength = 64;

    /// <summary>U+2026, appended so a capped value is never read as a whole one.</summary>
    private const char TruncationMark = '…';

    /// <summary>U+FFFD, the replacement character.</summary>
    private const char Unprintable = '�';

    private ReportedMediaMtxAction(string value) : base(value) { }

    public static ReportedMediaMtxAction From(string value)
    {
        Ensure.That(value).IsNotNull();

        return new ReportedMediaMtxAction(Bounded(value));
    }

    /// <summary>
    /// <see cref="Option{T}.None"/> means MediaMTX sent no <c>action</c> field
    /// at all. Anything else — including <c>""</c> — is a value that arrived.
    /// </summary>
    public static Option<ReportedMediaMtxAction> TryFrom(string? value) =>
        value is null
            ? Option<ReportedMediaMtxAction>.None
            : Option<ReportedMediaMtxAction>.Some(From(value));

    /// <summary>
    /// A <c>\r\n</c> in a logged value forges a second line in any text sink, so
    /// control characters are replaced rather than kept.
    /// </summary>
    private static string Bounded(string value)
    {
        char[] printable = [.. value
            .Take(MaximumLength)
            .Select(character => char.IsControl(character) ? Unprintable : character)];

        return value.Length > MaximumLength
            ? new string(printable) + TruncationMark
            : new string(printable);
    }
}
