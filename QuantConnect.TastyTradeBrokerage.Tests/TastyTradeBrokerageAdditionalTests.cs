using Moq;
using System;
using NUnit.Framework;
using QuantConnect.Util;
using QuantConnect.Tests;
using QuantConnect.Interfaces;
using System.Collections.Generic;
using QuantConnect.Securities;
using Alpaca.Markets;

namespace QuantConnect.Brokerages.TastyTrade.Tests
{
    [TestFixture]
    public class TastyTradeBrokerageAdditionalTests
    {
        private TestTastyTradeBrokerage _tastyTradeBrokerage;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var (username, password, sessionToken) = TastyTradeBrokerageTestHelpers.GetConfigParameters();

            var algorithmMock = new Mock<IAlgorithm>();
            _tastyTradeBrokerage = new TestTastyTradeBrokerage(username, password, sessionToken, algorithmMock.Object);
        }

        [Test]
        public void ParameterlessConstructorComposerUsage()
        {
            var brokerage = Composer.Instance.GetExportedValueByTypeName<IDataQueueHandler>("TastyTradeBrokerage");
            Assert.IsNotNull(brokerage);
        }

        private static IEnumerable<Symbol> QuoteSymbolParameters
        {
            get
            {
                TestGlobals.Initialize();
                yield return Symbols.AAPL;
                yield return Symbol.CreateOption(Symbols.AAPL, Symbols.AAPL.ID.Market, OptionStyle.American, OptionRight.Call, 230, new DateTime(2024, 12, 20));
            }
        }

        [Test, TestCaseSource(nameof(QuoteSymbolParameters))]
        public void GetLatestQuote(Symbol symbol)
        {
            var quote = _tastyTradeBrokerage.GetLatestQuotePublic(symbol);

            Assert.IsNotNull(quote);
            Assert.Greater(quote.AskSize, 0);
            Assert.Greater(quote.AskPrice, 0);
            Assert.Greater(quote.BidSize, 0);
            Assert.Greater(quote.BidPrice, 0);
        }
    }

    public class TestTastyTradeBrokerage : TastyTradeBrokerage
    {
        public TestTastyTradeBrokerage(string username, string password, string sessionToken, IAlgorithm algorithm)
            : base(username, password, sessionToken, algorithm)
        {
        }

        public TestTastyTradeBrokerage(string username, string password, string sessionToken, IOrderProvider orderProvider, ISecurityProvider securityProvider)
            : base(username, password, sessionToken, orderProvider, securityProvider)
        {
        }

        public IQuote GetLatestQuotePublic(Symbol symbol)
        {
            return GetLatestQuote(symbol);
        }
    }
}