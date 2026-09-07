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
        Entries.Add(new LoggedEntry(logLevel, eventId, formatter(state, exception)));
}

/// <summary>One entry, as the sink would have received it.</summary>
public sealed record LoggedEntry(LogLevel Level, EventId EventId, string Message);
