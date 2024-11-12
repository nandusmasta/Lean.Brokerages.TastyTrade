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
using System.Net.Http.Headers;

namespace QuantConnect.Brokerages.TastyTrade
{
    [BrokerageFactory(typeof(TastyTradeBrokerageFactory))]
    public partial class TastyTradeBrokerage : Brokerage
    {
        // Define base URLs as constants
        private const string PRODUCTION_API_URL = "https://api.tastyworks.com";
        private const string SANDBOX_API_URL = "https://api.cert.tastyworks.com";

        private readonly string _clientId;
        private readonly string _clientSecret;
        private readonly string _username;
        private readonly string _password;
        private readonly bool _paperTrading;
        private readonly bool _useOAuth;
        private string _baseUrl;
        private string _sessionToken;
        private string _rememberToken;
        private DateTime _sessionExpiration;
        private IHttpClient _client;
        private TastyTradeSymbolMapper _symbolMapper;
        private string _accountId;

        private IDataAggregator _aggregator;
        private IOrderProvider _orderProvider;
        private ISecurityProvider _securityProvider;
        
        private readonly ConcurrentDictionary<string, TastyTradeWebSocketClient> _websocketClients = new();
        private EventBasedDataQueueHandlerSubscriptionManager _subscriptionManager;
        private ConcurrentDictionary<int, decimal> _orderIdToFillQuantity;
        private BrokerageConcurrentMessageHandler<ITastyTradeOrderUpdate> _messageHandler;

        private bool _isInitialized;
        private bool _connected;

        public override bool IsConnected => _connected;

        #region Constructor & Setup

        public TastyTradeBrokerage() : base("TastyTrade")
        {
            _client = new HttpClientWrapper();
            _orderIdToFillQuantity = new ConcurrentDictionary<int, decimal>();
        }

        public TastyTradeBrokerage(string clientId, string clientSecret, string environment, bool paperTrading,
            IAlgorithm algorithm)
            : this(null, null, clientId, clientSecret, environment, paperTrading, algorithm?.Portfolio?.Transactions, algorithm?.Portfolio, true)
        {
            Log.Debug($"TastyTradeBrokerage Constructor 1: client id length: {clientId?.Length}, environment: {environment}");
        }

        public TastyTradeBrokerage(string username, string password, bool paperTrading, string environment,
            IAlgorithm algorithm)
            : this(username, password, null, null, environment, paperTrading, algorithm?.Portfolio?.Transactions, algorithm?.Portfolio, false)
        {
            Log.Debug($"TastyTradeBrokerage Constructor 1: username length: {username?.Length}, environment: {environment}");
        }

        public TastyTradeBrokerage(string username, string password, string clientId, string clientSecret, string environment, bool paperTrading,
            IOrderProvider orderProvider, ISecurityProvider securityProvider, bool useOAuth)
            : base("TastyTrade")
        {
            Log.DebuggingEnabled = true;
            Log.Debug($"TastyTradeBrokerage Constructor 2: username length: {username?.Length}, client id length: {clientId?.Length}, environment: {environment}");

            if (string.IsNullOrWhiteSpace(username))
            {
                throw new ArgumentException("Username cannot be null or empty", nameof(username));
            }
            if (string.IsNullOrWhiteSpace(password))
            {
                throw new ArgumentException("Password cannot be null or empty", nameof(password));
            }
            if (string.IsNullOrWhiteSpace(environment))
            {
                throw new ArgumentException("Environment cannot be null or empty", nameof(environment));
            }

            _username = username;
            _password = password;
            _clientId = clientId;
            _clientSecret = clientSecret;
            _useOAuth = useOAuth;
            _paperTrading = paperTrading;

            if (_paperTrading)
            {
                Log.Trace("TastyTradeBrokerage Constructor: Paper trading enabled - will switch to paper account after authentication");
            }

            SetupBrokerage(environment, orderProvider, securityProvider);
        }

        private void SetupBrokerage(string environment, IOrderProvider orderProvider, ISecurityProvider securityProvider)
        {
            _baseUrl = environment.ToLowerInvariant() switch
            {
                "sandbox" or "cert" => SANDBOX_API_URL,
                _ => PRODUCTION_API_URL
            };

            Log.Debug($"TastyTradeBrokerage: Initializing with environment: {environment}");
            Log.Debug($"TastyTradeBrokerage: Using base URL: {_baseUrl}");
            Log.Debug($"TastyTradeBrokerage: Using authentication method: {(_useOAuth ? "OAuth" : "Credentials")}");

            Initialize(orderProvider, securityProvider);
        }

        #endregion

        #region Initialization

