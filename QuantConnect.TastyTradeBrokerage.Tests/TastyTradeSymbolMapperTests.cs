using System;
using NUnit.Framework;
using QuantConnect.Brokerages.TastyTrade;

namespace QuantConnect.Brokerages.TastyTrade.Tests
{
    [TestFixture]
    public class TastyTradeSymbolMapperTests
    {
        private TastyTradeSymbolMapper _symbolMapper;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var (username, password, sessionToken) = TastyTradeBrokerageTestHelpers.GetConfigParameters();

            _symbolMapper = new TastyTradeSymbolMapper();
        }

        [TestCase("Equity", "AAPL", "AAPL", "2024/06/14", OptionRight.Call, 100)]
        [TestCase("Equity Option", "AAPL240614P00100000", "AAPL", "2024/06/14", OptionRight.Put, 100)]
        [TestCase("Equity Option", "AAPL240614C00235000", "AAPL", "2024/06/14", OptionRight.Call, 235)]
        [TestCase("Equity Option", "QQQ240613C00484000", "QQQ", "2024/06/13", OptionRight.Call, 484)]
        [TestCase("Future", "/ESM4", "ES", "2024/06/21", OptionRight.Call, 0)]
        [TestCase("Future Option", "./ESM4 EW4M4 240614C4800", "ES", "2024/06/14", OptionRight.Call, 4800)]
        public void ReturnsCorrectLeanSymbol(string brokerageAssetClass, string brokerageTicker, string expectedSymbol,
            DateTime expectedDateTime, OptionRight optionRight, decimal expectedStrike)
        {
            var leanSymbol = _symbolMapper.GetLeanSymbol(brokerageAssetClass, brokerageTicker);

            Assert.IsNotNull(leanSymbol);

            if (brokerageAssetClass == "Equity Option" || brokerageAssetClass == "Future Option")
            {
                Assert.That(leanSymbol.ID.Date, Is.EqualTo(expectedDateTime));
                Assert.That(leanSymbol.ID.OptionRight, Is.EqualTo(optionRight));
                Assert.That(leanSymbol.ID.StrikePrice, Is.EqualTo(expectedStrike));
            }
            else if (brokerageAssetClass == "Future")
            {
                Assert.That(leanSymbol.ID.Date, Is.EqualTo(expectedDateTime));
            }

            Assert.That(leanSymbol.ID.Symbol, Is.EqualTo(expectedSymbol));
        }

        [TestCase("AAPL", SecurityType.Equity, null, null, null, "AAPL")]
        [TestCase("INTL", SecurityType.Equity, null, null, null, "INTL")]
        [TestCase("AAPL", SecurityType.Option, OptionRight.Call, 100, "2024/06/14", "AAPL240614C00100000")]
        [TestCase("AAPL", SecurityType.Option, OptionRight.Call, 105, "2024/06/14", "AAPL240614C00105000")]
        [TestCase("AAPL", SecurityType.Option, OptionRight.Put, 265, "2024/06/14", "AAPL240614P00265000")]
        [TestCase("ES", SecurityType.Future, null, null, "2024/06/21", "/ESM4")]
        [TestCase("ES", SecurityType.FutureOption, OptionRight.Call, 4800, "2024/06/14", "./ESM4 EW4M4 240614C4800")]
        public void ReturnsCorrectBrokerageSymbol(string symbol, SecurityType securityType, OptionRight? optionRight,
            decimal? strike, DateTime? expiryDate, string expectedBrokerageSymbol)
        {
            var leanSymbol = GenerateLeanSymbol(symbol, securityType, optionRight, strike, expiryDate);
            var brokerageSymbol = _symbolMapper.GetBrokerageSymbol(leanSymbol);
            Assert.That(brokerageSymbol, Is.EqualTo(expectedBrokerageSymbol));
        }

        private Symbol GenerateLeanSymbol(string symbol, SecurityType securityType, OptionRight? optionRight = OptionRight.Call,
            decimal? strike = 0m, DateTime? expiryDate = default, OptionStyle? optionStyle = OptionStyle.American)
        {
            switch (securityType)
            {
                case SecurityType.Equity:
                    return Symbol.Create(symbol, SecurityType.Equity, Market.USA);
                case SecurityType.Option:
                    var underlying = Symbol.Create(symbol, SecurityType.Equity, Market.USA);
                    return Symbol.CreateOption(underlying, Market.USA, optionStyle.Value, optionRight.Value, strike.Value, expiryDate.Value);
                case SecurityType.Future:
                    return Symbol.CreateFuture(symbol, Market.USA, expiryDate.Value);
                case SecurityType.FutureOption:
                    var futureUnderlying = Symbol.CreateFuture(symbol, Market.USA, expiryDate.Value);
                    return Symbol.CreateOption(futureUnderlying, Market.USA, optionStyle.Value, optionRight.Value, strike.Value, expiryDate.Value);
                default:
                    throw new NotSupportedException();
            }
        }
    }
}