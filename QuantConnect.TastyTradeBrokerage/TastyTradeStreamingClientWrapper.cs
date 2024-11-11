using System;
using System.Threading;
using QuantConnect.Util;
using System.Threading.Tasks;
using System.Net.Http;
using Newtonsoft.Json.Linq;
using QuantConnect.Logging;

namespace QuantConnect.Brokerages.TastyTrade
{
    public class TastyTradeStreamingClientWrapper : IStreamingClient
    {
        private readonly string _sessionToken;
        private readonly SecurityType _securityType;
        private readonly string _baseUrl = "https://api.tastyworks.com";
        private readonly HttpClient _httpClient;
        private TastyTradeWebSocketClient _streamingClient;

        private bool _connectionFailed;
        private bool _authenticatedStream;
        private readonly SemaphoreSlim _connectionLock = new SemaphoreSlim(1, 1);

        public event Action<AuthStatus> Connected;
        public event Action SocketOpened;
        public event Action SocketClosed;
        public event Action<Exception> OnError;
        public event Action<string> OnWarning;
        public event Action<string> EnvironmentFailure;

        public TastyTradeWebSocketClient StreamingClient => _streamingClient;

        public TastyTradeStreamingClientWrapper(string sessionToken, SecurityType securityType)
        {
            _sessionToken = sessionToken;
            _securityType = securityType;
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("Authorization", sessionToken);
        }

        public async Task<AuthStatus> ConnectAndAuthenticateAsync(CancellationToken cancellationToken = default)
        {
            var result = AuthStatus.Unauthorized;

            try
            {
                await _connectionLock.WaitAsync(cancellationToken);
                if (_authenticatedStream)
                {
                    return AuthStatus.Authorized;
                }

                try
                {
                    // Get streaming URL and token from API
                    var response = await _httpClient.GetAsync($"{_baseUrl}/api-quote-tokens");
                    var content = await response.Content.ReadAsStringAsync();
                    var data = JObject.Parse(content);

                    var wsUrl = data["websocket-url"].ToString();
                    var streamToken = data["token"].ToString();

                    var streamUrl = GetStreamingUrl(wsUrl, streamToken);
                    Log.Trace($"TastyTradeStreamingClientWrapper.ConnectAndAuthenticateAsync({_securityType}): Connecting to {streamUrl}");

                    // Create and connect WebSocket client
                    _streamingClient = new TastyTradeWebSocketClient(streamUrl, _sessionToken, _securityType);

                    _streamingClient.Connected += () =>
                    {
                        _authenticatedStream = true;
                        SocketOpened?.Invoke();
                        Connected?.Invoke(AuthStatus.Authorized);
                    };

                    _streamingClient.Disconnected += () =>
                    {
                        _authenticatedStream = false;
                        SocketClosed?.Invoke();
                    };

                    _streamingClient.Error += (ex) =>
                    {
                        _connectionFailed = true;
                        OnError?.Invoke(ex);
                    };

                    await _streamingClient.Connect();

                    if (_authenticatedStream && !_connectionFailed)
                    {
                        result = AuthStatus.Authorized;
                        Log.Trace($"TastyTradeStreamingClientWrapper.ConnectAndAuthenticateAsync({_securityType}): Successfully connected");
                    }
                    else
                    {
                        var message = $"{_securityType} failed to connect to streaming feed";
                        EnvironmentFailure?.Invoke(message);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"TastyTradeStreamingClientWrapper.ConnectAndAuthenticateAsync({_securityType}): {ex.Message}");
                    OnError?.Invoke(ex);
                    _connectionFailed = true;
                }
            }
            finally
            {
                _connectionLock.Release();
            }

            return result;
        }

        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await _connectionLock.WaitAsync(cancellationToken);
                try
                {
                    if (_streamingClient != null)
                    {
                        await _streamingClient.Disconnect();
                        _streamingClient.Dispose();
                        _streamingClient = null;
                        _authenticatedStream = false;
                    }
                }
                finally
                {
                    _connectionLock.Release();
                }
            }
            catch (Exception ex)
            {
                Log.Error($"TastyTradeStreamingClientWrapper.DisconnectAsync(): {ex.Message}");
            }
        }

        public void Dispose()
        {
            _streamingClient?.Dispose();
            _httpClient?.Dispose();
            _connectionLock?.Dispose();
        }

        private string GetStreamingUrl(string baseWsUrl, string streamToken)
        {
            var streamType = _securityType switch
            {
                SecurityType.Equity => "equities",
                SecurityType.Option => "options",
                SecurityType.Future => "futures",
                SecurityType.FutureOption => "futures/options",
                _ => throw new NotSupportedException($"SecurityType {_securityType} is not supported for streaming")
            };

            return $"{baseWsUrl}/{streamType}/stream?token={streamToken}";
        }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException("Use ConnectAndAuthenticateAsync instead");
        }
    }

    public interface IStreamingClient : IDisposable
    {
        event Action<AuthStatus> Connected;
        event Action SocketOpened;
        event Action SocketClosed;
        event Action<Exception> OnError;
        event Action<string> OnWarning;
        Task<AuthStatus> ConnectAndAuthenticateAsync(CancellationToken cancellationToken = default);
        Task DisconnectAsync(CancellationToken cancellationToken = default);
        Task ConnectAsync(CancellationToken cancellationToken = default);
    }

    public enum AuthStatus
    {
        Unauthorized,
        Authorized
    }
}