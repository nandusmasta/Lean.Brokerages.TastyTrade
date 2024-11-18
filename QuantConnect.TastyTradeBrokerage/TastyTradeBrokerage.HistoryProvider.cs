using System;
using System.Linq;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using QuantConnect.Logging;

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
            JArray data;
            try
            {
                var brokerageSymbol = _symbolMapper.GetBrokerageSymbol(request.Symbol);
                var resolution = ConvertResolution(request.Resolution);
                var timeFrame = ConvertTimeFrame(request.Resolution);
                var startTime = request.StartTimeUtc.ToString("O");
                var endTime = request.EndTimeUtc.ToString("O");

                var url = $"{_baseUrl}/{GetEndpoint(request.Symbol.SecurityType)}/history" +
                         $"?symbol={brokerageSymbol}" +
                         $"&resolution={resolution}" +
                         $"&start-time={startTime}" +
                         $"&end-time={endTime}" +
                         $"&timeframe={timeFrame}";

                var response = _client.GetAsync(url).Result;
                var content = response.Content.ReadAsStringAsync().Result;

                if (!response.IsSuccessStatusCode)
                {
                    throw new Exception($"Request failed: {response.StatusCode}, Content: {content}");
                }

                data = JsonConvert.DeserializeObject<JArray>(content);
            }
            catch (Exception e)
            {
                Log.Error($"TastyTradeBrokerage.GetHistory(): Error getting history for {request.Symbol}: {e.Message}");
                return Enumerable.Empty<BaseData>();
            }

            return ProcessHistoryData(data, request);
        }

        private IEnumerable<BaseData> ProcessHistoryData(JArray data, HistoryRequest request)
        {
            foreach (var item in data)
            {
                var time = item["time"].Value<DateTime>();

                switch (request.TickType)
                {
                    case TickType.Trade:
                        if (request.Resolution == Resolution.Tick)
                        {
                            yield return new Tick
                            {
                                Symbol = request.Symbol,
                                Time = time,
                                Value = item["price"].Value<decimal>(),
                                Quantity = item["size"].Value<decimal>(),
                                TickType = TickType.Trade
                            };
                        }
                        else
                        {
                            yield return new TradeBar
                            {
                                Symbol = request.Symbol,
                                Time = time,
                                Open = item["open"].Value<decimal>(),
                                High = item["high"].Value<decimal>(),
                                Low = item["low"].Value<decimal>(),
                                Close = item["close"].Value<decimal>(),
                                Volume = item["volume"].Value<decimal>(),
                                Period = request.Resolution.ToTimeSpan()
                            };
                        }
                        break;

                    case TickType.Quote:
                        if (request.Resolution == Resolution.Tick)
                        {
                            yield return new Tick
                            {
                                Symbol = request.Symbol,
                                Time = time,
                                BidPrice = item["bid"].Value<decimal>(),
                                BidSize = item["bidSize"].Value<decimal>(),
                                AskPrice = item["ask"].Value<decimal>(),
                                AskSize = item["askSize"].Value<decimal>(),
                                TickType = TickType.Quote
                            };
                        }
                        else
                        {
                            yield return new QuoteBar
                            {
                                Symbol = request.Symbol,
                                Time = time,
                                Bid = new Bar
                                {
                                    Open = item["bidOpen"].Value<decimal>(),
                                    High = item["bidHigh"].Value<decimal>(),
                                    Low = item["bidLow"].Value<decimal>(),
                                    Close = item["bidClose"].Value<decimal>()
                                },
                                Ask = new Bar
                                {
                                    Open = item["askOpen"].Value<decimal>(),
                                    High = item["askHigh"].Value<decimal>(),
                                    Low = item["askLow"].Value<decimal>(),
                                    Close = item["askClose"].Value<decimal>()
                                },
                                Period = request.Resolution.ToTimeSpan()
                            };
                        }
                        break;
                }
            }
        }

        private string GetEndpoint(SecurityType securityType)
        {
            return securityType switch
            {
                SecurityType.Equity => "equities",
                SecurityType.Option => "option-chains",
                SecurityType.Future => "futures",
                SecurityType.FutureOption => "futures-options",
                _ => throw new NotSupportedException($"SecurityType {securityType} not supported")
            };
        }

        private string ConvertResolution(Resolution resolution)
        {
            return resolution switch
            {
                Resolution.Tick => "tick",
                Resolution.Second => "1sec",
                Resolution.Minute => "1min",
                Resolution.Hour => "1hour",
                Resolution.Daily => "1day",
                _ => throw new ArgumentOutOfRangeException(nameof(resolution))
            };
        }

        private string ConvertTimeFrame(Resolution resolution)
        {
            return resolution switch
            {
                Resolution.Tick => "tick",
                Resolution.Second => "second",
                Resolution.Minute => "minute",
                Resolution.Hour => "hour",
                Resolution.Daily => "day",
                _ => throw new ArgumentOutOfRangeException(nameof(resolution))
            };
        }
    }
}