using Newtonsoft.Json;
using QuantConnect.Orders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QuantConnect.Brokerages.TastyTrade
{
    // Nested classes for the response structure
    public class TastyTradeOrderResponse
    {
        [JsonProperty("data")]
        public TastyTradeResponseData Data { get; set; }
    }

    public class TastyTradeResponseData
    {
        [JsonProperty("items")]
        public List<TastyTradeOrderDto> Items { get; set; }
    }

    public class TastyTradeOrderDto
    {
        [JsonProperty("instrument-type")]
        public string InstrumentType { get; set; }

        [JsonProperty("symbol")]
        public string Symbol { get; set; }

        [JsonProperty("order-side")]
        public string OrderSide { get; set; }

        [JsonProperty("quantity")]
        public decimal Quantity { get; set; }

        [JsonProperty("order-type")]
        public string OrderType { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("received-at")]
        public DateTime ReceivedAt { get; set; }

        [JsonProperty("limit-price")]
        public decimal? LimitPrice { get; set; }

        [JsonProperty("stop-price")]
        public decimal? StopPrice { get; set; }

        // Additional properties for options
        [JsonProperty("expiration-date")]
        public DateTime? ExpirationDate { get; set; }

        [JsonProperty("strike-price")]
        public decimal? StrikePrice { get; set; }

        [JsonProperty("option-type")]
        public string OptionType { get; set; }  // "C" for Call, "P" for Put
    }

    public static class TastyTradeOrderExtensions
    {
        public static Order ToLeanOrder(this TastyTradeOrderDto dto, ITastyTradeSymbolMapper symbolMapper)
        {
            var securityType = ConvertInstrumentType(dto.InstrumentType);
            var market = Market.USA;  // TastyTrade only supports US market

            Symbol leanSymbol;
            if (securityType == SecurityType.Option)
            {
                var optionRight = dto.OptionType == "C" ? OptionRight.Call : OptionRight.Put;
                var underlying = Symbol.Create(dto.Symbol, SecurityType.Equity, market);
                leanSymbol = Symbol.CreateOption(
                    underlying,
                    market,
                    OptionStyle.American,
                    optionRight,
                    dto.StrikePrice ?? 0,
                    dto.ExpirationDate ?? DateTime.Today);
            }
            else
            {
                leanSymbol = Symbol.Create(dto.Symbol, securityType, market);
            }

            var quantity = dto.OrderSide == "Buy" ? dto.Quantity : -dto.Quantity;

            Order order = dto.OrderType switch
            {
                "Market" => new MarketOrder(leanSymbol, quantity, dto.ReceivedAt),
                "Limit" => new LimitOrder(leanSymbol, quantity, dto.LimitPrice ?? 0, dto.ReceivedAt),
                "Stop" => new StopMarketOrder(leanSymbol, quantity, dto.StopPrice ?? 0, dto.ReceivedAt),
                "StopLimit" => new StopLimitOrder(leanSymbol, quantity, dto.StopPrice ?? 0, dto.LimitPrice ?? 0, dto.ReceivedAt),
                _ => null
            };

            if (order != null)
            {
                order.Status = ConvertOrderStatus(dto.Status);
            }

            return order;
        }

        private static SecurityType ConvertInstrumentType(string instrumentType) =>
            instrumentType?.ToLowerInvariant() switch
            {
                "equity" => SecurityType.Equity,
                "equity option" => SecurityType.Option,
                "future" => SecurityType.Future,
                "future option" => SecurityType.FutureOption,
                _ => SecurityType.Base
            };

        private static OrderStatus ConvertOrderStatus(string tastyTradeStatus) =>
            tastyTradeStatus?.ToLowerInvariant() switch
            {
                "received" => OrderStatus.Submitted,
                "cancelled" => OrderStatus.Canceled,
                "filled" => OrderStatus.Filled,
                "partially_filled" => OrderStatus.PartiallyFilled,
                "rejected" => OrderStatus.Invalid,
                _ => OrderStatus.None
            };        
    }
}
