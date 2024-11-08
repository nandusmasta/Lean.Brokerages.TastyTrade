using System;
using NodaTime;
using QuantConnect.Logging;
using QuantConnect.Securities;
using QuantConnect.Data.Market;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Threading.Tasks;

namespace QuantConnect.Brokerages.TastyTrade
{
    public partial class TastyTradeBrokerage
    {
        private readonly ConcurrentDictionary<string, SubscriptionData> _subscriptionsBySymbol = new();
        private readonly ConcurrentDictionary<Symbol, WebSocket[]> _webSocketsBySymbol = new();
        private readonly ClientWebSocket _quoteSocket = new();
        private readonly ClientWebSocket _tradeSocket = new();
        private readonly object _locker = new();
        private CancellationTokenSource _cancellationTokenSource;

        private bool Subscribe(IEnumerable<Symbol> symbols)
        {
            lock (_locker)
            {
                foreach (var symbol in symbols)
                {
                    var brokerageSymbol = _symbolMapper.GetBrokerageSymbol(symbol);
                    var exchangeTimeZone = MarketHoursDatabase.FromDataFolder()
                        .GetExchangeHours(symbol.ID.Market, symbol, symbol.SecurityType).TimeZone;

                    _subscriptionsBySymbol[brokerageSymbol] = new SubscriptionData
                    {
                        Symbol = symbol,
                        ExchangeTimeZone = exchangeTimeZone
                    };

                    var quoteSocket = new ClientWebSocket();
                    var tradeSocket = new ClientWebSocket();

                    ConnectWebSocket(quoteSocket, GetQuoteStreamUrl(brokerageSymbol)).Wait();
                    ConnectWebSocket(tradeSocket, GetTradeStreamUrl(brokerageSymbol)).Wait();

                    _webSocketsBySymbol[symbol] = new[] { quoteSocket, tradeSocket };

                    StartWebSocketListening(quoteSocket, brokerageSymbol, HandleQuoteMessage);
                    StartWebSocketListening(tradeSocket, brokerageSymbol, HandleTradeMessage);
                }
            }
            return true;
        }

        private bool Unsubscribe(IEnumerable<Symbol> symbols)
        {
            lock (_locker)
            {
                foreach (var symbol in symbols)
                {
                    if (_webSocketsBySymbol.TryRemove(symbol, out var sockets))
                    {
                        foreach (var socket in sockets)
                        {
                            try
                            {
                                socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Unsubscribe",
                                    CancellationToken.None).Wait();
                            }
                            catch (Exception e)
                            {
                                Log.Error($"TastyTradeBrokerage.Unsubscribe(): Error closing WebSocket: {e.Message}");
                            }
                            finally
                            {
                                socket.Dispose();
                            }
                        }
                    }

                    var brokerageSymbol = _symbolMapper.GetBrokerageSymbol(symbol);
                    _subscriptionsBySymbol.TryRemove(brokerageSymbol, out _);
                }
            }
            return true;
        }

        private async Task ConnectWebSocket(ClientWebSocket webSocket, string url)
        {
            _cancellationTokenSource = new CancellationTokenSource();
            await webSocket.ConnectAsync(new Uri(url), _cancellationTokenSource.Token);

            if (webSocket.State != WebSocketState.Open)
            {
                throw new Exception($"TastyTradeBrokerage.ConnectWebSocket(): Failed to connect to {url}");
            }

            // Authenticate WebSocket connection
            var auth = new
            {
                authorization = _sessionToken,
                action = "auth"
            };

            var buffer = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(auth));
            await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true,
                _cancellationTokenSource.Token);
        }

        private void StartWebSocketListening(ClientWebSocket socket, string symbol, Action<string, string> messageHandler)
        {
            var buffer = new byte[4096];
            Task.Run(async () =>
            {
                try
                {
                    while (socket.State == WebSocketState.Open && !_cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), _cancellationTokenSource.Token);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                            break;
                        }

                        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        messageHandler(symbol, message);
                    }
                }
                catch (Exception e)
                {
                    Log.Error($"TastyTradeBrokerage.StartWebSocketListening(): Error: {e.Message}");

                    // Notify disconnection
                    if (IsConnected)
                    {
                        _connected = false;
                        OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Disconnect,
                            "WebSocket disconnected", "Connection lost"));
                    }
                }
            });
        }

        private void HandleQuoteMessage(string symbol, string message)
        {
            try
            {
                if (!_subscriptionsBySymbol.TryGetValue(symbol, out var subscriptionData))
                {
                    return;
                }

                var data = JObject.Parse(message);
                var tick = new Tick
                {
                    Symbol = subscriptionData.Symbol,
                    Time = DateTime.UtcNow.ConvertFromUtc(subscriptionData.ExchangeTimeZone),
                    TickType = TickType.Quote,
                    BidPrice = data["bid-price"].Value<decimal>(),
                    BidSize = data["bid-size"].Value<decimal>(),
                    AskPrice = data["ask-price"].Value<decimal>(),
                    AskSize = data["ask-size"].Value<decimal>()
                };

                lock (_aggregator)
                {
                    _aggregator.Update(tick);
                }
            }
            catch (Exception e)
            {
                Log.Error($"TastyTradeBrokerage.HandleQuoteMessage(): Error parsing message: {e.Message}");
            }
        }

        private void HandleTradeMessage(string symbol, string message)
        {
            try
            {
                if (!_subscriptionsBySymbol.TryGetValue(symbol, out var subscriptionData))
                {
                    return;
                }

                var data = JObject.Parse(message);
                var tick = new Tick
                {
                    Symbol = subscriptionData.Symbol,
                    Time = DateTime.UtcNow.ConvertFromUtc(subscriptionData.ExchangeTimeZone),
                    TickType = TickType.Trade,
                    Value = data["price"].Value<decimal>(),
                    Quantity = data["size"].Value<decimal>()
                };

                lock (_aggregator)
                {
                    _aggregator.Update(tick);
                }
            }
            catch (Exception e)
            {
                Log.Error($"TastyTradeBrokerage.HandleTradeMessage(): Error parsing message: {e.Message}");
            }
        }

        private string GetQuoteStreamUrl(string symbol)
        {
            var response = _client.GetAsync($"{_baseUrl}/api-quote-tokens").Result;
            var content = response.Content.ReadAsStringAsync().Result;
            var data = JObject.Parse(content);

            return $"{data["websocket-url"]}/quote/{symbol}?token={data["token"]}";
        }

        private string GetTradeStreamUrl(string symbol)
        {
            var response = _client.GetAsync($"{_baseUrl}/api-quote-tokens").Result;
            var content = response.Content.ReadAsStringAsync().Result;
            var data = JObject.Parse(content);

            return $"{data["websocket-url"]}/trade/{symbol}?token={data["token"]}";
        }

        private class SubscriptionData
        {
            public Symbol Symbol { get; set; }
            public DateTimeZone ExchangeTimeZone { get; set; }
        }
    }
}