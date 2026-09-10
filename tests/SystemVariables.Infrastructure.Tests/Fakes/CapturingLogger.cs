using Microsoft.Extensions.Logging;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.SystemVariables.Infrastructure.Tests.Fakes;

/// <summary>
/// Records what was logged, for the case where the log <b>is</b> the behaviour:
/// spec 126 turns a refused seed from a routine warning into an error whose
/// message no longer promises the index will repair itself, and nothing else
/// the seeder does distinguishes the two (FR-004, FR-005).
///
/// <para>
/// Mirrors <c>StreamDistribution.Infrastructure.Tests.Fakes.CapturingLogger</c>.
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
