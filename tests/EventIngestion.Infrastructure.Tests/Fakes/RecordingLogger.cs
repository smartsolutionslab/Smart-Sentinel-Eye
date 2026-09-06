using Microsoft.Extensions.Logging;

namespace SmartSentinelEye.EventIngestion.Infrastructure.Tests.Fakes;

/// <summary>
/// Keeps every line, with its level, so a test can assert on what the loop said
/// as well as on what it did (ADR-0054 — hand-written, no mocking framework).
///
/// <para>
/// Non-generic because <c>MqttConnectionLoop</c> takes a plain
/// <see cref="ILogger"/>. A copy of the Scenario Simulator's recorder of the
/// same name, for the reason the two <c>FakeMqttClient</c>s are copies: the test
/// assemblies share no project.
/// </para>
/// </summary>
internal sealed class RecordingLogger : ILogger
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
