using System.Net;
using System.Net.Sockets;
using MQTTnet;
using MQTTnet.Formatter;

namespace SmartSentinelEye.EventIngestion.Infrastructure.Tests;

/// <summary>
/// <b>The premise both reconnect loops now rest on, read off the real library
/// rather than a decompiler.</b> <c>DropSignal</c> in <c>MqttConnectionLoop</c>
/// and in the simulator's <c>MqttPublisher</c> decides whether a disconnect
/// event describes a connection that existed by reading
/// <see cref="MqttClientDisconnectedEventArgs.ClientWasConnected"/>. That is
/// only stronger than asking the client afterwards if MQTTnet fills it in at the
/// moment of the disconnect — a fact about MQTTnet 5.2.0.1603, not about our
/// code, and #2130's version of it came out of a decompiler.
///
/// <para>
/// So it is measured. A <see cref="TcpListener"/> on the loopback speaks MQTT
/// 3.1.1 by hand — read the CONNECT, write the four bytes of a CONNACK — because
/// 3.1.1 is what both production clients pin, and because a CONNACK small enough
/// to write as a literal is cheaper and more honest than a broker. <b>No Docker,
/// no Aspire stack, no mosquitto.</b>
/// </para>
///
/// <para>
/// Three paths, two different expected answers through one helper: a helper that
/// returned a constant, or an <c>args</c> shape that never varied, fails at
/// least one of them.
/// </para>
/// </summary>
public class MqttClientWasConnectedContractTests
{
    /// <summary>
    /// The path the whole defect turns on. A refused CONNACK makes MQTTnet run
    /// <c>DisconnectInternal</c> while the connection status is still
    /// <c>Connecting</c>, and it dispatches the handler fire-and-forget — so this
    /// event is in flight after every refusal and can land arbitrarily late. It
    /// reports <c>false</c>, which is what lets a later attempt tell it from a
    /// drop of its own.
    /// </summary>
    [Fact]
    public async Task A_refused_CONNACK_reports_a_client_that_was_never_connected()
    {
        (await ObserveAsync(ReturnCode.NotAuthorized, Ending.LeaveOpen)).ShouldBe(
            false,
            "a refused CONNECT never had a connection to lose. If this reported true, "
            + "ClientWasConnected would be no better a discriminator than IsConnected and both "
            + "DropSignals would be back to reconnecting live clients.");
    }

    /// <summary>The broker goes away under an established connection.</summary>
    [Fact]
    public async Task A_peer_that_closes_an_established_connection_reports_a_client_that_was_connected()
    {
        (await ObserveAsync(ReturnCode.Accepted, Ending.CloseTheSocket)).ShouldBe(
            true,
            "this is the only event either loop exists to notice. A false here would leave the "
            + "subscriber deaf until the pod was restarted.");
    }

    /// <summary>
    /// The subscribe-failure path: <c>MqttConnectionLoop.SubscribeAsync</c>
    /// deliberately disconnects a live client so the next attempt is a clean
    /// connect. It goes through the public <c>DisconnectAsync</c>, which captures
    /// <c>IsConnected</c> while it is still true — so that drop is honoured, and
    /// the loop needs no second mechanism to notice a disconnect it asked for.
    /// </summary>
    [Fact]
    public async Task A_deliberate_disconnect_of_a_live_client_reports_a_client_that_was_connected()
    {
        (await ObserveAsync(ReturnCode.Accepted, Ending.DisconnectFromHere)).ShouldBe(
            true,
            "connected-but-not-listening is the silent failure the subscribe-failure disconnect "
            + "exists to avoid, and the attempt has to wake up in order to retry it.");
    }

    private static async Task<bool?> ObserveAsync(byte returnCode, Ending ending)
    {
        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(20));
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        Task serving = ServeAsync(listener, returnCode, ending, stop.Token);

        TaskCompletionSource<bool> observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using IMqttClient client = new MqttClientFactory().CreateMqttClient();
        client.DisconnectedAsync += args =>
        {
            observed.TrySetResult(args.ClientWasConnected);
            return Task.CompletedTask;
        };

        await AttemptAsync(client, ((IPEndPoint)listener.LocalEndpoint).Port, ending, stop.Token);

        Task first = await Task.WhenAny(observed.Task, Task.Delay(TimeSpan.FromSeconds(10), stop.Token));
        await stop.CancelAsync();
        listener.Stop();
        await serving;

        return first == observed.Task ? observed.Task.Result : null;
    }

    private static async Task AttemptAsync(
        IMqttClient client, int port, Ending ending, CancellationToken cancellationToken)
    {
        try
        {
            await client.ConnectAsync(Options(port), cancellationToken);

            if (ending == Ending.DisconnectFromHere)
            {
                await client.DisconnectAsync(new MqttClientDisconnectOptions(), cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A refusal answers rather than throws on 5.x, but a socket that
            // closes under the handshake does not. Either way the event is the
            // subject here, not the call.
            _ = exception;
        }
    }

    private static MqttClientOptions Options(int port) =>
        new MqttClientOptionsBuilder()
            .WithTcpServer(IPAddress.Loopback.ToString(), port)
            .WithProtocolVersion(MqttProtocolVersion.V311)
            .WithClientId("client-was-connected-probe")
            .Build();

    /// <summary>
    /// A broker of four bytes. <c>0x20 0x02</c> is a CONNACK of remaining length
    /// two; the third byte is the session-present flag and the fourth is the
    /// return code — <c>0x00</c> accepted, <c>0x05</c> not authorized (MQTT
    /// 3.1.1 section 3.2.2.3).
    /// </summary>
    private static async Task ServeAsync(
        TcpListener listener, byte returnCode, Ending ending, CancellationToken cancellationToken)
    {
        try
        {
            using TcpClient peer = await listener.AcceptTcpClientAsync(cancellationToken);
            await using NetworkStream stream = peer.GetStream();

            await ReadPacketAsync(stream, cancellationToken);
            await stream.WriteAsync(new byte[] { 0x20, 0x02, 0x00, returnCode }, cancellationToken);
            await stream.FlushAsync(cancellationToken);

            if (ending == Ending.CloseTheSocket)
            {
                // Long enough for the client to finish the handshake and reach
                // Connected: a socket closed inside it is the refusal case again.
                await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
                peer.Close();
                return;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or IOException)
        {
            // The observation is over and the listener was stopped underneath us.
            _ = exception;
        }
    }

    /// <summary>Reads one MQTT packet: type byte, the varint remaining length, then that many bytes.</summary>
    private static async Task ReadPacketAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] one = new byte[1];
        await stream.ReadExactlyAsync(one, cancellationToken);

        int multiplier = 1;
        int length = 0;
        byte digit;
        do
        {
            await stream.ReadExactlyAsync(one, cancellationToken);
            digit = one[0];
            length += (digit & 127) * multiplier;
            multiplier *= 128;
        }
        while ((digit & 128) != 0);

        if (length > 0)
        {
            await stream.ReadExactlyAsync(new byte[length], cancellationToken);
        }
    }

    private static class ReturnCode
    {
        public const byte Accepted = 0x00;
        public const byte NotAuthorized = 0x05;
    }

    private enum Ending
    {
        LeaveOpen,
        CloseTheSocket,
        DisconnectFromHere,
    }
}
