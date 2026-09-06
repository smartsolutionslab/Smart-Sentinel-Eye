using MQTTnet;
using MQTTnet.Diagnostics.PacketInspection;
using MQTTnet.Packets;

namespace SmartSentinelEye.EventIngestion.Infrastructure.Tests.Fakes;

/// <summary>
/// A broker that never was (ADR-0054 — hand-written, not a mocking framework).
///
/// <para>
/// It records what each CONNECT presented and every topic a SUBSCRIBE asked
/// for, and lets a test drop the connection on demand. Recording the
/// <b>password</b> rather than counting mints is deliberate: a loop that mints a
/// token and then presents a stale one would pass a call-count assertion and
/// fail this one, and presenting the live credential is the property that
/// matters (#2038).
/// </para>
/// </summary>
internal sealed class FakeMqttClient : IMqttClient
{
    private readonly Queue<Exception> connectFailures = new();
    private readonly TaskCompletionSource firstSubscribeSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event Func<MqttApplicationMessageReceivedEventArgs, Task>? ApplicationMessageReceivedAsync;

    public event Func<MqttClientConnectedEventArgs, Task>? ConnectedAsync;

    public event Func<MqttClientConnectingEventArgs, Task>? ConnectingAsync;

    public event Func<MqttClientDisconnectedEventArgs, Task>? DisconnectedAsync;

    public event Func<InspectMqttPacketEventArgs, Task>? InspectPacketAsync;

    /// <summary>The password presented by each CONNECT, in order.</summary>
    public List<string> PresentedCredentials { get; } = [];

    /// <summary>Every topic a SUBSCRIBE asked for, in order, across all connects.</summary>
    public List<string> SubscribedTopics { get; } = [];

    public int ConnectAttempts => PresentedCredentials.Count;

    public bool IsConnected { get; private set; }

    public MqttClientOptions Options { get; private set; } = new();

    /// <summary>Makes the next <paramref name="count"/> CONNECTs fail, as an unreachable broker does.</summary>
    public void RefuseNextConnects(int count)
    {
        for (int i = 0; i < count; i++)
        {
            connectFailures.Enqueue(new InvalidOperationException("broker refused the connection"));
        }
    }

    /// <summary>Drops an established connection, as a broker restart does.</summary>
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

    /// <summary>Completes once a SUBSCRIBE has been seen, so a test need not poll.</summary>
    public Task FirstSubscribe => firstSubscribeSeen.Task;

    public Task<MqttClientConnectResult> ConnectAsync(MqttClientOptions options, CancellationToken cancellationToken = default)
    {
        Options = options;
        PresentedCredentials.Add(System.Text.Encoding.UTF8.GetString(
            options.Credentials?.GetPassword(options) ?? []));

        if (connectFailures.Count > 0)
        {
            throw connectFailures.Dequeue();
        }

        IsConnected = true;
        return Task.FromResult(new MqttClientConnectResult());
    }

    public Task<MqttClientSubscribeResult> SubscribeAsync(
        MqttClientSubscribeOptions options, CancellationToken cancellationToken = default)
    {
        SubscribedTopics.AddRange(options.TopicFilters.Select(filter => filter.Topic));
        firstSubscribeSeen.TrySetResult();

        return Task.FromResult(new MqttClientSubscribeResult(
            packetIdentifier: 1,
            items: [.. options.TopicFilters.Select(filter =>
                new MqttClientSubscribeResultItem(filter, MqttClientSubscribeResultCode.GrantedQoS1))],
            reasonString: string.Empty,
            userProperties: []));
    }

    public Task DisconnectAsync(MqttClientDisconnectOptions options, CancellationToken cancellationToken = default)
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task<MqttClientPublishResult> PublishAsync(
        MqttApplicationMessage applicationMessage, CancellationToken cancellationToken = default) =>
        Task.FromResult(new MqttClientPublishResult(
            packetIdentifier: 1, MqttClientPublishReasonCode.Success, reasonString: string.Empty, userProperties: []));

    public Task<MqttClientUnsubscribeResult> UnsubscribeAsync(
        MqttClientUnsubscribeOptions options, CancellationToken cancellationToken = default) =>
        Task.FromResult(new MqttClientUnsubscribeResult(
            packetIdentifier: 1, items: [], reasonString: string.Empty, userProperties: []));

    public Task PingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SendEnhancedAuthenticationExchangeDataAsync(
        MqttEnhancedAuthenticationExchangeData data, CancellationToken cancellationToken = default) => Task.CompletedTask;

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
