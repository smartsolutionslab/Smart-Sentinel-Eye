using Microsoft.Extensions.Logging;

namespace SmartSentinelEye.ScenarioSimulator.Tests.Fakes;

/// <summary>
/// Keeps every line, with its level, so a test can assert on how many were
/// written as well as what they said (ADR-0054 — hand-written, no mocking
/// framework). "Exactly one warning, not one per dropped sample" is a claim
/// about the count, and only a recorder can answer it.
/// </summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message)> entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (entries)
            {
                return [.. entries];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (entries)
        {
            entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
