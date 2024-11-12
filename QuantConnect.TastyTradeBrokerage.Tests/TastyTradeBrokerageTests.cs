using Moq;
using NUnit.Framework;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Tests;
using QuantConnect.Tests.Brokerages;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using QuantConnect.Configuration;
using static QuantConnect.Brokerages.TastyTrade.Tests.TastyTradeBrokerageAdditionalTests;

namespace QuantConnect.Brokerages.TastyTrade.Tests
{
    [TestFixture]
    public partial class TastyTradeBrokerageTests : BrokerageTests
    {
        protected override Symbol Symbol { get; } = Symbols.AAPL;
        protected override SecurityType SecurityType { get; }

        protected override BrokerageName BrokerageName => BrokerageName.TastyTrade;

        protected override IBrokerage CreateBrokerage(IOrderProvider orderProvider, ISecurityProvider securityProvider)
        {
            var (username, password, sessionToken) = TastyTradeBrokerageTestHelpers.GetConfigParameters();

            return new TestTastyTradeBrokerage(username, password, sessionToken, orderProvider, securityProvider);
        }

        protected override bool IsAsync() => false;

        protected override decimal GetAskPrice(Symbol symbol)
        {
            return (Brokerage as TestTastyTradeBrokerage).GetLatestQuotePublic(symbol).AskPrice;
        }

        private static IEnumerable<TestCaseData> EquityOrderParameters
        {
            get
            {
                var EPU = Symbol.Create("AAPL", SecurityType.Equity, Market.USA);
                yield return new TestCaseData(new MarketOrderTestParameters(EPU));
                yield return new TestCaseData(new LimitOrderTestParameters(EPU, 250m, 200m));
                yield return new TestCaseData(new StopMarketOrderTestParameters(EPU, 250m, 200m));
                yield return new TestCaseData(new StopLimitOrderTestParameters(EPU, 250m, 200m));
            }
        }

        private static IEnumerable<TestCaseData> OptionOrderParameters
        {
            get
            {
                var option = Symbol.CreateOption(Symbols.AAPL, Symbols.AAPL.ID.Market, OptionStyle.American, OptionRight.Call, 230, new DateTime(2024, 12, 20));
                yield return new TestCaseData(new MarketOrderTestParameters(option));
                yield return new TestCaseData(new LimitOrderTestParameters(option, 20m, 10m));
                yield return new TestCaseData(new StopMarketOrderTestParameters(option, 20m, 10m));
                yield return new TestCaseData(new StopLimitOrderTestParameters(option, 20m, 10m));
            }
        }

        private static IEnumerable<TestCaseData> FutureOrderParameters
        {
            get
            {
                var future = Symbol.CreateFuture("ES", Market.USA, new DateTime(2024, 6, 21));
                yield return new TestCaseData(new MarketOrderTestParameters(future));
                yield return new TestCaseData(new LimitOrderTestParameters(future, 5000m, 4800m));
                yield return new TestCaseData(new StopMarketOrderTestParameters(future, 5000m, 4800m));
                yield return new TestCaseData(new StopLimitOrderTestParameters(future, 5000m, 4800m));
            }
        }

        [Test, TestCaseSource(nameof(EquityOrderParameters))]
        public override void CancelOrders(OrderTestParameters parameters)
        {
            base.CancelOrders(parameters);
        }

        [Test, TestCaseSource(nameof(OptionOrderParameters))]
        public void CancelOrdersOption(OrderTestParameters parameters)
        {
            base.CancelOrders(parameters);
        }

        [Test, TestCaseSource(nameof(FutureOrderParameters))]
        public void CancelOrdersFutures(OrderTestParameters parameters)
        {
            base.CancelOrders(parameters);
        }

        [Test, TestCaseSource(nameof(EquityOrderParameters))]
        public override void LongFromZero(OrderTestParameters parameters)
        {
            base.LongFromZero(parameters);
        }

        [Test, TestCaseSource(nameof(OptionOrderParameters))]
        public void LongFromZeroOption(OrderTestParameters parameters)
        {
            base.LongFromZero(parameters);
        }

        [Test, TestCaseSource(nameof(FutureOrderParameters))]
        public void LongFromZeroFuture(OrderTestParameters parameters)
        {
            base.LongFromZero(parameters);
        }

        [Test, TestCaseSource(nameof(EquityOrderParameters))]
        public override void CloseFromLong(OrderTestParameters parameters)
        {
            base.CloseFromLong(parameters);
        }

