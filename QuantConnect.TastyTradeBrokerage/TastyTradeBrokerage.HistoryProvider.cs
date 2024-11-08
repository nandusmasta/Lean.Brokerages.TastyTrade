using System;
using System.Linq;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;

namespace QuantConnect.Brokerages.TastyTrade
{
    public partial class TastyTradeBrokerage
    {
        private bool _unsupportedEquityTradeTickAndSecondResolution;
        private bool _unsupportedOpenInterestNonTickResolution;
        private bool _unsupportedOptionTickType;
        private bool _unsupportedCryptoTickType;
        private bool _unsupportedSecurityTypeWarningLogged;

        public override IEnumerable<BaseData> GetHistory(HistoryRequest request)
        {
            if (!CanSubscribe(request.Symbol))
            {
                if (!_unsupportedSecurityTypeWarningLogged)
                {
                    _unsupportedSecurityTypeWarningLogged = true;
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "UnsupportedSecurityType",
                    $"The security type '{request.Symbol.SecurityType}' of symbol '{request.Symbol}' is not supported for historical data retrieval."));
                }
                return null;
            }

            var brokerageSymbol = _symbolMapper.GetBrokerageSymbol(request.Symbol);

            IEnumerable<BaseData> data;
            switch (request.Symbol.SecurityType)
            {
                case SecurityType.Equity:
                    data = GetEquityHistory(request, brokerageSymbol);
                    break;
                case SecurityType.Option:
                    data = GetOptionHistory(request, brokerageSymbol);
                    break;
                case SecurityType.Crypto:
                    data = GetCryptoHistory(request, brokerageSymbol);
                    break;
                default:
                    return null;
            }

            if (data != null)
            {
                return data.Where(x => request.ExchangeHours.IsOpen(x.Time, x.EndTime, request.IncludeExtendedMarketHours));
            }
            return data;
        }

        private IEnumerable<BaseData> GetEquityHistory(HistoryRequest request, string brokerageSymbol)
        {
            if (request.TickType == TickType.Trade && request.Resolution is Resolution.Second or Resolution.Tick)
            {
                if (!_unsupportedEquityTradeTickAndSecondResolution)
                {
                    _unsupportedEquityTradeTickAndSecondResolution = true;
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "InvalidResolution",
                        $"The requested resolution '{request.Resolution}' is not supported for trade tick data. No historical data will be returned."));
                }
                return null;
            }

            return GetHistoricalData(request, brokerageSymbol, "equities");
        }

        private IEnumerable<BaseData> GetOptionHistory(HistoryRequest request, string brokerageSymbol)
        {
            if (request.TickType != TickType.Trade && !_unsupportedOptionTickType)
            {
                _unsupportedOptionTickType = true;
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "InvalidTickType",
                    $"The requested TickType '{request.TickType}' is not supported for option data. Only Trade type is supported."));
                return null;
            }

            return GetHistoricalData(request, brokerageSymbol, "option-chains");
        }

        private IEnumerable<BaseData> GetCryptoHistory(HistoryRequest request, string brokerageSymbol)
        {
            if (request.TickType == TickType.OpenInterest)
            {
                if (!_unsupportedCryptoTickType)
                {
                    _unsupportedCryptoTickType = true;
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "InvalidTickType",
                        $"The requested TickType '{request.TickType}' is not supported for crypto data."));
                }
                return null;
            }

            return GetHistoricalData(request, brokerageSymbol, "crypto");
        }

        private IEnumerable<BaseData> GetHistoricalData(HistoryRequest request, string brokerageSymbol, string endpoint)
        {
            var resolution = ConvertResolution(request.Resolution);
            var timeFrame = ConvertTimeFrame(request.Resolution);
            var startTime = request.StartTimeUtc.ToString("O");
            var endTime = request.EndTimeUtc.ToString("O");

            var url = $"{_baseUrl}/{endpoint}/history?symbol={brokerageSymbol}&resolution={resolution}&start-time={startTime}&end-time={endTime}&timeframe={timeFrame}";
            var response = _client.GetAsync(url).Result;
            var content = response.Content.ReadAsStringAsync().Result;

            if (!response.IsSuccessStatusCode)
            {
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "HistoryError",
                    $"Error getting history for {brokerageSymbol}: {content}"));
                return null;
            }

            var data = JsonConvert.DeserializeObject<JArray>(content);
            var result = new List<BaseData>();

            foreach (var item in data)
            {
                var time = item["time"].Value<DateTime>();
                switch (request.TickType)
                {
                    case TickType.Trade:
                        if (request.Resolution == Resolution.Tick)
                        {
                            result.Add(new Tick
                            {
                                Symbol = request.Symbol,
                                Time = time,
                                Value = item["price"].Value<decimal>(),
                                Quantity = item["size"].Value<decimal>(),
                                TickType = TickType.Trade
                            });
                        }
                        else
                        {
                            result.Add(new TradeBar
                            {
                                Symbol = request.Symbol,
                                Time = time,
                                Open = item["open"].Value<decimal>(),
                                High = item["high"].Value<decimal>(),
                                Low = item["low"].Value<decimal>(),
                                Close = item["close"].Value<decimal>(),
                                Volume = item["volume"].Value<decimal>(),
                                Period = request.Resolution.ToTimeSpan()
                            });
                        }
                        break;

                    case TickType.Quote:
                        result.Add(new QuoteBar
                        {
                            Symbol = request.Symbol,
                            Time = time,
                            Ask = new Bar
                            {
                                Open = item["ask-open"].Value<decimal>(),
                                High = item["ask-high"].Value<decimal>(),
                                Low = item["ask-low"].Value<decimal>(),
                                Close = item["ask-close"].Value<decimal>()
                            },
                            Bid = new Bar
                            {
                                Open = item["bid-open"].Value<decimal>(),
                                High = item["bid-high"].Value<decimal>(),
                                Low = item["bid-low"].Value<decimal>(),
                                Close = item["bid-close"].Value<decimal>()
                            },
                            Period = request.Resolution.ToTimeSpan()
                        });
                        break;
                }
            }

            return result;
        }

        private string ConvertResolution(Resolution resolution)
        {
            switch (resolution)
            {
                case Resolution.Tick:
                    return "tick";
                case Resolution.Second:
                    return "1sec";
                case Resolution.Minute:
                    return "1min";
                case Resolution.Hour:
                    return "1hour";
                case Resolution.Daily:
                    return "1day";
                default:
                    throw new ArgumentOutOfRangeException(nameof(resolution));
            }
        }

        private string ConvertTimeFrame(Resolution resolution)
        {
            switch (resolution)
            {
                case Resolution.Tick:
                case Resolution.Second:
                case Resolution.Minute:
                    return "minute";
                case Resolution.Hour:
                    return "hour";
                case Resolution.Daily:
                    return "day";
                default:
                    throw new ArgumentOutOfRangeException(nameof(resolution));
            }
        }
    }
}