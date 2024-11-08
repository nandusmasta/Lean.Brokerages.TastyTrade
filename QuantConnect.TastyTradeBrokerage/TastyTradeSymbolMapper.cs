using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace QuantConnect.Brokerages.TastyTrade
{
    public class TastyTradeSymbolMapper : ITastyTradeSymbolMapper
    {
        private static readonly Regex _optionSymbolRegex = new Regex(
            @"^(?<symbol>[A-Z]+)\s+(?<year>\d{6})(?<right>[CP])(?<strike>\d{8})$",
            RegexOptions.Compiled);

        public readonly HashSet<SecurityType> SupportedSecurityType = new()
        {
            SecurityType.Equity,
            SecurityType.Option,
            SecurityType.Future,
            SecurityType.FutureOption
        };

        public string GetBrokerageSymbol(Symbol symbol)
        {
            return symbol.SecurityType switch
            {
                SecurityType.Equity => symbol.Value,
                SecurityType.Option => GenerateOptionSymbol(symbol),
                SecurityType.Future => $"/{symbol.Value}",
                SecurityType.FutureOption => GenerateFutureOptionSymbol(symbol),
                _ => throw new NotSupportedException($"SecurityType {symbol.SecurityType} is not supported")
            };
        }

        public string GetBrokerageSecurityType(Symbol symbol)
        {
            return symbol.SecurityType switch
            {
                SecurityType.Equity => "Equity",
                SecurityType.Option => "Equity Option",
                SecurityType.Future => "Future",
                SecurityType.FutureOption => "Future Option",
                _ => throw new NotSupportedException($"SecurityType {symbol.SecurityType} is not supported")
            };
        }

        public Symbol GetLeanSymbol(string brokerageType, string brokerageSymbol)
        {
            switch (brokerageType.ToLowerInvariant())
            {
                case "equity":
                    return Symbol.Create(brokerageSymbol, SecurityType.Equity, Market.USA);

                case "equity option":
                    return ParseOptionSymbol(brokerageSymbol);

                case "future":
                    return Symbol.Create(brokerageSymbol.TrimStart('/'), SecurityType.Future, Market.USA);

                case "future option":
                    return ParseFutureOptionSymbol(brokerageSymbol);

                default:
                    throw new NotSupportedException($"BrokerageType {brokerageType} is not supported");
            }
        }

        public Symbol GetLeanSymbol(string brokerageSymbol, SecurityType securityType, string market,
            DateTime expirationDate = default, decimal strike = 0, OptionRight optionRight = OptionRight.Call)
        {
            switch (securityType)
            {
                case SecurityType.Option:
                    var underlying = Symbol.Create(brokerageSymbol, SecurityType.Equity, market);
                    return Symbol.CreateOption(underlying, market, OptionStyle.American,
                        optionRight, strike, expirationDate);

                case SecurityType.Future:
                    return Symbol.CreateFuture(brokerageSymbol, market, expirationDate);

                case SecurityType.FutureOption:
                    var futureUnderlying = Symbol.CreateFuture(brokerageSymbol, market, expirationDate);
                    return Symbol.CreateOption(futureUnderlying, market, OptionStyle.American,
                        optionRight, strike, expirationDate);

                default:
                    throw new NotSupportedException($"SecurityType {securityType} is not supported");
            }
        }

        private static string GenerateOptionSymbol(Symbol symbol)
        {
            if (symbol.SecurityType != SecurityType.Option)
            {
                throw new ArgumentException("Symbol must be an option");
            }

            var strikePrice = (Convert.ToInt32(symbol.ID.StrikePrice * 1000)).ToString("D8");
            return $"{symbol.Underlying.Value}  {symbol.ID.Date:yyMMdd}{symbol.ID.OptionRight.ToString()[0]}{strikePrice}";
        }

        private static string GenerateFutureOptionSymbol(Symbol symbol)
        {
            if (symbol.SecurityType != SecurityType.FutureOption)
            {
                throw new ArgumentException("Symbol must be a future option");
            }

            var strikePrice = (Convert.ToInt32(symbol.ID.StrikePrice)).ToString("D4");
            return $".{symbol.Underlying.Value} {symbol.ID.Date:yyMMdd}{symbol.ID.OptionRight.ToString()[0]}{strikePrice}";
        }

        private static Symbol ParseOptionSymbol(string brokerageSymbol)
        {
            var match = _optionSymbolRegex.Match(brokerageSymbol);
            if (!match.Success)
            {
                throw new ArgumentException($"Failed to parse option symbol: {brokerageSymbol}");
            }

            var ticker = match.Groups["symbol"].Value;
            var expiry = DateTime.ParseExact(match.Groups["year"].Value, "yyMMdd", null);
            var right = match.Groups["right"].Value == "C" ? OptionRight.Call : OptionRight.Put;
            var strike = decimal.Parse(match.Groups["strike"].Value) / 1000m;

            var underlying = Symbol.Create(ticker, SecurityType.Equity, Market.USA);
            return Symbol.CreateOption(underlying, Market.USA, OptionStyle.American, right, strike, expiry);
        }

        private static Symbol ParseFutureOptionSymbol(string brokerageSymbol)
        {
            // Example: ./ESZ3 EW4U3 230927P2975
            var parts = brokerageSymbol.Split(' ');
            if (parts.Length != 3)
            {
                throw new ArgumentException($"Failed to parse future option symbol: {brokerageSymbol}");
            }

            var futureSymbol = parts[0].TrimStart('.');
            var optionRoot = parts[1];
            var optionInfo = parts[2];

            var expiry = DateTime.ParseExact(optionInfo.Substring(0, 6), "yyMMdd", null);
            var right = optionInfo[6] == 'C' ? OptionRight.Call : OptionRight.Put;
            var strike = decimal.Parse(optionInfo.Substring(7));

            var underlying = Symbol.CreateFuture(futureSymbol, Market.USA, expiry);
            return Symbol.CreateOption(underlying, Market.USA, OptionStyle.American, right, strike, expiry);
        }
    }
}