        [Test, TestCaseSource(nameof(OptionOrderParameters))]
        public void CloseFromLongOption(OrderTestParameters parameters)
        {
            base.CloseFromLong(parameters);
        }

        [Test, TestCaseSource(nameof(FutureOrderParameters))]
        public void CloseFromLongFuture(OrderTestParameters parameters)
        {
            base.CloseFromLong(parameters);
        }

        [Test, TestCaseSource(nameof(EquityOrderParameters))]
        public override void ShortFromZero(OrderTestParameters parameters)
        {
            base.ShortFromZero(parameters);
        }

        [Test, TestCaseSource(nameof(EquityOrderParameters))]
        public override void CloseFromShort(OrderTestParameters parameters)
        {
            base.CloseFromShort(parameters);
        }

        [Test, TestCaseSource(nameof(EquityOrderParameters))]
        public override void ShortFromLong(OrderTestParameters parameters)
        {
            base.ShortFromLong(parameters);
        }

        [Test, TestCaseSource(nameof(EquityOrderParameters))]
        public override void LongFromShort(OrderTestParameters parameters)
        {
            base.LongFromShort(parameters);
        }

        [Test]
        public void UpdateNotExistOrder()
        {
            var limitOrder = new LimitOrder(Symbol, 1, 2000m, DateTime.UtcNow);
            limitOrder.BrokerId.Add(Guid.NewGuid().ToString());
            Assert.IsFalse(Brokerage.UpdateOrder(limitOrder));
        }

        [Test]
        public void LookupSymbols()
        {
            var option = Symbol.CreateCanonicalOption(Symbols.AAPL);

            var options = (Brokerage as IDataQueueUniverseProvider).LookupSymbols(option, false).ToList();
            Assert.IsNotNull(options);
            Assert.True(options.Any());
            Assert.Greater(options.Count, 0);
            Assert.That(options.Distinct().ToList().Count, Is.EqualTo(options.Count));
        }

        private static IEnumerable<TestCaseData> MarketOpenCloseOrderTypeParameters
        {
            get
            {
                var symbol = Symbols.AAPL;
                yield return new TestCaseData(new MarketOnOpenOrder(symbol, 1m, DateTime.UtcNow), !symbol.IsMarketOpen(DateTime.UtcNow, false));
                yield return new TestCaseData(new MarketOnCloseOrder(symbol, 1m, DateTime.UtcNow), symbol.IsMarketOpen(DateTime.UtcNow, false));
            }
        }

        [TestCaseSource(nameof(MarketOpenCloseOrderTypeParameters))]
        public void PlaceMarketOpenCloseOrder(Order order, bool marketIsOpen)
        {
            Log.Trace($"PLACE {order.Type} ORDER TEST");

            var submittedResetEvent = new AutoResetEvent(false);
            var invalidResetEvent = new AutoResetEvent(false);

            OrderProvider.Add(order);

            Brokerage.OrdersStatusChanged += (_, orderEvents) =>
            {
                var orderEvent = orderEvents[0];

                Log.Trace("");
                Log.Trace($"{nameof(PlaceMarketOpenCloseOrder)}.OrderEvent.Status: {orderEvent.Status}");
                Log.Trace("");

                if (orderEvent.Status == OrderStatus.Submitted)
                {
                    submittedResetEvent.Set();
                }
                else if (orderEvent.Status == OrderStatus.Invalid)
                {
                    invalidResetEvent.Set();
                }
            };

            if (marketIsOpen)
            {
                Assert.IsTrue(Brokerage.PlaceOrder(order));

                if (!submittedResetEvent.WaitOne(TimeSpan.FromSeconds(5)))
                {
                    Assert.Fail($"{nameof(PlaceMarketOpenCloseOrder)}: the brokerage doesn't return {OrderStatus.Submitted}");
                }

                var openOrders = Brokerage.GetOpenOrders();

                Assert.IsNotEmpty(openOrders);
                Assert.That(openOrders.Count, Is.EqualTo(1));
                Assert.That(openOrders[0].Type, Is.EqualTo(order.Type));
                Assert.IsTrue(Brokerage.CancelOrder(order));
            }
            else
            {
                Assert.IsFalse(Brokerage.PlaceOrder(order));

                if (!invalidResetEvent.WaitOne(TimeSpan.FromSeconds(5)))
                {
                    Assert.Fail($"{nameof(PlaceMarketOpenCloseOrder)}: the brokerage doesn't return {OrderStatus.Invalid}");
                }
            }
        }
    }
}