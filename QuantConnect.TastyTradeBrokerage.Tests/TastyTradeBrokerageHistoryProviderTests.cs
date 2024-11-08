using Moq;
using System;
using NodaTime;
using System.Linq;
using NUnit.Framework;
using QuantConnect.Data;
using QuantConnect.Util;
using QuantConnect.Tests;
using QuantConnect.Interfaces;
using QuantConnect.Securities;
using System.Collections.Generic;

namespace QuantConnect.Brokerages.TastyTrade.Tests
{
    [TestFixture]
    public class TastyTradeBrokerageHistoryProviderTests
    {
        private TastyTradeBrokerage _tastyTradeBrokerage;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var (username, password, sessionToken) = TastyTradeBrokerageTestHelpers.GetConfigParameters();
            _tastyTradeBrokerage = new TastyTradeBrokerage(username, password, sessionToken, new Mock<IAlgorithm>().Object);
        }

        private static IEnumerable<TestCaseData> TestParameters
        {
            get
            {
                yield return new TestCaseData(Symbols.AAPL, Resolution.Minute, TickType.Trade, new DateTime(2024, 6, 12, 9, 30, 0), new DateTime(2024, 6, 17, 16, 0, 0));
                yield return new TestCaseData(Symbols.AAPL, Resolution.Hour, TickType.Trade, new DateTime(2024, 6, 17, 9, 30, 0), new DateTime(2024, 6, 17, 16, 0, 0));
                yield return new TestCaseData(Symbols.AAPL, Resolution.Daily, TickType.Trade, new DateTime(2024, 6, 17, 9, 30, 0), new DateTime(2024, 6, 21, 16, 0, 0));

                yield return new TestCaseData(Symbols.AAPL, Resolution.Tick, TickType.Quote, new DateTime(2024, 6, 17, 9, 30, 0), new DateTime(2024, 6, 17, 16, 0, 0));
                yield return new TestCaseData(Symbols.AAPL, Resolution.Second, TickType.Quote, new DateTime(2024, 6, 17, 9, 30, 0), new DateTime(2024, 6, 17, 16, 0, 0));
                yield return new TestCaseData(Symbols.AAPL, Resolution.Minute, TickType.Quote, new DateTime(2024, 6, 17, 9, 30, 0), new DateTime(2024, 6, 17, 16, 0, 0));
                yield return new TestCaseData(Symbols.AAPL, Resolution.Hour, TickType.Quote, new DateTime(2024, 6, 17, 9, 30, 0), new DateTime(2024, 6, 17, 16, 0, 0));
                yield return new TestCaseData(Symbols.AAPL, Resolution.Daily, TickType.Quote, new DateTime(2024, 6, 17, 9, 30, 0), new DateTime(2024, 6, 17, 16, 0, 0));

                var AAPLOption = Symbol.CreateOption(Symbols.AAPL, Symbols.AAPL.ID.Market, OptionStyle.American, OptionRight.Call, 100, new DateTime(2024, 06, 21));
                yield return new TestCaseData(AAPLOption, Resolution.Tick, TickType.Trade, new DateTime(2024, 6, 12, 9, 30, 0), new DateTime(2024, 6, 21, 16, 0, 0));
                yield return new TestCaseData(AAPLOption, Resolution.Second, TickType.Trade, new DateTime(2024, 6, 12, 9, 30, 0), new DateTime(2024, 6, 21, 16, 0, 0));
                yield return new TestCaseData(AAPLOption, Resolution.Minute, TickType.Trade, new DateTime(2024, 6, 12, 9, 30, 0), new DateTime(2024, 6, 21, 16, 0, 0));
                yield return new TestCaseData(AAPLOption, Resolution.Hour, TickType.Trade, new DateTime(2024, 6, 12, 9, 30, 0), new DateTime(2024, 6, 21, 16, 0, 0));
                yield return new TestCaseData(AAPLOption, Resolution.Daily, TickType.Trade, new DateTime(2024, 6, 12, 9, 30, 0), new DateTime(2024, 6, 21, 16, 0, 0));
            }
        }

