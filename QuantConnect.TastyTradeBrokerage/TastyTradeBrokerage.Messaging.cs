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
using Alpaca.Markets;
using static QuantConnect.Brokerages.WebSocketClientWrapper;

namespace QuantConnect.Brokerages.TastyTrade
{
    public partial class TastyTradeBrokerage
    {
        private readonly ConcurrentDictionary<string, SubscriptionData> _subscriptionsBySymbol = new();
        private readonly ConcurrentDictionary<Symbol, WebSocketClientWrapper[]> _webSocketsBySymbol = new();
        private readonly ClientWebSocket _quoteSocket = new();
        private readonly ClientWebSocket _tradeSocket = new();
        private readonly object _locker = new();
        private CancellationTokenSource _cancellationTokenSource;
        private readonly ConcurrentDictionary<string, WebSocketClientWrapper> _webSockets;
        private readonly ConcurrentDictionary<string, int> _reconnectionAttempts;
        private const int MaxReconnectionAttempts = 5;
        private readonly TimeSpan _reconnectDelay = TimeSpan.FromSeconds(5);

        #region Subscription Management

        private bool Subscribe(IEnumerable<Symbol> symbols)
        {
            lock (_locker)
            {
                try
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

                        // Create a new websocket client wrapper
                        var webSocket = new WebSocketClientWrapper();

                        // Set up event handlers
                        webSocket.Message += (s, e) => HandleWebSocketMessage(e, symbol);
                        webSocket.Open += (s, e) => HandleWebSocketOpened(webSocket, symbol);
                        webSocket.Error += (s, e) => HandleWebSocketError(webSocket, symbol, e);
                        webSocket.Closed += (s, e) => HandleWebSocketClosed(webSocket, symbol, e);

                        // Get the streaming URL for this symbol
                        var wsUrl = GetStreamingUrl(brokerageSymbol);

                        // Initialize and connect
                        webSocket.Initialize(wsUrl, _sessionToken);
                        webSocket.Connect();

                        // Store the websocket
                        _webSockets[brokerageSymbol] = webSocket;

                        Log.Trace($"TastyTradeBrokerage.Subscribe(): Subscribed to {symbol} ({brokerageSymbol})");
                    }

                    return true;
                }
                catch (Exception e)
                {
                    Log.Error($"TastyTradeBrokerage.Subscribe(): Error subscribing to symbols: {e.Message}");
                    return false;
                }
            }
        }

        private bool Unsubscribe(IEnumerable<Symbol> symbols)
        {
            lock (_locker)
            {
                try
                {
                    foreach (var symbol in symbols)
                    {
                        var brokerageSymbol = _symbolMapper.GetBrokerageSymbol(symbol);

                        if (_webSockets.TryRemove(brokerageSymbol, out var webSocket))
                        {
                            webSocket.Close();
                        }

                        _subscriptionsBySymbol.TryRemove(brokerageSymbol, out _);

                        Log.Trace($"TastyTradeBrokerage.Unsubscribe(): Unsubscribed from {symbol} ({brokerageSymbol})");
                    }

                    return true;
                }
                catch (Exception e)
                {
                    Log.Error($"TastyTradeBrokerage.Unsubscribe(): Error unsubscribing from symbols: {e.Message}");
                    return false;
                }
            }
        }

        private class SubscriptionData
        {
            public Symbol Symbol { get; set; }
            public DateTimeZone ExchangeTimeZone { get; set; }
        }

        #endregion

        #region WebSocket Connection

        private void InitializeWebSocket(string brokerageSymbol, Symbol symbol)
        {
            try
            {
                var webSocket = new WebSocketClientWrapper();

                // Correct event handler signatures
                webSocket.Message += (s, e) => HandleWebSocketMessage(e, symbol);
                webSocket.Open += (s, e) => HandleWebSocketOpened(webSocket, symbol);
                webSocket.Error += (s, e) => HandleWebSocketError(webSocket, symbol, e);
                webSocket.Closed += (s, e) => HandleWebSocketClosed(webSocket, symbol, e);

                var wsUrl = GetStreamingUrl(brokerageSymbol);
                webSocket.Initialize(wsUrl, _sessionToken);

                _webSockets[brokerageSymbol] = webSocket;
                _reconnectionAttempts[brokerageSymbol] = 0;

                webSocket.Connect();

                Log.Trace($"TastyTradeBrokerage.InitializeWebSocket(): Created socket for {symbol} ({brokerageSymbol})");
            }
            catch (Exception ex)
            {
                Log.Error($"TastyTradeBrokerage.InitializeWebSocket(): Error initializing WebSocket for {symbol}: {ex.Message}");
                // Only bubble up critical initialization errors
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, -1,
                    $"Error initializing WebSocket for {symbol}: {ex.Message}"));
            }
        }

        private void HandleWebSocketMessage(WebSocketMessage e, Symbol symbol)
        {
            try
            {
                if (e.Data is TextMessage textMessage)
                {
                    var data = JObject.Parse(textMessage.Message);
                    var messageType = data["type"]?.ToString();

                    switch (messageType)
                    {
                        case "quote":
                            ProcessQuoteMessage(data, symbol);
                            break;
                        case "trade":
                            ProcessTradeMessage(data, symbol);
                            break;
                        default:
                            Log.Trace($"TastyTradeBrokerage.HandleWebSocketMessage(): Received unknown message type: {messageType}");
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"TastyTradeBrokerage.HandleWebSocketMessage(): Error processing message: {ex.Message}");
            }
        }

        private void HandleWebSocketError(WebSocketClientWrapper webSocket, Symbol symbol, WebSocketError e)
        {
            var brokerageSymbol = _symbolMapper.GetBrokerageSymbol(symbol);
            Log.Debug($"TastyTradeBrokerage.HandleWebSocketError(): WebSocket error for {symbol}: {e.Message}");

            // Attempt reconnection if this wasn't triggered by an intentional disconnection
            if (_webSockets.ContainsKey(brokerageSymbol))
            {
                TryReconnect(webSocket, brokerageSymbol, symbol);
            }
        }

        private void HandleWebSocketOpened(WebSocketClientWrapper webSocket, Symbol symbol)
        {
            try
            {
                var brokerageSymbol = _symbolMapper.GetBrokerageSymbol(symbol);

                // Reset reconnection attempts on successful connection
                _reconnectionAttempts[brokerageSymbol] = 0;

                Log.Trace($"TastyTradeBrokerage.HandleWebSocketOpened(): Connection established for {symbol}");

                // Resubscribe to the data feed
                var subscribeMessage = new
                {
                    action = "subscribe",
                    symbol = brokerageSymbol
                };

                webSocket.Send(JsonConvert.SerializeObject(subscribeMessage));
            }
            catch (Exception e)
            {
                Log.Error($"TastyTradeBrokerage.HandleWebSocketOpened(): Error in connection handling: {e.Message}");
            }
        }

        private void HandleWebSocketClosed(WebSocketClientWrapper webSocket, Symbol symbol, WebSocketCloseData closeData)
        {
            var brokerageSymbol = _symbolMapper.GetBrokerageSymbol(symbol);
            Log.Trace($"TastyTradeBrokerage.HandleWebSocketClosed(): Connection closed for {symbol}: Code: {closeData.Code}, Reason: {closeData.Reason}, Clean: {closeData.WasClean}");

            // If it wasn't a clean close, attempt reconnection
            if (!closeData.WasClean && _webSockets.ContainsKey(brokerageSymbol))
            {
                TryReconnect(webSocket, brokerageSymbol, symbol);
            }
        }

        private void TryReconnect(WebSocketClientWrapper webSocket, string brokerageSymbol, Symbol symbol)
        {
            try
            {
                lock (_locker)
                {
                    if (!_reconnectionAttempts.TryGetValue(brokerageSymbol, out var attempts))
                    {
                        attempts = 0;
                    }

                    if (attempts >= MaxReconnectionAttempts)
                    {
                        Log.Error($"TastyTradeBrokerage.TryReconnect(): Max reconnection attempts reached for {symbol}");
                        // Only now do we bubble up the error after max retries
                        OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, -1,
                            $"Unable to reconnect WebSocket after {MaxReconnectionAttempts} attempts for {symbol}"));
                        return;
                    }

                    _reconnectionAttempts[brokerageSymbol] = attempts + 1;

                    // Implement exponential backoff
                    var delay = TimeSpan.FromMilliseconds(_reconnectDelay.TotalMilliseconds * Math.Pow(2, attempts));
                    Thread.Sleep(delay);

                    Log.Trace($"TastyTradeBrokerage.TryReconnect(): Attempting reconnection {attempts + 1} of {MaxReconnectionAttempts} for {symbol}");

                    webSocket.Close();
                    InitializeWebSocket(brokerageSymbol, symbol);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"TastyTradeBrokerage.TryReconnect(): Error during reconnection for {symbol}: {ex.Message}");
            }
        }

        private string GetStreamingUrl(string symbol)
        {
            // Get streaming URL from TastyTrade API
            var response = _client.GetAsync($"{_baseUrl}/streaming/tokens").Result;
            var content = response.Content.ReadAsStringAsync().Result;

            if (!response.IsSuccessStatusCode)
            {
                throw new BrokerageException($"Failed to get streaming URL: {content}");
            }

            var data = JObject.Parse(content);
            var wsUrl = data["websocket-url"].ToString();
            var token = data["token"].ToString();

            return $"{wsUrl}/stream?symbol={symbol}&token={token}";
        }

        #endregion

        #region Quote/Trade Messages

        private void ProcessQuoteMessage(JObject message, Symbol symbol)
        {
            try
            {
                var subscription = _subscriptionsBySymbol[_symbolMapper.GetBrokerageSymbol(symbol)];

                var tick = new Tick
                {
                    Symbol = symbol,
                    Time = DateTime.UtcNow.ConvertFromUtc(subscription.ExchangeTimeZone),
                    TickType = TickType.Quote,
                    BidPrice = message["bid"].Value<decimal>(),
                    BidSize = message["bidSize"].Value<decimal>(),
                    AskPrice = message["ask"].Value<decimal>(),
                    AskSize = message["askSize"].Value<decimal>()
                };

                _aggregator.Update(tick);
            }
            catch (Exception e)
            {
                Log.Error($"TastyTradeBrokerage.ProcessQuoteMessage(): Error processing quote: {e.Message}");
            }
        }

        private void ProcessTradeMessage(JObject message, Symbol symbol)
        {
            try
            {
                var subscription = _subscriptionsBySymbol[_symbolMapper.GetBrokerageSymbol(symbol)];

                var tick = new Tick
                {
                    Symbol = symbol,
                    Time = DateTime.UtcNow.ConvertFromUtc(subscription.ExchangeTimeZone),
                    TickType = TickType.Trade,
                    Value = message["price"].Value<decimal>(),
                    Quantity = message["size"].Value<decimal>()
                };

                _aggregator.Update(tick);
            }
            catch (Exception e)
            {
                Log.Error($"TastyTradeBrokerage.ProcessTradeMessage(): Error processing trade: {e.Message}");
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