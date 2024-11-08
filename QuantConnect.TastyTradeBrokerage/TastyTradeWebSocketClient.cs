using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using QuantConnect.Logging;
using Newtonsoft.Json.Linq;

namespace QuantConnect.Brokerages.TastyTrade
{
    public class TastyTradeWebSocketClient : IDisposable
    {
        private readonly ClientWebSocket _webSocket;
        private readonly string _url;
        private readonly string _sessionToken;
        private readonly SecurityType _securityType;
        private bool _connected;
        private CancellationTokenSource _cts;

        public event Action<JObject> MessageReceived;
        public event Action Connected;
        public event Action Disconnected;
        public event Action<Exception> Error;

        public bool IsConnected => _connected;

        public TastyTradeWebSocketClient(string url, string sessionToken, SecurityType securityType)
        {
            _url = url;
            _sessionToken = sessionToken;
            _securityType = securityType;
            _webSocket = new ClientWebSocket();
            _cts = new CancellationTokenSource();
        }

        public async Task Connect()
        {
            try
            {
                await _webSocket.ConnectAsync(new Uri(_url), _cts.Token);

                if (_webSocket.State != WebSocketState.Open)
                {
                    throw new Exception($"WebSocket failed to connect: {_webSocket.State}");
                }

                var authMessage = new JObject
                {
                    ["action"] = "auth",
                    ["authorization"] = _sessionToken
                };

                await SendMessage(authMessage.ToString());

                _connected = true;
                Connected?.Invoke();

                StartMessageLoop();
            }
            catch (Exception ex)
            {
                Log.Error($"TastyTradeWebSocketClient.Connect(): Error connecting: {ex.Message}");
                Error?.Invoke(ex);
                throw;
            }
        }

        public async Task Disconnect()
        {
            if (_connected)
            {
                _cts.Cancel();
                try
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Log.Error($"TastyTradeWebSocketClient.Disconnect(): Error closing connection: {ex.Message}");
                }
                finally
                {
                    _connected = false;
                    Disconnected?.Invoke();
                }
            }
        }

        public async Task SendMessage(string message)
        {
            if (!_connected)
            {
                throw new InvalidOperationException("Cannot send message when not connected");
            }

            var buffer = Encoding.UTF8.GetBytes(message);
            await _webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, _cts.Token);
        }

        private void StartMessageLoop()
        {
            Task.Run(async () =>
            {
                var buffer = new byte[4096];
                try
                {
                    while (_webSocket.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
                    {
                        var message = new StringBuilder();
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                await Disconnect();
                                return;
                            }

                            message.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                        }
                        while (!result.EndOfMessage);

                        try
                        {
                            var parsed = JObject.Parse(message.ToString());
                            MessageReceived?.Invoke(parsed);
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"TastyTradeWebSocketClient.StartMessageLoop(): Error parsing message: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex) when (!(ex is TaskCanceledException || ex is OperationCanceledException))
                {
                    Log.Error($"TastyTradeWebSocketClient.StartMessageLoop(): Error in message loop: {ex.Message}");
                    Error?.Invoke(ex);
                    await Disconnect();
                }
            });
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _webSocket?.Dispose();
        }
    }
}