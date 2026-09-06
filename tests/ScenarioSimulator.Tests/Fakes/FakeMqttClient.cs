using System.Buffers;
using MQTTnet;
using MQTTnet.Diagnostics.PacketInspection;
using MQTTnet.Exceptions;

namespace SmartSentinelEye.ScenarioSimulator.Tests.Fakes;

/// <summary>
/// A broker that never was (ADR-0054 — hand-written, not a mocking framework).
///
/// <para>
/// <b>The connect is gated rather than delayed.</b> The publisher's outage
/// window is the interval in which samples are dropped, and a test that opened
/// it with a sleep would be asserting against a race. Holding the CONNECT until
/// the test releases it makes the window exact.
/// </para>
///
/// <para>
/// A deliberate second copy of EventIngestion's fake of the same name: the two
/// test assemblies share no project, and this one gates connects and forces a
/// publish failure where that one records credentials and topics.
/// </para>
/// </summary>
internal sealed class FakeMqttClient : IMqttClient
{
    private TaskCompletionSource? connectGate;

    public event Func<MqttApplicationMessageReceivedEventArgs, Task>? ApplicationMessageReceivedAsync;

    public event Func<MqttClientConnectedEventArgs, Task>? ConnectedAsync;

    public event Func<MqttClientConnectingEventArgs, Task>? ConnectingAsync;

    public event Func<MqttClientDisconnectedEventArgs, Task>? DisconnectedAsync;

    public event Func<InspectMqttPacketEventArgs, Task>? InspectPacketAsync;

    /// <summary>Payloads the broker accepted, in order.</summary>
    public List<string> Published { get; } = [];

    /// <summary>
    /// Makes the next publish throw as a connection that dropped between the
    /// <c>IsConnected</c> check and the publish does — the US2-AC3 race.
    /// </summary>
    public bool FailNextPublishAsNotConnected { get; set; }

    public bool IsConnected { get; private set; }

    public MqttClientOptions Options { get; private set; } = new();

    /// <summary>Holds every CONNECT until <see cref="AllowConnect"/> is called.</summary>
    public void GateConnect() =>
        connectGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void AllowConnect() => connectGate?.TrySetResult();

    public async Task DropAsync()
    {
        IsConnected = false;

        Func<MqttClientDisconnectedEventArgs, Task>? handler = DisconnectedAsync;
        if (handler is not null)
        {
            await handler(new MqttClientDisconnectedEventArgs(
                clientWasConnected: true,
                connectResult: null!,
                reason: MqttClientDisconnectReason.UnspecifiedError,
                reasonString: "the broker went away",
                userProperties: [],
                exception: null!));
        }
    }

    public async Task<MqttClientConnectResult> ConnectAsync(
        MqttClientOptions options, CancellationToken cancellationToken = default)
    {
        Options = options;

        if (connectGate is not null)
        {
            await connectGate.Task.WaitAsync(cancellationToken);
        }

        IsConnected = true;
        return new MqttClientConnectResult();
    }

    public Task<MqttClientPublishResult> PublishAsync(
        MqttApplicationMessage applicationMessage, CancellationToken cancellationToken = default)
    {
        if (FailNextPublishAsNotConnected)
        {
            FailNextPublishAsNotConnected = false;
            throw new MqttClientNotConnectedException();
        }

        Published.Add(System.Text.Encoding.UTF8.GetString(applicationMessage.Payload.ToArray()));

        return Task.FromResult(new MqttClientPublishResult(
            packetIdentifier: 1, MqttClientPublishReasonCode.Success, reasonString: string.Empty, userProperties: []));
    }

    public Task DisconnectAsync(MqttClientDisconnectOptions options, CancellationToken cancellationToken = default)
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task<MqttClientSubscribeResult> SubscribeAsync(
        MqttClientSubscribeOptions options, CancellationToken cancellationToken = default) =>
        Task.FromResult(new MqttClientSubscribeResult(
            packetIdentifier: 1, items: [], reasonString: string.Empty, userProperties: []));

    public Task<MqttClientUnsubscribeResult> UnsubscribeAsync(
        MqttClientUnsubscribeOptions options, CancellationToken cancellationToken = default) =>
        Task.FromResult(new MqttClientUnsubscribeResult(
            packetIdentifier: 1, items: [], reasonString: string.Empty, userProperties: []));

    public Task PingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SendEnhancedAuthenticationExchangeDataAsync(
        MqttEnhancedAuthenticationExchangeData data, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public void Dispose()
    {
        // Nothing native is held; the connection state is a bool.
    }

    /// <summary>Kept so the compiler does not warn on events this fake never raises.</summary>
    internal void SilenceUnusedEvents()
    {
        _ = ApplicationMessageReceivedAsync;
        _ = ConnectedAsync;
        _ = ConnectingAsync;
        _ = InspectPacketAsync;
    }
}
