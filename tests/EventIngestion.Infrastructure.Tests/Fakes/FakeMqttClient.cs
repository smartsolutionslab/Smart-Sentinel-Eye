using MQTTnet;
using MQTTnet.Diagnostics.PacketInspection;
using MQTTnet.Exceptions;
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
///
/// <para>
/// <b>It models MQTTnet 5, and the difference from 4 is the whole point.</b> A
/// fake that encodes the previous major version's contract passes against a loop
/// the real library would break — which is exactly what happened here: the
/// refusal was written as a thrown exception, and that is v4.
/// </para>
/// </summary>
internal sealed class FakeMqttClient : IMqttClient
{
    private readonly TaskCompletionSource firstSubscribeSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int refusals;
    private TimeSpan? holdFor;
    private bool staleDuringNextConnect;

    public event Func<MqttApplicationMessageReceivedEventArgs, Task>? ApplicationMessageReceivedAsync;

    public event Func<MqttClientConnectedEventArgs, Task>? ConnectedAsync;

    public event Func<MqttClientConnectingEventArgs, Task>? ConnectingAsync;

    public event Func<MqttClientDisconnectedEventArgs, Task>? DisconnectedAsync;

    public event Func<InspectMqttPacketEventArgs, Task>? InspectPacketAsync;

    /// <summary>The password presented by each CONNECT, in order.</summary>
    public List<string> PresentedCredentials { get; } = [];

    /// <summary>Every topic a SUBSCRIBE asked for, in order, across all connects.</summary>
    public List<string> SubscribedTopics { get; } = [];

    /// <summary>
    /// Makes every connection end the moment it is usable, as a session takeover
    /// does: both real clients connect with a fixed client id
    /// (<c>event-ingestion</c>, <c>scenario-simulator</c>), so a second pod — or
    /// a restart before the broker reaps the old session — is answered with a
    /// CONNACK and then a close. The drop is raised after the SUBSCRIBE has gone
    /// out, so this is a connection that completed and then vanished rather than
    /// one that was never usable.
    /// </summary>
    public bool DropEveryConnectionImmediately { get; set; }

    public int ConnectAttempts => PresentedCredentials.Count;

    public bool IsConnected { get; private set; }

    public MqttClientOptions Options { get; private set; } = new();

    /// <summary>Completes once a SUBSCRIBE has been seen, so a test need not poll.</summary>
    public Task FirstSubscribe => firstSubscribeSeen.Task;

    /// <summary>Makes the next <paramref name="count"/> CONNECTs be refused.</summary>
    public void RefuseNextConnects(int count) => refusals += count;

    /// <summary>
    /// Refuses every CONNECT, for good. An <c>acl.txt</c> that grants no read on
    /// the subscribe topic, or a credential the broker will never accept, is a
    /// permanent condition rather than the JWKS fetch that finishes on its own.
    /// </summary>
    public void RefuseEveryConnect() => refusals = int.MaxValue;

    /// <summary>
    /// Makes every connection last <paramref name="duration"/> and then drop —
    /// a peer that keeps taking the session back at roughly its own reconnect
    /// period. With a fixed client id two pods do exactly this to each other,
    /// and the period they converge on is a little above the shorter of their
    /// two floors, because whichever reconnects first wins the session.
    ///
    /// <para>
    /// Distinct from <see cref="DropEveryConnectionImmediately"/>, and the
    /// distance between them is the point: a connection that dies on arrival is
    /// already covered, and a connection that outlives the backoff floor by a
    /// hair is the case <c>ResetIfHeld</c>'s fixed yardstick cannot tell from a
    /// connection that held.
    /// </para>
    /// </summary>
    public void HoldEveryConnectionFor(TimeSpan duration) => holdFor = duration;

    /// <summary>
    /// Raises a disconnect for a connection that is <b>still up</b>, leaving
    /// <see cref="IsConnected"/> alone.
    ///
    /// <para>
    /// Not a contrivance: MQTTnet 5 dispatches the disconnect handler
    /// fire-and-forget — <c>DisconnectCore</c> does
    /// <c>Task.Run(...).RunInBackground(_logger)</c> and does not await it — and
    /// <c>ConnectAsync</c> calls <c>DisconnectInternal</c> on a non-success
    /// CONNACK. So a stale disconnect is in flight after <em>every</em> refusal,
    /// by design, and it can be delivered after a later CONNECT has succeeded.
    /// Raising it directly is what makes the arrival order a fact of the test
    /// rather than a race it would have to win.
    /// </para>
    /// </summary>
    public async Task RaiseStaleDisconnectAsync()
    {
        Func<MqttClientDisconnectedEventArgs, Task>? handler = DisconnectedAsync;
        if (handler is not null)
        {
            await handler(new MqttClientDisconnectedEventArgs(
                clientWasConnected: false,
                connectResult: null!,
                reason: MqttClientDisconnectReason.UnspecifiedError,
                reasonString: "a refused CONNECT's disconnect, delivered after a later one succeeded",
                userProperties: [],
                exception: null!));
        }
    }