        private void Initialize(string username, string password, string sessionToken,
        IOrderProvider orderProvider, ISecurityProvider securityProvider)
        {
            if (_isInitialized)
            {
                return;
            }
            _isInitialized = true;

            _client = new HttpClientWrapper();
            _orderIdToFillQuantity = new ConcurrentDictionary<int, decimal>();
            _orderProvider = orderProvider;
            _securityProvider = securityProvider;
            _symbolMapper = new TastyTradeSymbolMapper();

            // Handle authentication
            if (string.IsNullOrEmpty(sessionToken))
            {
                _sessionToken = AuthenticateWithCredentials(username, password).Result;
            }
            else
            {
                _sessionToken = sessionToken;
            }

            SetupCommonComponents();
        }

        private void Initialize(IOrderProvider orderProvider, ISecurityProvider securityProvider)
        {
            _client = new HttpClientWrapper();
            _orderIdToFillQuantity = new ConcurrentDictionary<int, decimal>();
            _orderProvider = orderProvider;
            _securityProvider = securityProvider;
            _symbolMapper = new TastyTradeSymbolMapper();

            // Authenticate and set up the session token
            _sessionToken = AuthenticateSession().Result;
            _client.DefaultRequestHeaders.Add("Authorization", _sessionToken);

            // Initialize message handler
            _messageHandler = new BrokerageConcurrentMessageHandler<ITastyTradeOrderUpdate>(HandleOrderUpdate);

            // Initialize subscriptions
            _subscriptionManager = new EventBasedDataQueueHandlerSubscriptionManager();
            _subscriptionManager.SubscribeImpl += (s, t) => Subscribe(s);
            _subscriptionManager.UnsubscribeImpl += (s, t) => Unsubscribe(s);

            // Initialize aggregator
            _aggregator = Composer.Instance.GetPart<IDataAggregator>();
            if (_aggregator == null)
            {
                var aggregatorName = Config.Get("data-aggregator", "QuantConnect.Lean.Engine.DataFeeds.AggregationManager");
                Log.Trace($"TastyTradeBrokerage(): Creating {aggregatorName}");
                _aggregator = Composer.Instance.GetExportedValueByTypeName<IDataAggregator>(aggregatorName);
            }
        }

        private void SetupCommonComponents()
        {
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

        private async Task SwitchToPaperTrading()
        {
            try
            {
                // First, get available accounts
                Log.Trace("TastyTradeBrokerage.SwitchToPaperTrading(): Getting customer accounts...");
                var accountsResponse = await _client.GetAsync($"{_baseUrl}/customers/me/accounts");
                var accountsContent = await accountsResponse.Content.ReadAsStringAsync();

                if (!accountsResponse.IsSuccessStatusCode)
                {
                    throw new BrokerageException($"Error getting accounts: {accountsContent}");
                }

                Log.Trace($"TastyTradeBrokerage.SwitchToPaperTrading(): Accounts response: {accountsContent}");

                var accounts = JObject.Parse(accountsContent);
                var demoAccount = accounts["data"]?["items"]?
                    .FirstOrDefault(a => a["account-type-name"]?.ToString() == "Individual" &&
                                       a["is-test-drive"]?.Value<bool>() == true);

                if (demoAccount == null)
                {
                    throw new BrokerageException("No paper trading account found");
                }

                var accountNumber = demoAccount["account-number"].ToString();
                Log.Trace($"TastyTradeBrokerage.SwitchToPaperTrading(): Found paper trading account: {accountNumber}");

                // Create request to switch account
                var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/sessions/switch-account")
                {
                    Content = new StringContent(
                        JsonConvert.SerializeObject(new
                        {
                            account_number = accountNumber
                        }),
                        Encoding.UTF8,
                        "application/json")
                };

                Log.Trace("TastyTradeBrokerage.SwitchToPaperTrading(): Switching to paper trading account...");
                var response = await _client.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    throw new BrokerageException($"Error switching to paper trading account: {response.StatusCode} {content}");
                }

                Log.Trace($"TastyTradeBrokerage.SwitchToPaperTrading(): Successfully switched to paper trading account. Response: {content}");
            }
            catch (Exception e)
            {
                var message = $"Error switching to paper trading account: {e.Message}";
                Log.Error($"TastyTradeBrokerage.SwitchToPaperTrading(): {message}");
                throw new BrokerageException(message, e);
            }
        }

        #endregion

        #region Sesion Management

        private async Task<HttpResponseMessage> AuthenticateWithOAuth()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/oauth/token")
            {
                Content = new FormUrlEncodedContent(new[]
                {
                new KeyValuePair<string, string>("client_id", _clientId),
                new KeyValuePair<string, string>("client_secret", _clientSecret),
                new KeyValuePair<string, string>("grant_type", "client_credentials")
            })
            };

            return await _client.SendAsync(request);
        }

