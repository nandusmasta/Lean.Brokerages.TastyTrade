using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace QuantConnect.Brokerages.TastyTrade
{
    public interface IHttpClient : IDisposable
    {
        Task<HttpResponseMessage> GetAsync(string requestUri);
        Task<HttpResponseMessage> PostAsync(string requestUri, HttpContent content);
        Task<HttpResponseMessage> PutAsync(string requestUri, HttpContent content);
        Task<HttpResponseMessage> DeleteAsync(string requestUri);
        Task<HttpResponseMessage> SendAsync(HttpRequestMessage request);
        void AddDefaultHeader(string name, string value);
        HttpRequestHeaders DefaultRequestHeaders { get; }
    }

    public class HttpClientWrapper : IHttpClient
    {
        private readonly HttpClient _client;

        public HttpClientWrapper()
        {
            _client = new HttpClient();
        }

        public HttpRequestHeaders DefaultRequestHeaders => _client.DefaultRequestHeaders;

        public void AddDefaultHeader(string name, string value)
        {
            _client.DefaultRequestHeaders.Add(name, value);
        }

        public Task<HttpResponseMessage> DeleteAsync(string requestUri)
        {
            return _client.DeleteAsync(requestUri);
        }

        public Task<HttpResponseMessage> GetAsync(string requestUri)
        {
            return _client.GetAsync(requestUri);
        }

        public Task<HttpResponseMessage> PostAsync(string requestUri, HttpContent content)
        {
            return _client.PostAsync(requestUri, content);
        }

        public Task<HttpResponseMessage> PutAsync(string requestUri, HttpContent content)
        {
            return _client.PutAsync(requestUri, content);
        }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
        {
            return _client.SendAsync(request);
        }

        public void Dispose()
        {
            _client?.Dispose();
        }
    }

    public interface ITastyTradeOrderUpdate
    {
        string Id { get; }
        string Status { get; }
        string AccountNumber { get; }
        decimal? Price { get; }
        decimal? FilledQuantity { get; }
        decimal? LeavesQuantity { get; }
        decimal? Quantity { get; }
        string Symbol { get; }
        string InstrumentType { get; }
        string Side { get; }
        string TimeInForce { get; }
        string OrderType { get; }
        DateTime UpdatedAt { get; }
        bool IsCanceled { get; }
        bool IsFilled { get; }
    }

    public class TastyTradeOrderUpdate : ITastyTradeOrderUpdate
    {
        public string Id { get; set; }
        public string Status { get; set; }
        public string AccountNumber { get; set; }
        public decimal? Price { get; set; }
        public decimal? FilledQuantity { get; set; }
        public decimal? LeavesQuantity { get; set; }
        public decimal? Quantity { get; set; }
        public string Symbol { get; set; }
        public string InstrumentType { get; set; }
        public string Side { get; set; }
        public string TimeInForce { get; set; }
        public string OrderType { get; set; }
        public DateTime UpdatedAt { get; set; }
        public bool IsCanceled => Status.Equals("cancelled", StringComparison.OrdinalIgnoreCase);
        public bool IsFilled => FilledQuantity == Quantity;
    }

    public interface ITastyTradeSymbolMapper : ISymbolMapper
    {
        string GetBrokerageSecurityType(Symbol symbol);
    }
}