    /// <summary>
    /// Delivers that same stale disconnect from <b>inside</b> the next CONNECT,
    /// before it is answered — the mirror of
    /// <see cref="RaiseStaleDisconnectAsync"/>, which delivers it to a client
    /// that is already up.
    ///
    /// <para>
    /// This is the arrival the loop cannot see coming. The attempt has attached
    /// its handler and is waiting on a network round trip, so the client reports
    /// no connection; a guard reading <c>IsConnected</c> therefore takes the
    /// stale event for a drop of a connection that has not happened yet, and
    /// completes the wait of the attempt that is about to succeed. Delivering it
    /// from inside the call makes that interleaving a fact of the test rather
    /// than a thread-pool starvation it would have to provoke.
    /// </para>
    /// </summary>
    public void RaiseStaleDisconnectDuringNextConnect() => staleDuringNextConnect = true;

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

    /// <summary>
    /// <b>A refused CONNECT returns; it does not throw.</b> MQTTnet 4's
    /// <c>MqttClientOptionsBuilder</c> set
    /// <c>ThrowOnNonSuccessfulConnectResponse = true</c>, so a rejection arrived
    /// as an exception and a caller that ignored the result still noticed. In
    /// MQTTnet 5 that property is gone. Verified against mosquitto 2.0.18: a bad
    /// credential answers <c>ResultCode=NotAuthorized</c> with
    /// <c>IsConnected=false</c> and throws nothing, and a good one answers
    /// <c>Success</c>. The reason code <em>is</em> the answer, so a caller that
    /// does not read it cannot tell a refusal from a connection.
    ///
    /// <para>
    /// <b>It yields before answering</b>, because a CONNECT is a network round
    /// trip and nothing in the real client can complete one synchronously. A
    /// fake that answers on the calling thread turns a caller that retries
    /// without a delay into a loop that never reaches an await — which does not
    /// merely mis-measure the spin, it hangs the test host inside the
    /// constructor that started the loop.
    /// </para>
    ///
    /// <para>
    /// <b>Connecting a client that is already connected throws</b>, as
    /// <c>MqttClient.ThrowIfConnected</c> does. No CONNECT leaves the machine in
    /// that case, so nothing is recorded and
    /// <see cref="ConnectAttempts"/> does not move — the failure is visible in
    /// the log, not in the count.
    /// </para>
    /// </summary>
    public async Task<MqttClientConnectResult> ConnectAsync(
        MqttClientOptions options, CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        if (staleDuringNextConnect)
        {
            staleDuringNextConnect = false;
            await RaiseStaleDisconnectAsync();
        }

        if (IsConnected)
        {
            throw new InvalidOperationException(
                "It is not allowed to connect with a server after the connection is established.");
        }

        Options = options;
        PresentedCredentials.Add(System.Text.Encoding.UTF8.GetString(
            options.Credentials?.GetPassword(options) ?? []));

        if (refusals > 0)
        {
            refusals--;
            IsConnected = false;

            return new MqttClientConnectResult
            {
                ResultCode = MqttClientConnectResultCode.NotAuthorized,
                ReasonString = "the broker refused the credential",
            };
        }

        IsConnected = true;

        return new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success };
    }

    /// <summary>
    /// <b>Refuses a SUBSCRIBE on a client that is not connected</b>, with the
    /// same type the real one throws. Without this the fake models a broker that
    /// accepts subscriptions over a socket nobody opened, and a loop that took a
    /// refusal for a connection would park quietly here instead of spinning as it
    /// does against mosquitto.
    /// </summary>
    public Task<MqttClientSubscribeResult> SubscribeAsync(
        MqttClientSubscribeOptions options, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new MqttClientNotConnectedException();
        }

        SubscribedTopics.AddRange(options.TopicFilters.Select(filter => filter.Topic));
        firstSubscribeSeen.TrySetResult();

        MqttClientSubscribeResult granted = new(
            packetIdentifier: 1,
            items: [.. options.TopicFilters.Select(filter =>
                new MqttClientSubscribeResultItem(filter, MqttClientSubscribeResultCode.GrantedQoS1))],
            reasonString: string.Empty,
            userProperties: []);

        if (DropEveryConnectionImmediately)
        {
            return DropThenAsync(granted);
        }

        if (holdFor is TimeSpan hold)
        {
            // Scheduled rather than awaited: a SUBSCRIBE that took the whole
            // hold to answer would move the delay into the wrong packet.
            _ = HoldThenDropAsync(hold);
        }

        return Task.FromResult(granted);
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

    private async Task<MqttClientSubscribeResult> DropThenAsync(MqttClientSubscribeResult granted)
    {
        await DropAsync();
        return granted;
    }

    private async Task HoldThenDropAsync(TimeSpan hold)
    {
        await Task.Delay(hold);
        await DropAsync();
    }
}