        private static IEnumerable<TestCaseData> NotSupportHistoryParameters
        {
            get
            {
                yield return new TestCaseData(Symbols.AAPL, Resolution.Tick, TickType.Trade, new DateTime(default), new DateTime(default));
                yield return new TestCaseData(Symbols.AAPL, Resolution.Second, TickType.Trade, new DateTime(default), new DateTime(default));

                yield return new TestCaseData(Symbols.AAPL, Resolution.Second, TickType.OpenInterest, new DateTime(default), new DateTime(default));
                yield return new TestCaseData(Symbols.AAPL, Resolution.Minute, TickType.OpenInterest, new DateTime(default), new DateTime(default));
                yield return new TestCaseData(Symbols.AAPL, Resolution.Hour, TickType.OpenInterest, new DateTime(default), new DateTime(default));
                yield return new TestCaseData(Symbols.AAPL, Resolution.Daily, TickType.OpenInterest, new DateTime(default), new DateTime(default));

                yield return new TestCaseData(Symbols.AAPL, Resolution.Tick, TickType.OpenInterest, new DateTime(2024, 6, 10, 9, 30, 0), new DateTime(2024, 6, 17, 16, 0, 0));

                var AAPLOption = Symbol.CreateOption(Symbols.AAPL, Symbols.AAPL.ID.Market, OptionStyle.American, OptionRight.Call, 100, new DateTime(2024, 06, 21));
                yield return new TestCaseData(AAPLOption, Resolution.Second, TickType.OpenInterest, new DateTime(default), new DateTime(default));
                yield return new TestCaseData(AAPLOption, Resolution.Minute, TickType.OpenInterest, new DateTime(default), new DateTime(default));
                yield return new TestCaseData(AAPLOption, Resolution.Hour, TickType.OpenInterest, new DateTime(default), new DateTime(default));
                yield return new TestCaseData(AAPLOption, Resolution.Daily, TickType.OpenInterest, new DateTime(default), new DateTime(default));

                yield return new TestCaseData(AAPLOption, Resolution.Tick, TickType.Quote, new DateTime(2024, 6, 17, 9, 30, 0), new DateTime(2024, 6, 17, 16, 0, 0));
                yield return new TestCaseData(AAPLOption, Resolution.Second, TickType.Quote, new DateTime(2024, 6, 17, 9, 30, 0), new DateTime(2024, 6, 17, 16, 0, 0));
                yield return new TestCaseData(AAPLOption, Resolution.Minute, TickType.Quote, new DateTime(2024, 6, 17, 9, 30, 0), new DateTime(2024, 6, 17, 16, 0, 0));
                yield return new TestCaseData(AAPLOption, Resolution.Hour, TickType.Quote, new DateTime(2024, 6, 17, 9, 30, 0), new DateTime(2024, 6, 17, 16, 0, 0));
                yield return new TestCaseData(AAPLOption, Resolution.Daily, TickType.Quote, new DateTime(2024, 6, 17, 9, 30, 0), new DateTime(2024, 6, 17, 16, 0, 0));
            }
        }

        [Test, TestCaseSource(nameof(NotSupportHistoryParameters))]
        public void GetsHistoryWithNotSupportedParameters(Symbol symbol, Resolution resolution, TickType tickType, DateTime startDate, DateTime endDate)
        {
            var historyRequest = CreateHistoryRequest(symbol, resolution, tickType, startDate, endDate);
            var histories = _tastyTradeBrokerage.GetHistory(historyRequest)?.ToList();
            Assert.IsNull(histories);
        }

        [Test, TestCaseSource(nameof(TestParameters))]
        public void GetsHistory(Symbol symbol, Resolution resolution, TickType tickType, DateTime startDate, DateTime endDate)
        {
            var historyRequest = CreateHistoryRequest(symbol, resolution, tickType, startDate, endDate);

            var histories = _tastyTradeBrokerage.GetHistory(historyRequest).ToList();
            Assert.Greater(histories.Count, 0);
            Assert.IsTrue(histories.All(x => x.EndTime - x.Time == resolution.ToTimeSpan()));
            Assert.IsTrue(histories.All(x => x.Symbol == symbol));
            Assert.IsTrue(histories.All(x => x is not Data.Market.Tick tick || tick.TickType == tickType));
        }

        internal static HistoryRequest CreateHistoryRequest(Symbol symbol, Resolution resolution, TickType tickType, DateTime startDateTime,
            DateTime endDateTime, SecurityExchangeHours exchangeHours = null, DateTimeZone dataTimeZone = null)
        {
            if (exchangeHours == null)
            {
                exchangeHours = SecurityExchangeHours.AlwaysOpen(TimeZones.NewYork);
            }

            if (dataTimeZone == null)
            {
                dataTimeZone = TimeZones.NewYork;
            }

            var dataType = LeanData.GetDataType(resolution, tickType);
            return new HistoryRequest(
                startTimeUtc: startDateTime,
                endTimeUtc: endDateTime,
                dataType: dataType,
                symbol: symbol,
                resolution: resolution,
                exchangeHours: exchangeHours,
                dataTimeZone: dataTimeZone,
                fillForwardResolution: null,
                includeExtendedMarketHours: true,
                isCustomData: false,
                dataNormalizationMode: DataNormalizationMode.Adjusted,
                tickType: tickType
                );
        }
    }
}