using System;

namespace QuantConnect.Brokerages.TastyTrade
{
    public interface IQuote
    {
        Symbol Symbol { get; }
        DateTime Time { get; }
        decimal BidPrice { get; }
        decimal AskPrice { get; }
        decimal BidSize { get; }
        decimal AskSize { get; }
    }

    public class Quote : IQuote
    {
        public Symbol Symbol { get; set; }
        public DateTime Time { get; set; }
        public decimal BidPrice { get; set; }
        public decimal AskPrice { get; set; }
        public decimal BidSize { get; set; }
        public decimal AskSize { get; set; }
    }
}