using System;
using System.Collections.Generic;
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Orders.Fees;
using QuantConnect.Orders.Fills;
using QuantConnect.Orders.Slippage;

namespace QuantConnect.Brokerages.TastyTrade
{
    public class TastyTradeBrokerageModel : DefaultBrokerageModel
    {
        private const decimal _defaultLeverage = 1m;

        public TastyTradeBrokerageModel() : base()
        {
        }

        public override bool CanSubmitOrder(Security security, Order order, out BrokerageMessageEvent message)
        {
            message = new BrokerageMessageEvent(BrokerageMessageType.Information, 1, "Valid Order");

            // Validate security type
            if (!IsSecurityTypeSupported(security.Type))
            {
                var messageString = $"Unsupported security type: {security.Type}";
                message = new BrokerageMessageEvent(BrokerageMessageType.Error, 0, messageString);
                return false;
            }

            // Validate order type
            if (!IsValidOrderType(security.Type, order.Type))
            {
                var messageString = $"Invalid order type: {order.Type}";
                message = new BrokerageMessageEvent(BrokerageMessageType.Error, 0, messageString);
                return false;
            }

            // Check for margin requirements if buying
            if (order.Direction == OrderDirection.Buy && security.Holdings.Quantity + order.Quantity > 0)
            {
                var marginRequired = security.MarginModel.GetInitialMarginRequirement(security, order.Quantity);
                var availableCash = security.QuoteCurrency.Amount;
                if (marginRequired > availableCash)
                {
                    var messageString = "Insufficient margin to place buy order";
                    message = new BrokerageMessageEvent(BrokerageMessageType.Error, 0, messageString);
                    return false;
                }
            }

            return true;
        }

        public override IBuyingPowerModel GetBuyingPowerModel(Security security)
        {
            switch (security.Type)
            {
                case SecurityType.Equity:
                    return new SecurityMarginModel(_defaultLeverage);
                case SecurityType.Option:
                    return new SecurityMarginModel(_defaultLeverage);
                case SecurityType.Future:
                    return new SecurityMarginModel(_defaultLeverage);
                case SecurityType.FutureOption:
                    return new SecurityMarginModel(_defaultLeverage);
                default:
                    return new SecurityMarginModel(_defaultLeverage);
            }
        }

        public override IFillModel GetFillModel(Security security)
        {
            return new ImmediateFillModel();
        }

        public override ISlippageModel GetSlippageModel(Security security)
        {
            return new ConstantSlippageModel(0);
        }

        public override IFeeModel GetFeeModel(Security security)
        {
            switch (security.Type)
            {
                case SecurityType.Equity:
                    return new TastyTradeEquityFeeModel();
                case SecurityType.Option:
                    return new TastyTradeOptionFeeModel();
                case SecurityType.Future:
                    return new TastyTradeFuturesFeeModel();
                case SecurityType.FutureOption:
                    return new TastyTradeFuturesOptionFeeModel();
                default:
                    return new ConstantFeeModel(0);
            }
        }

        private bool IsSecurityTypeSupported(SecurityType securityType)
        {
            return securityType == SecurityType.Equity ||
                   securityType == SecurityType.Option ||
                   securityType == SecurityType.Future ||
                   securityType == SecurityType.FutureOption;
        }

        private bool IsValidOrderType(SecurityType securityType, OrderType orderType)
        {
            return orderType switch
            {
                OrderType.Market => true,
                OrderType.Limit => true,
                OrderType.StopMarket => true,
                OrderType.StopLimit => true,
                OrderType.MarketOnOpen => securityType == SecurityType.Equity,
                OrderType.MarketOnClose => securityType == SecurityType.Equity,
                _ => false
            };
        }

        public override bool CanUpdateOrder(Security security, Order order, UpdateOrderRequest request, out BrokerageMessageEvent message)
        {
            message = new BrokerageMessageEvent(BrokerageMessageType.Information, 1, "Valid Update Order");

            if (order.Status == OrderStatus.Filled)
            {
                var messageString = "Cannot update a filled order";
                message = new BrokerageMessageEvent(BrokerageMessageType.Error, 0, messageString);
                return false;
            }

            if (request.Quantity != null && request.Quantity <= 0)
            {
                var messageString = "Cannot update quantity to less than or equal to zero";
                message = new BrokerageMessageEvent(BrokerageMessageType.Error, 0, messageString);
                return false;
            }

            return true;
        }
    }

    public class TastyTradeEquityFeeModel : FeeModel
    {
        public override OrderFee GetOrderFee(OrderFeeParameters parameters)
        {
            // $1 per contract minimum, $0.65 per contract after 10 contracts
            var quantity = Math.Abs(parameters.Order.Quantity);
            var feePerShare = quantity <= 10 ? 1.00m : 0.65m;
            var fee = Math.Max(1.00m, quantity * feePerShare);
            return new OrderFee(new CashAmount(fee, "USD"));
        }
    }

    public class TastyTradeOptionFeeModel : FeeModel
    {
        public override OrderFee GetOrderFee(OrderFeeParameters parameters)
        {
            // $1 per contract to open, $0 to close
            var fee = parameters.Order.Direction == OrderDirection.Buy ?
                Math.Abs(parameters.Order.Quantity) * 1.00m : 0m;
            return new OrderFee(new CashAmount(fee, "USD"));
        }
    }

    public class TastyTradeFuturesFeeModel : FeeModel
    {
        public override OrderFee GetOrderFee(OrderFeeParameters parameters)
        {
            // $2.50 per contract
            var fee = Math.Abs(parameters.Order.Quantity) * 2.50m;
            return new OrderFee(new CashAmount(fee, "USD"));
        }
    }

    public class TastyTradeFuturesOptionFeeModel : FeeModel
    {
        public override OrderFee GetOrderFee(OrderFeeParameters parameters)
        {
            // $2.50 per contract
            var fee = Math.Abs(parameters.Order.Quantity) * 2.50m;
            return new OrderFee(new CashAmount(fee, "USD"));
        }
    }
}