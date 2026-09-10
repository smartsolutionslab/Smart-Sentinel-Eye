using Microsoft.Extensions.Logging;

namespace SmartSentinelEye.Identity.Infrastructure.Tests.Fakes;

/// <summary>
/// Keeps every entry written through it, so a test can assert on what was
/// logged — and, more to the point here, on what was <b>not</b>.
///
/// <para>
/// Hand-written rather than mocked (ADR-0054). <c>NullLogger</c> is what the
/// five pre-existing <c>KioskPrivilegeSweepTests</c> use, and it is why a change
/// to the sweep's logging shipped with nothing able to see it.
/// </para>
/// </summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    public List<LoggedEntry> Entries { get; } = [];

    /// <summary>
    /// Every entry the source-generated <c>[LoggerMessage]</c> methods emitted
    /// for <paramref name="name"/> — the method name, which the generator uses
    /// as the event's name.
    /// </summary>
    public IReadOnlyList<LoggedEntry> Named(string name) =>
        Entries.Where(entry => entry.EventId.Name == name).ToArray();

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    /// <summary>
    /// Always enabled: the generated log methods check this first, so a logger
    /// that answered false would record nothing and every "was not logged"
    /// assertion below would pass for the wrong reason.
    /// </summary>
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Entries.Add(new LoggedEntry(
            logLevel,
            eventId,
            formatter(state, exception),
            FieldsOf(state),
            exception));

    /// <summary>
    /// The structured fields the entry carried. <c>[LoggerMessage]</c> emits a
    /// state implementing <see cref="IReadOnlyList{T}"/> of key/value pairs, so
    /// this is the shape an OTLP sink receives — the assertable one. Spec 122
    /// (#2166): the message text may name the right client while the field
    /// names the wrong one, and only the field is what an operator queries by.
    /// </summary>
    private static Dictionary<string, object?> FieldsOf<TState>(TState state) =>
        state is IReadOnlyList<KeyValuePair<string, object?>> pairs
            ? pairs
                .Where(pair => pair.Key != "{OriginalFormat}")
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
            : new Dictionary<string, object?>(StringComparer.Ordinal);
}

/// <summary>One entry, as the sink would have received it.</summary>
public sealed record LoggedEntry(
    LogLevel Level,
    EventId EventId,
    string Message,
    IReadOnlyDictionary<string, object?> Fields,
    Exception? Exception)
{
    /// <summary>
    /// One structured field as text, or <c>null</c> when the entry does not
    /// carry it at all — which an assertion must be able to tell apart from a
    /// field carrying the wrong value.
    /// </summary>
    public string? Field(string name) =>
        Fields.TryGetValue(name, out object? value) ? value?.ToString() : null;
}
