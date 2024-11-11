using System;
using System.Linq;
using QuantConnect.Data;
using QuantConnect.Util;
using QuantConnect.Orders;
using QuantConnect.Logging;
using QuantConnect.Interfaces;
using QuantConnect.Securities;
using QuantConnect.Orders.Fees;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using QuantConnect.Api;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using QuantConnect.Configuration;
using System.Collections.Concurrent;
using QLNet;
using System.Net.WebSockets;
using System.Threading;
using QuantConnect.Data.Market;

namespace QuantConnect.Brokerages.TastyTrade
{
    [BrokerageFactory(typeof(TastyTradeBrokerageFactory))]
    public partial class TastyTradeBrokerage : Brokerage
    {
        private IHttpClient _client;
        private TastyTradeSymbolMapper _symbolMapper;
        private string _sessionToken;
        private string _accountId;

        private IDataAggregator _aggregator;
        private IOrderProvider _orderProvider;
        private ISecurityProvider _securityProvider;
        
        private readonly ConcurrentDictionary<string, TastyTradeWebSocketClient> _websocketClients = new();
        private EventBasedDataQueueHandlerSubscriptionManager _subscriptionManager;
        private ConcurrentDictionary<int, decimal> _orderIdToFillQuantity;
        private BrokerageConcurrentMessageHandler<ITastyTradeOrderUpdate> _messageHandler;

        private readonly string _baseUrl = "https://api.tastyworks.com";
        private bool _isInitialized;
        private bool _connected;

        public override bool IsConnected => _connected;

        public TastyTradeBrokerage() : base("TastyTrade")
        {
            _client = new HttpClientWrapper();
            _orderIdToFillQuantity = new ConcurrentDictionary<int, decimal>();
        }

        public TastyTradeBrokerage(string username, string password, string sessionToken, IAlgorithm algorithm)
            : this(username, password, sessionToken, algorithm?.Portfolio?.Transactions, algorithm?.Portfolio)
        {
        }

        public TastyTradeBrokerage(string username, string password, string sessionToken, IOrderProvider orderProvider, ISecurityProvider securityProvider) : base("TastyTrade")
        {
            _client = new HttpClientWrapper();
            _orderIdToFillQuantity = new ConcurrentDictionary<int, decimal>();
            Initialize(username, password, sessionToken, orderProvider, securityProvider);
        }

        private void Initialize(string username, string password, string sessionToken, IOrderProvider orderProvider, ISecurityProvider securityProvider)
        {
            if (_isInitialized)
            {
                return;
            }
            _isInitialized = true;

            _sessionToken = sessionToken;
            _orderProvider = orderProvider;
            _securityProvider = securityProvider;
            _symbolMapper = new TastyTradeSymbolMapper();

            if (string.IsNullOrEmpty(sessionToken))
            {
                _sessionToken = AuthenticateSession(username, password).Result;
            }

            _client.DefaultRequestHeaders.Add("Authorization", _sessionToken);

            _messageHandler = new BrokerageConcurrentMessageHandler<ITastyTradeOrderUpdate>(HandleOrderUpdate);

            if (_orderProvider != null)
            {
                // Setup WebSocket for order updates
                // TODO: implement WebSocket order updates
            }

            _subscriptionManager = new EventBasedDataQueueHandlerSubscriptionManager();
            _subscriptionManager.SubscribeImpl += (s, t) => Subscribe(s);
            _subscriptionManager.UnsubscribeImpl += (s, t) => Unsubscribe(s);

            _aggregator = Composer.Instance.GetPart<IDataAggregator>();
            if (_aggregator == null)
            {
                var aggregatorName = Config.Get("data-aggregator", "QuantConnect.Lean.Engine.DataFeeds.AggregationManager");
                Log.Trace($"TastyTradeBrokerage(): Creating {aggregatorName}");
                _aggregator = Composer.Instance.GetExportedValueByTypeName<IDataAggregator>(aggregatorName);
            }
        }

        private async Task<string> AuthenticateSession(string username, string password)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/sessions")
            {
                Content = new StringContent(JsonConvert.SerializeObject(new
                {
                    login = username,
                    password = password
                }), Encoding.UTF8, "application/json")
            };

            var response = await _client.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Authentication failed: {content}");
            }

