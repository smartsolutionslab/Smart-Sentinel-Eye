using Microsoft.Extensions.Logging;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.MigrationRunner.Tests.Fakes;

/// <summary>
/// Records what was logged, for the cases where the log <em>is</em> the
/// behaviour: a MigrationRunner that pauses says so, or the pause is
/// indistinguishable from a hang to whoever is waiting on it.
///
/// <para>
/// Mirrors <c>SystemVariables.Application.Tests.Fakes.CapturingLogger</c> and
/// <c>Automation.Application.Tests.Fakes.CapturingLogger</c>. Copied rather
/// than shared, for the same reason those two are copies of each other: test
/// projects do not reference one another, and the alternative is a shared
/// test-support assembly that nothing else wants yet.
/// </para>
/// </summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message, Exception? Exception)> entries = [];

    public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries => entries;

    public IDisposable BeginScope<TState>(TState state)
        where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Ensure.That(formatter).IsNotNull();
        entries.Add((logLevel, formatter(state, exception), exception));
    }
}

/// <summary>Outside the generic on purpose: one instance, not one per T.</summary>
internal sealed class NullScope : IDisposable
{
    public static NullScope Instance { get; } = new();

    public void Dispose()
    {
    }
}
