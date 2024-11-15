﻿using System;
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

        #region Subscription Management

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

                    ConnectWebSocketWithRetry(quoteSocket, GetQuoteStreamUrl(brokerageSymbol)).Wait();
                    ConnectWebSocketWithRetry(tradeSocket, GetTradeStreamUrl(brokerageSymbol)).Wait();

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

        private class SubscriptionData
        {
            public Symbol Symbol { get; set; }
            public DateTimeZone ExchangeTimeZone { get; set; }
        }

        #endregion

        #region WebSocket Connection

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

        private async Task ConnectWebSocketWithRetry(ClientWebSocket webSocket, string url, int maxRetries = 3)
        {
            int retryCount = 0;
            bool connected = false;
            Exception lastException = null;

            while (!connected && retryCount < maxRetries)
            {
                try
                {
                    _cancellationTokenSource = new CancellationTokenSource();
                    _cancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(30)); // Add timeout

                    Log.Trace($"TastyTradeBrokerage.ConnectWebSocket(): Attempting to connect to {url}");
                    await webSocket.ConnectAsync(new Uri(url), _cancellationTokenSource.Token);

                    if (webSocket.State == WebSocketState.Open)
                    {
                        // Send authentication message
                        var auth = new
                        {
                            authorization = _sessionToken,
                            action = "auth"
                        };

                        var buffer = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(auth));
                        await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true,
                            _cancellationTokenSource.Token);

                        connected = true;
                        Log.Trace($"TastyTradeBrokerage.ConnectWebSocket(): Successfully connected and authenticated to {url}");
                    }
                    else
                    {
                        throw new BrokerageException($"WebSocket failed to connect. State: {webSocket.State}");
                    }
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    retryCount++;

                    if (retryCount < maxRetries)
                    {
                        var delay = Math.Pow(2, retryCount); // Exponential backoff
                        Log.Error($"TastyTradeBrokerage.ConnectWebSocket(): Attempt {retryCount} failed. Retrying in {delay} seconds. Error: {ex.Message}");
                        await Task.Delay(TimeSpan.FromSeconds(delay));
                    }
                }
            }

            if (!connected)
            {
                throw new BrokerageException($"Failed to connect after {maxRetries} attempts", lastException);
            }
        }

        private void StartWebSocketListening(ClientWebSocket socket, string symbol, Action<string, string> messageHandler)
        {
            var buffer = new byte[4096];
            var messageBuilder = new StringBuilder();

            Task.Run(async () =>
            {
                try
                {
                    while (socket.State == WebSocketState.Open && !_cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), _cancellationTokenSource.Token);
                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                                break;
                            }

                            messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                        }
                        while (!result.EndOfMessage);

                        if (result.MessageType != WebSocketMessageType.Close)
                        {
                            var message = messageBuilder.ToString();
                            messageBuilder.Clear();

                            try
                            {
                                messageHandler(symbol, message);
                            }
                            catch (Exception e)
                            {
                                Log.Error($"TastyTradeBrokerage.StartWebSocketListening(): Error processing message: {e.Message}");
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.Error($"TastyTradeBrokerage.StartWebSocketListening(): Error: {e.Message}");

                    // Notify disconnection
                    if (IsConnected)
                    {
                        _connected = false;
                        OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Disconnect, -1,
                            $"WebSocket disconnected: {e.Message}"));
                    }
                }
            });
        }

        #endregion

        #region Quote/Trade Messages

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
            try
            {
                var response = _client.GetAsync($"{_baseUrl}/api-quote-tokens").Result;
                var content = response.Content.ReadAsStringAsync().Result;

                if (!response.IsSuccessStatusCode)
                {
                    throw new Exception($"Failed to get streaming URL: {content}");
                }

                var data = JObject.Parse(content);
                var wsUrl = data["data"]?["websocket-url"]?.ToString();
                var token = data["data"]?["token"]?.ToString();

                if (string.IsNullOrEmpty(wsUrl) || string.IsNullOrEmpty(token))
                {
                    throw new Exception("Missing websocket URL or token in response");
                }

                return $"{wsUrl}/quote/{symbol}?token={token}";
            }
            catch (Exception e)
            {
                Log.Error($"TastyTradeBrokerage.GetQuoteStreamUrl(): {e.Message}");
                throw;
            }
        }

        private string GetTradeStreamUrl(string symbol)
        {
            var response = _client.GetAsync($"{_baseUrl}/api-quote-tokens").Result;
            var content = response.Content.ReadAsStringAsync().Result;
            var data = JObject.Parse(content);

            return $"{data["websocket-url"]}/trade/{symbol}?token={data["token"]}";
        }

        #endregion

    }
}