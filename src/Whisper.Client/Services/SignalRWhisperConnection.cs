using System.Net.Http;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Whisper.Client.Models;
using Whisper.Shared;
using Whisper.Shared.Contracts;

namespace Whisper.Client.Services;

public sealed class SignalRWhisperConnection(ILogger<SignalRWhisperConnection> logger) : IWhisperConnection
{
    private HubConnection? _hub;
    private ServerProfile? _profile;
    private Guid _clientId;
    private ClientConnectionState _state = ClientConnectionState.Disconnected;

    public ClientConnectionState State
    {
        get => _state;
        private set
        {
            if (_state == value)
            {
                return;
            }

            _state = value;
            StateChanged?.Invoke(this, value);
        }
    }

    public AuthResult? Session { get; private set; }

    public event EventHandler<ClientConnectionState>? StateChanged;

    public event EventHandler<ChatMessage>? MessageReceived;

    public event EventHandler<MemberInfo>? MemberJoined;

    public event EventHandler<Guid>? MemberLeft;

    public event EventHandler<MemberInfo>? MemberUpdated;

    public event EventHandler<SpeakingChange>? SpeakingChanged;

    public event EventHandler<IReadOnlyList<ChannelInfo>>? ChannelsUpdated;

    public event EventHandler<string>? ServerNotice;

    public async Task<AuthResult> ConnectAsync(
        ServerProfile profile,
        Guid clientId,
        CancellationToken cancellationToken = default)
    {
        await DisconnectAsync().ConfigureAwait(false);

        _profile = profile;
        _clientId = clientId;
        State = ClientConnectionState.Connecting;

        var hub = new HubConnectionBuilder()
            .WithUrl(profile.HubUrl, http =>
            {
                if (profile is { UseTls: true, AllowSelfSignedCertificate: true })
                {
                    // Both transports need telling: negotiation runs over HTTP, the hub
                    // itself over a web socket.
                    http.HttpMessageHandlerFactory = TrustAnyCertificate;
                    http.WebSocketConfiguration = socket =>
                        socket.RemoteCertificateValidationCallback = (_, _, _, _) => true;
                }
            })
            .WithAutomaticReconnect([
                TimeSpan.Zero,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10),
            ])
            .Build();

        RegisterHandlers(hub);
        _hub = hub;

        try
        {
            await hub.StartAsync(cancellationToken).ConfigureAwait(false);

            var result = await hub.InvokeAsync<AuthResult>(
                HubMethods.Authenticate,
                profile.Password,
                clientId,
                profile.DisplayName,
                cancellationToken).ConfigureAwait(false);

            if (!result.Success)
            {
                await DisconnectAsync().ConfigureAwait(false);
                return result;
            }

            Session = result;
            State = ClientConnectionState.Connected;
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not connect to {Url}.", profile.HubUrl);
            await DisconnectAsync().ConfigureAwait(false);
            return AuthResult.Failed(DescribeFailure(ex));
        }
    }

    public async Task DisconnectAsync()
    {
        var hub = _hub;
        _hub = null;
        Session = null;

        if (hub is not null)
        {
            await hub.DisposeAsync().ConfigureAwait(false);
        }

        State = ClientConnectionState.Disconnected;
    }

    public Task<ChatMessage> SendMessageAsync(int channelId, string content) =>
        Hub.InvokeAsync<ChatMessage>(HubMethods.SendMessage, channelId, content);

    public Task<HistoryPage> GetHistoryAsync(int channelId, DateTimeOffset? before, int take) =>
        Hub.InvokeAsync<HistoryPage>(HubMethods.GetHistory, channelId, before, take);

    public Task JoinVoiceAsync(int channelId) => Hub.InvokeAsync(HubMethods.JoinVoice, channelId);

    public Task LeaveVoiceAsync() => Hub.InvokeAsync(HubMethods.LeaveVoice);

    public Task SetMutedAsync(bool isMuted) => Hub.InvokeAsync(HubMethods.SetMuted, isMuted);

    public Task SetDeafenedAsync(bool isDeafened) => Hub.InvokeAsync(HubMethods.SetDeafened, isDeafened);

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);

    private HubConnection Hub =>
        _hub ?? throw new InvalidOperationException("Not connected to a server.");

    private void RegisterHandlers(HubConnection hub)
    {
        hub.On<ChatMessage>(HubMethods.MessageReceived, m => MessageReceived?.Invoke(this, m));
        hub.On<MemberInfo>(HubMethods.MemberJoined, m => MemberJoined?.Invoke(this, m));
        hub.On<Guid>(HubMethods.MemberLeft, id => MemberLeft?.Invoke(this, id));
        hub.On<MemberInfo>(HubMethods.MemberUpdated, m => MemberUpdated?.Invoke(this, m));
        hub.On<uint, bool>(
            HubMethods.SpeakingChanged,
            (ssrc, speaking) => SpeakingChanged?.Invoke(this, new SpeakingChange(ssrc, speaking)));
        hub.On<IReadOnlyList<ChannelInfo>>(HubMethods.ChannelsUpdated, c => ChannelsUpdated?.Invoke(this, c));
        hub.On<string>(HubMethods.ServerNotice, text => ServerNotice?.Invoke(this, text));

        hub.Reconnecting += _ =>
        {
            State = ClientConnectionState.Reconnecting;
            return Task.CompletedTask;
        };

        hub.Reconnected += async _ =>
        {
            // A reconnect is a brand new connection id on the server, so the session has
            // to be re-established before anything else will be accepted.
            await ReauthenticateAsync().ConfigureAwait(false);
        };

        hub.Closed += _ =>
        {
            State = ClientConnectionState.Disconnected;
            return Task.CompletedTask;
        };
    }

    private async Task ReauthenticateAsync()
    {
        if (_hub is null || _profile is null)
        {
            return;
        }

        try
        {
            var result = await _hub.InvokeAsync<AuthResult>(
                HubMethods.Authenticate,
                _profile.Password,
                _clientId,
                _profile.DisplayName).ConfigureAwait(false);

            if (result.Success)
            {
                Session = result;
                State = ClientConnectionState.Connected;
                return;
            }

            ServerNotice?.Invoke(this, result.FailureReason ?? "Reconnection was rejected.");
            await DisconnectAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Re-authentication after reconnect failed.");
            await DisconnectAsync().ConfigureAwait(false);
        }
    }

    private static HttpMessageHandler TrustAnyCertificate(HttpMessageHandler inner)
    {
        if (inner is HttpClientHandler handler)
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        return inner;
    }

    private static string DescribeFailure(Exception ex) => ex switch
    {
        HttpRequestException => "Could not reach the server. Check the address, port, and that the server is running.",
        TaskCanceledException or OperationCanceledException => "The connection attempt timed out.",
        _ => ex.Message,
    };
}