        private async Task<HttpResponseMessage> AuthenticateWithCredentials()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/sessions")
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(new
                    {
                        login = _username,
                        password = _password,
                        remember_me = true
                    }),
                    Encoding.UTF8,
                    "application/json")
            };

            return await _client.SendAsync(request);
        }

        private async Task<string> AuthenticateWithCredentials(string username, string password)
        {
            try
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
            catch (Exception e)
            {
                Log.Error($"TastyTradeBrokerage.AuthenticateWithCredentials(): {e.Message}");
                throw;
            }
        }

        private async Task<string> AuthenticateSession()
        {
            try
            {
                if (!string.IsNullOrEmpty(_sessionToken) && _sessionExpiration > DateTime.UtcNow)
                {
                    Log.Trace("TastyTradeBrokerage.AuthenticateSession(): Using existing valid session token");
                    return _sessionToken;
                }

                _client.DefaultRequestHeaders.Clear();
                _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                // Construct authentication payload
                var authPayload = new
                {
                    login = _username,
                    password = _password,
                    remember_me = true
                };

                var jsonPayload = JsonConvert.SerializeObject(authPayload, Formatting.Indented);
                Log.Debug($"TastyTradeBrokerage.AuthenticateSession(): Authentication URL: {_baseUrl}/sessions");
                Log.Debug($"TastyTradeBrokerage.AuthenticateSession(): Authentication payload:\n{jsonPayload}");

                var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/sessions")
                {
                    Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
                };

                // Log request headers
                Log.Debug("TastyTradeBrokerage.AuthenticateSession(): Request Headers:");
                foreach (var header in request.Headers)
                {
                    Log.Debug($"    {header.Key}: {string.Join(", ", header.Value)}");
                }
                foreach (var header in request.Content.Headers)
                {
                    Log.Debug($"    {header.Key}: {string.Join(", ", header.Value)}");
                }

                var response = await _client.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();

                // Log response details
                Log.Debug($"TastyTradeBrokerage.AuthenticateSession(): Response Status: {response.StatusCode}");
                Log.Debug($"TastyTradeBrokerage.AuthenticateSession(): Response Headers:");
                foreach (var header in response.Headers)
                {
                    Log.Debug($"    {header.Key}: {string.Join(", ", header.Value)}");
                }
                Log.Debug($"TastyTradeBrokerage.AuthenticateSession(): Response Content:\n{content}");

                if (!response.IsSuccessStatusCode)
                {
                    throw new BrokerageException($"Error authenticating: {content}");
                }

                var result = JsonConvert.DeserializeObject<JObject>(content);
                var data = result["data"];

                _sessionToken = data["session-token"]?.ToString();
                _rememberToken = data["remember-token"]?.ToString();

                if (DateTime.TryParse(data["session-expiration"]?.ToString(), out var expiration))
                {
                    _sessionExpiration = expiration;
                    Log.Debug($"TastyTradeBrokerage.AuthenticateSession(): Session will expire at: {_sessionExpiration}");
                }
                else
                {
                    _sessionExpiration = DateTime.UtcNow.AddHours(24);
                    Log.Debug($"TastyTradeBrokerage.AuthenticateSession(): Using default expiration of 24 hours: {_sessionExpiration}");
                }

                if (string.IsNullOrEmpty(_sessionToken))
                {
                    throw new BrokerageException("Session token not found in response");
                }

                Log.Debug($"TastyTradeBrokerage.AuthenticateSession(): Successfully authenticated with token length: {_sessionToken.Length}");

                _client.DefaultRequestHeaders.Remove("Authorization");
                _client.DefaultRequestHeaders.Add("Authorization", _sessionToken);

                if (_paperTrading)
                {
                    await SwitchToPaperTrading();
                }

                return _sessionToken;
            }
            catch (Exception e)
            {
                var message = $"Error during authentication: {e.Message}";
                Log.Error($"TastyTradeBrokerage.AuthenticateSession(): {message}");
                throw new BrokerageException(message, e);
            }
        }

        private async Task EnsureValidSession()
        {
            if (string.IsNullOrEmpty(_sessionToken) || _sessionExpiration <= DateTime.UtcNow)
            {
                await AuthenticateSession();
            }
        }

        private async Task<HttpResponseMessage> SendAuthenticatedRequest(HttpRequestMessage request)
        {
            await EnsureValidSession();
            return await _client.SendAsync(request);
        }

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

        #endregion

        #region Orders

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

        public override async void Disconnect()
        {
            if (!_connected) return;

            try
            {
                // Destroy the session if we have a token
                if (!string.IsNullOrEmpty(_sessionToken))
                {
                    var request = new HttpRequestMessage(HttpMethod.Delete, $"{_baseUrl}/sessions")
                    {
                        Headers = { { "Authorization", _sessionToken } }
                    };

                    await _client.SendAsync(request);
                }

                _sessionToken = null;
                _rememberToken = null;
                _connected = false;

                // Cleanup WebSocket connections
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
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Disconnect, -1, "Disconnected from TastyTrade"));
            }
            catch (Exception e)
            {
                Log.Error($"TastyTradeBrokerage.Disconnect(): Error during disconnect: {e.Message}");
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

        #endregion
    }
}