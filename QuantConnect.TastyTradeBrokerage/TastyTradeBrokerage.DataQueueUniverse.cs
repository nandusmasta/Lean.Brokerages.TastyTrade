using System;
using QuantConnect.Logging;
using QuantConnect.Securities;
using QuantConnect.Interfaces;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;

namespace QuantConnect.Brokerages.TastyTrade
{
    public partial class TastyTradeBrokerage : IDataQueueUniverseProvider
    {
        public IEnumerable<Symbol> LookupSymbols(Symbol symbol, bool includeExpired, string securityCurrency = null)
        {
            if (!symbol.SecurityType.IsOption())
            {
                Log.Error("TastyTradeBrokerage.LookupSymbols(): The provided symbol is not an option. SecurityType: " + symbol.SecurityType);
                yield break;
            }

            var exchangeTimeZone = MarketHoursDatabase.FromDataFolder().GetExchangeHours(symbol.ID.Market, symbol, symbol.SecurityType).TimeZone;
            var exchangeDate = DateTime.UtcNow.ConvertFromUtc(exchangeTimeZone).Date;

            var response = _client.GetAsync($"{_baseUrl}/option-chains/{symbol.Underlying.Value}/nested").Result;
            if (!response.IsSuccessStatusCode)
            {
                Log.Error($"TastyTradeBrokerage.LookupSymbols(): Failed to fetch option chain for {symbol.Underlying.Value}");
                yield break;
            }

            var content = response.Content.ReadAsStringAsync().Result;
            var optionChain = JsonConvert.DeserializeObject<JObject>(content);

            foreach (var expiration in optionChain["expirations"])
            {
                var expirationDate = expiration["expiration-date"].Value<DateTime>();
                if (expirationDate.Date < exchangeDate && !includeExpired)
                {
                    continue;
                }

                foreach (var strike in expiration["strikes"])
                {
                    var strikePrice = strike["strike-price"].Value<decimal>();
                    var putSymbol = strike["put"].ToString();
                    var callSymbol = strike["call"].ToString();

                    if (!string.IsNullOrEmpty(putSymbol))
                    {
                        yield return _symbolMapper.GetLeanSymbol("UsOption", putSymbol);
                    }

                    if (!string.IsNullOrEmpty(callSymbol))
                    {
                        yield return _symbolMapper.GetLeanSymbol("UsOption", callSymbol);
                    }
                }
            }
        }

        public bool CanPerformSelection()
        {
            return IsConnected;
        }
    }
}