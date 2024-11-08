using System;
using QuantConnect.Orders;
using QuantConnect.Logging;
using QuantConnect.Orders.TimeInForces;

namespace QuantConnect.Brokerages.TastyTrade
{
    public static class TastyTradeBrokerageExtensions
    {
        public static object CreateOrderRequest(this Order order, decimal targetQuantity, ITastyTradeSymbolMapper symbolMapper, OrderType orderType)
        {
            var brokerageSymbol = symbolMapper.GetBrokerageSymbol(order.Symbol);
            var instrumentType = symbolMapper.GetBrokerageSecurityType(order.Symbol);
            var timeInForce = ConvertTimeInForce(order);
            var orderObj = CreateOrderRequestObject(order, targetQuantity, brokerageSymbol, instrumentType, timeInForce);

            return orderObj;
        }

        private static object CreateOrderRequestObject(Order order, decimal targetQuantity, string brokerageSymbol, string instrumentType, string timeInForce)
        {
            return new
            {
                order_type = ConvertOrderType(order),
                time_in_force = timeInForce,
                price = GetOrderPrice(order),
                price_effect = order.Direction == OrderDirection.Buy ? "Debit" : "Credit",
                legs = new[]
                {
                    new
                    {
                        instrument_type = instrumentType,
                        symbol = brokerageSymbol,
                        action = order.Direction == OrderDirection.Buy ? "Buy" : "Sell",
                        quantity = Math.Abs(targetQuantity)
                    }
                }
            };
        }

        public static bool TryGetLeanTimeInForceByTastyTradeTimeInForce(this OrderProperties orderProperties, string timeInForce)
        {
            switch (timeInForce.ToLowerInvariant())
            {
                case "day":
                    orderProperties.TimeInForce = TimeInForce.Day;
                    return true;
                case "gtc":
                    orderProperties.TimeInForce = TimeInForce.GoodTilCanceled;
                    return true;
                case "ext":
                    orderProperties.TimeInForce = TimeInForce.GoodTilCanceled;
                    return true;
                default:
                    return false;
            }
        }

        private static string ConvertOrderType(Order order)
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

        private static string ConvertTimeInForce(Order order)
        {
            if (order.SecurityType == SecurityType.Option && order.TimeInForce is not DayTimeInForce)
            {
                Log.Error($"Invalid TimeInForce '{order.TimeInForce.GetType().Name}' for Option security type. Only 'DayTimeInForce' is supported for options.");
                return "Day";
            }

            if (order.Type == OrderType.MarketOnOpen)
            {
                return "Opg";
            }
            if (order.Type == OrderType.MarketOnClose)
            {
                return "Cls";
            }

            return order.TimeInForce switch
            {
                DayTimeInForce => "Day",
                GoodTilCanceledTimeInForce => "GTC",
                _ => throw new NotSupportedException($"TimeInForce type '{order.TimeInForce.GetType().Name}' is not supported.")
            };
        }

        private static decimal? GetOrderPrice(Order order)
        {
            return order switch
            {
                LimitOrder limitOrder => limitOrder.LimitPrice,
                StopMarketOrder stopOrder => stopOrder.StopPrice,
                StopLimitOrder stopLimitOrder => stopLimitOrder.LimitPrice,
                _ => null
            };
        }

        public static string ConvertResolution(this Resolution resolution)
        {
            return resolution switch
            {
                Resolution.Minute => "1min",
                Resolution.Hour => "1hour",
                Resolution.Daily => "1day",
                _ => throw new NotImplementedException($"Resolution '{resolution}' is not supported. Please use Minute, Hour, or Daily resolution.")
            };
        }

        public static string ConvertTimeFrame(this Resolution resolution)
        {
            return resolution switch
            {
                Resolution.Tick => "tick",
                Resolution.Second => "1sec",
                Resolution.Minute => "1min",
                Resolution.Hour => "1hour",
                Resolution.Daily => "1day",
                _ => throw new NotImplementedException($"Resolution '{resolution}' is not supported.")
            };
        }
    }
}