            var result = JsonConvert.DeserializeObject<JObject>(content);
            return result["session-token"].ToString();
        }

        public override List<Order> GetOpenOrders()
        {
            var response = _client.GetAsync($"{_baseUrl}/accounts/{GetAccountId()}/orders/live").Result;
            var content = response.Content.ReadAsStringAsync().Result;
            var orders = JsonConvert.DeserializeObject<JArray>(content);

            var leanOrders = new List<Order>();
            foreach (var order in orders)
            {
                var leanSymbol = _symbolMapper.GetLeanSymbol(order["instrument-type"].ToString(), order["symbol"].ToString());
                var quantity = order["order-side"].ToString() == "Buy" ?
                    decimal.Parse(order["quantity"].ToString()) :
                    -decimal.Parse(order["quantity"].ToString());

                Order leanOrder;
                switch (order["order-type"].ToString())
                {
                    case "Market":
                        leanOrder = new MarketOrder(leanSymbol, quantity, order["received-at"].Value<DateTime>());
                        break;
                    case "Limit":
                        leanOrder = new LimitOrder(leanSymbol, quantity,
                            order["limit-price"].Value<decimal>(),
                            order["received-at"].Value<DateTime>());
                        break;
                    case "Stop":
                        leanOrder = new StopMarketOrder(leanSymbol, quantity,
                            order["stop-price"].Value<decimal>(),
                            order["received-at"].Value<DateTime>());
                        break;
                    case "StopLimit":
                        leanOrder = new StopLimitOrder(leanSymbol, quantity,
                            order["stop-price"].Value<decimal>(),
                            order["limit-price"].Value<decimal>(),
                            order["received-at"].Value<DateTime>());
                        break;
                    default:
                        continue;
                }

                leanOrder.Status = ConvertOrderStatus(order["status"].ToString());
                leanOrder.BrokerId.Add(order["id"].ToString());
                leanOrders.Add(leanOrder);
            }

            return leanOrders;
        }

        public override List<Holding> GetAccountHoldings()
        {
            var response = _client.GetAsync($"{_baseUrl}/accounts/{GetAccountId()}/positions").Result;
            var content = response.Content.ReadAsStringAsync().Result;
            var positions = JsonConvert.DeserializeObject<JArray>(content);

            var holdings = new List<Holding>();
            foreach (var position in positions)
            {
                holdings.Add(new Holding
                {
                    Symbol = _symbolMapper.GetLeanSymbol(
                        position["instrument-type"].ToString(),
                        position["symbol"].ToString()),
                    Quantity = position["quantity"].Value<decimal>(),
                    AveragePrice = position["average-open-price"].Value<decimal>(),
                    MarketPrice = position["mark-price"].Value<decimal>(),
                    MarketValue = position["mark"].Value<decimal>(),
                    UnrealizedPnL = position["unrealized-day-gain"].Value<decimal>(),
                    CurrencySymbol = Currencies.USD
                });
            }
            return holdings;
        }

        public override List<CashAmount> GetCashBalance()
        {
            var response = _client.GetAsync($"{_baseUrl}/accounts/{GetAccountId()}/balances").Result;
            var content = response.Content.ReadAsStringAsync().Result;
            var balance = JsonConvert.DeserializeObject<JObject>(content);

            return new List<CashAmount>
            {
                new CashAmount(balance["cash-balance"].Value<decimal>(),
                             balance["currency"].ToString())
            };
        }

        public override bool PlaceOrder(Order order)
        {
            try
            {
                var request = CreateOrderRequest(order);
                var response = _client.PostAsync($"{_baseUrl}/accounts/{GetAccountId()}/orders", request).Result;
                var content = response.Content.ReadAsStringAsync().Result;

                if (!response.IsSuccessStatusCode)
                {
                    OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero,
                        $"Order Placement Failed: {content}")
                    { Status = OrderStatus.Invalid });
                    return false;
                }

                var result = JsonConvert.DeserializeObject<JObject>(content);
                order.BrokerId.Add(result["id"].ToString());

                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero,
                    "TastyTrade Order Event")
                { Status = OrderStatus.Submitted });

                return true;
            }
            catch (Exception ex)
            {
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero,
                    ex.Message)
                { Status = OrderStatus.Invalid });
                return false;
            }
        }

        public override bool UpdateOrder(Order order)
        {
            try
            {
                var request = CreateOrderRequest(order);
                var orderId = order.BrokerId.Last();
                var response = _client.PutAsync(
                    $"{_baseUrl}/accounts/{GetAccountId()}/orders/{orderId}",
                    request).Result;

                var content = response.Content.ReadAsStringAsync().Result;
                if (!response.IsSuccessStatusCode)
                {
                    OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero,
                        $"Order Update Failed: {content}")
                    { Status = OrderStatus.Invalid });
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero,
                    ex.Message)
                { Status = OrderStatus.Invalid });
                return false;
            }
        }

        public override bool CancelOrder(Order order)
        {
            try
            {
                var orderId = order.BrokerId.Last();
                var response = _client.DeleteAsync(
                    $"{_baseUrl}/accounts/{GetAccountId()}/orders/{orderId}").Result;

                if (!response.IsSuccessStatusCode)
                {
                    var content = response.Content.ReadAsStringAsync().Result;
                    OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero,
                        $"Order Cancellation Failed: {content}")
                    { Status = OrderStatus.Invalid });
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero,
                    ex.Message)
                { Status = OrderStatus.Invalid });
                return false;
            }
        }

        public override void Connect()
        {
            if (_connected) return;

            try
            {
                // Initialize account details first
                _accountId = GetAccountId();

                // Initialize WebSocket connections
                var quoteSocket = new TastyTradeWebSocketClient(GetQuoteStreamUrl(_accountId), _sessionToken, SecurityType.Equity);
                var tradeSocket = new TastyTradeWebSocketClient(GetTradeStreamUrl(_accountId), _sessionToken, SecurityType.Equity);

                // Set up message handlers
                quoteSocket.MessageReceived += (msg) => HandleQuoteMessage(msg);
                tradeSocket.MessageReceived += (msg) => HandleTradeMessage(msg);

                // Set up error handlers
                quoteSocket.Error += (ex) => OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, -1, $"Quote socket error: {ex.Message}"));
                tradeSocket.Error += (ex) => OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, -1, $"Trade socket error: {ex.Message}"));

                // Connect sockets
                quoteSocket.Connect().Wait();
                tradeSocket.Connect().Wait();

                // Store socket clients
                _websocketClients["quote"] = quoteSocket;
                _websocketClients["trade"] = tradeSocket;

                _connected = true;
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Reconnect, -1, "Connected successfully to TastyTrade"));
            }
            catch (Exception e)
            {
                Log.Error($"TastyTradeBrokerage.Connect(): Error connecting: {e.Message}");
                _connected = false;
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, -1, $"Error connecting to TastyTrade: {e.Message}"));
                throw;
            }
        }

        public override void Disconnect()
        {
            if (!_connected) return;

            try
            {
                foreach (var client in _websocketClients.Values)
                {
                    try
                    {
                        client.Disconnect().Wait(TimeSpan.FromSeconds(2));
                        client.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"TastyTradeBrokerage.Disconnect(): Error disconnecting WebSocket client: {ex.Message}");
                    }
                }

                _websocketClients.Clear();
                _connected = false;
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Disconnect, -1, "Disconnected from TastyTrade"));
            }
            catch (Exception e)
            {
                Log.Error($"TastyTradeBrokerage.Disconnect(): Error during disconnect: {e.Message}");
                throw;
            }
        }

        private string GetAccountId()
        {
            if (!string.IsNullOrEmpty(_accountId))
            {
                return _accountId;
            }

            try
            {
                var response = _client.GetAsync($"{_baseUrl}/customers/me/accounts").Result;
                var content = response.Content.ReadAsStringAsync().Result;

                if (!response.IsSuccessStatusCode)
                {
                    throw new BrokerageException($"Error getting account information: {content}");
                }

                var accounts = JObject.Parse(content);

                // Get the first active trading account
                var tradingAccount = accounts["data"]["items"]
                    .FirstOrDefault(a => a["type"].ToString() == "trading" &&
                                       a["status"].ToString() == "active");

                if (tradingAccount == null)
                {
                    throw new BrokerageException("No active trading account found");
                }

                _accountId = tradingAccount["account-number"].ToString();

                if (string.IsNullOrEmpty(_accountId))
                {
                    throw new BrokerageException("Account number not found in response");
                }

                return _accountId;
            }
            catch (Exception e)
            {
                var message = $"Error retrieving account ID: {e.Message}";
                Log.Error($"TastyTradeBrokerage.GetAccountId(): {message}");
                throw new BrokerageException(message, e);
            }
        }

        private void HandleQuoteMessage(JObject message)
        {
            try
            {
                if (!_subscriptionsBySymbol.TryGetValue(message["symbol"].ToString(), out var subscription))
                {
                    return;
                }

                var tick = new Tick
                {
                    Symbol = subscription.Symbol,
                    Time = DateTime.UtcNow.ConvertFromUtc(subscription.ExchangeTimeZone),
                    TickType = TickType.Quote,
                    BidPrice = message["bid"].Value<decimal>(),
                    AskPrice = message["ask"].Value<decimal>(),
                    BidSize = message["bidSize"].Value<decimal>(),
                    AskSize = message["askSize"].Value<decimal>()
                };

                _aggregator.Update(tick);
            }
            catch (Exception e)
            {
                Log.Error($"TastyTradeBrokerage.HandleQuoteMessage(): Error processing quote message: {e.Message}");
            }
        }

        private void HandleTradeMessage(JObject message)
        {
            try
            {
                if (!_subscriptionsBySymbol.TryGetValue(message["symbol"].ToString(), out var subscription))
                {
                    return;
                }

                var tick = new Tick
                {
                    Symbol = subscription.Symbol,
                    Time = DateTime.UtcNow.ConvertFromUtc(subscription.ExchangeTimeZone),
                    TickType = TickType.Trade,
                    Value = message["price"].Value<decimal>(),
                    Quantity = message["size"].Value<decimal>()
                };

                _aggregator.Update(tick);
            }
            catch (Exception e)
            {
                Log.Error($"TastyTradeBrokerage.HandleTradeMessage(): Error processing trade message: {e.Message}");
            }
        }

        private HttpContent CreateOrderRequest(Order order)
        {
            var requestObj = new
            {
                order_type = ConvertOrderType(order),
                time_in_force = ConvertTimeInForce(order),
                price = GetOrderPrice(order),
                price_effect = GetPriceEffect(order),
                legs = new[]
                {
                    new
                    {
                        instrument_type = _symbolMapper.GetBrokerageSecurityType(order.Symbol),
                        symbol = _symbolMapper.GetBrokerageSymbol(order.Symbol),
                        action = GetOrderAction(order),
                        quantity = Math.Abs(order.Quantity)
                    }
                }
            };

            return new StringContent(
                JsonConvert.SerializeObject(requestObj),
                Encoding.UTF8,
                "application/json");
        }

        private string ConvertOrderType(Order order)
        {
            switch (order)
            {
                case MarketOrder _:
                    return "Market";
                case LimitOrder _:
                    return "Limit";
                case StopMarketOrder _:
                    return "Stop";
                case StopLimitOrder _:
                    return "StopLimit";
                default:
                    throw new NotSupportedException($"Order type {order.Type} not supported");
            }
        }

        private string ConvertTimeInForce(Order order)
        {
            return "Day"; // Default to Day orders for now
        }

        private decimal? GetOrderPrice(Order order)
        {
            switch (order)
            {
                case LimitOrder limitOrder:
                    return limitOrder.LimitPrice;
                case StopMarketOrder stopOrder:
                    return stopOrder.StopPrice;
                case StopLimitOrder stopLimitOrder:
                    return stopLimitOrder.LimitPrice;
                default:
                    return null;
            }
        }

        private string GetPriceEffect(Order order)
        {
            return order.Quantity > 0 ? "Debit" : "Credit";
        }

        private string GetOrderAction(Order order)
        {
            if (order.Quantity > 0)
            {
                return "Buy";
            }
            return "Sell";
        }

        private OrderStatus ConvertOrderStatus(string tastyTradeStatus)
        {
            switch (tastyTradeStatus.ToLowerInvariant())
            {
                case "received":
                    return OrderStatus.Submitted;
                case "cancelled":
                    return OrderStatus.Canceled;
                case "filled":
                    return OrderStatus.Filled;
                case "partially_filled":
                    return OrderStatus.PartiallyFilled;
                case "rejected":
                    return OrderStatus.Invalid;
                default:
                    return OrderStatus.None;
            }
        }

        private void HandleOrderUpdate(ITastyTradeOrderUpdate update)
        {
            // TODO: Implement order update handling
        }

        // Improved cleanup in Dispose method
        public override void Dispose()
        {
            // Cancel any pending operations
            _cancellationTokenSource?.Cancel();

            // Cleanup WebSockets
            foreach (var sockets in _webSocketsBySymbol.Values)
            {
                foreach (var socket in sockets)
                {
                    try
                    {
                        if (socket != null)
                        {
                            if (socket.State == WebSocketState.Open)
                            {
                                socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposing",
                                    CancellationToken.None).Wait(TimeSpan.FromSeconds(1));
                            }
                            socket.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"TastyTradeBrokerage.Dispose(): Error disposing WebSocket: {ex.Message}");
                    }
                }
            }
            _webSocketsBySymbol.Clear();

            // Cleanup other resources
            _client?.Dispose();
            _aggregator?.Dispose();
            _messageHandler = null; // Simply null out the message handler
            _cancellationTokenSource?.Dispose();

            base.Dispose();
        }

        protected virtual IQuote GetLatestQuote(Symbol symbol)
        {
            try
            {
                var brokerageSymbol = _symbolMapper.GetBrokerageSymbol(symbol);

                // Get quote from TastyTrade API
                var url = $"{_baseUrl}/quote-tokens/{brokerageSymbol}";
                var response = _client.GetAsync(url).Result;
                var content = response.Content.ReadAsStringAsync().Result;

                if (!response.IsSuccessStatusCode)
                {
                    throw new Exception($"Error getting quote: {content}");
                }

                var data = JObject.Parse(content);

                return new Quote
                {
                    Symbol = symbol,
                    Time = DateTime.UtcNow,
                    BidPrice = data["bid-price"].Value<decimal>(),
                    AskPrice = data["ask-price"].Value<decimal>(),
                    BidSize = data["bid-size"].Value<decimal>(),
                    AskSize = data["ask-size"].Value<decimal>()
                };
            }
            catch (Exception e)
            {
                throw new BrokerageException($"GetLatestQuote failed for symbol {symbol}: {e.Message}");
            }
        }
    }
}