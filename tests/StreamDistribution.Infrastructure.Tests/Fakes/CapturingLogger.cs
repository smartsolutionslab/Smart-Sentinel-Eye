using Microsoft.Extensions.Logging;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.StreamDistribution.Infrastructure.Tests.Fakes;

/// <summary>
/// Records what was logged, for the case where the log carries the only
/// surviving diagnosis: an unreachable realm is swallowed into a refusal, and
/// IDX20803's inner chain is the only thing that says whether it was DNS, a
/// refused socket or TLS (spec 119 FR-005).
///
/// <para>
/// Mirrors <c>StreamDistribution.Application.Tests.Fakes.CapturingLogger</c>.
/// Copied rather than shared: test projects do not reference one another.
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
