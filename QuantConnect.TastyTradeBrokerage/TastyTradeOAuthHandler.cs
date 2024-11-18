using Newtonsoft.Json.Linq;
using QuantConnect.Configuration;
using QuantConnect.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace QuantConnect.Brokerages.TastyTrade
{
    public class TastyTradeOAuthHandler
    {
        private readonly string _clientId;
        private readonly string _clientSecret;
        private readonly string _redirectUri;
        private readonly string _baseUrl;
        private readonly string _authUrl;
        private readonly IHttpClient _client;
        private string _accessToken;
        private string _refreshToken;
        private DateTime _tokenExpiry;

        public TastyTradeOAuthHandler(string clientId, string clientSecret, string accessToken, string refreshToken, string redirectUri, bool isSandbox, IHttpClient client)
        {
            _clientId = clientId;
            _clientSecret = clientSecret;
            _redirectUri = redirectUri;
            _accessToken = accessToken;
            _refreshToken = refreshToken;
            _client = client;

            // Set URLs based on environment
            _baseUrl = isSandbox ? "https://api.cert.tastyworks.com" : "https://api.tastyworks.com";
            _authUrl = isSandbox ? "https://cert-auth.staging-tasty.works" : "https://oauth.tastytrade.com";

            _tokenExpiry = DateTime.UtcNow.AddHours(1); // Default expiry if not set
        }

        public async Task<string> GetAuthorizationHeader()
        {
            var token = await GetAccessToken();
            return $"Bearer {token}";
        }

        private async Task<string> GetAccessToken()
        {
            if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _tokenExpiry)
            {
                return _accessToken;
            }

            if (!string.IsNullOrEmpty(_refreshToken))
            {
                return await RefreshAccessToken();
            }

            throw new BrokerageException("No valid access token or refresh token available");
        }

        private async Task<string> RefreshAccessToken()
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/oauth/token")
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["grant_type"] = "refresh_token",
                        ["refresh_token"] = _refreshToken,
                        ["client_id"] = _clientId,
                        ["client_secret"] = _clientSecret
                    })
                };

                var response = await _client.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    throw new BrokerageException($"Error refreshing token: {content}");
                }

                var result = JObject.Parse(content);
                _accessToken = result["access_token"].ToString();
                _tokenExpiry = DateTime.UtcNow.AddSeconds(result["expires_in"].Value<int>());

                // Store new refresh token if provided
                if (result["refresh_token"] != null)
                {
                    _refreshToken = result["refresh_token"].ToString();
                }

                Log.Trace($"TastyTradeOAuthHandler.RefreshAccessToken(): Successfully refreshed access token. Expires in {_tokenExpiry}");

                return _accessToken;
            }
            catch (Exception e)
            {
                throw new BrokerageException($"Error refreshing access token: {e.Message}", e);
            }
        }

        public string GetAuthorizationUrl(string state = null)
        {
            var queryParams = new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["redirect_uri"] = _redirectUri,
                ["response_type"] = "code",
                ["scope"] = "trade"  // Add other scopes as needed
            };

            if (!string.IsNullOrEmpty(state))
            {
                queryParams["state"] = state;
            }

            var queryString = string.Join("&", queryParams.Select(kvp =>
                $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));

            return $"{_authUrl}/authorize?{queryString}";
        }

        public async Task<string> ExchangeCodeForToken(string code)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/oauth/token")
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["grant_type"] = "authorization_code",
                        ["code"] = code,
                        ["client_id"] = _clientId,
                        ["client_secret"] = _clientSecret,
                        ["redirect_uri"] = _redirectUri
                    })
                };

                var response = await _client.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    throw new BrokerageException($"Error exchanging code for token: {content}");
                }

                var result = JObject.Parse(content);
                _accessToken = result["access_token"].ToString();
                _refreshToken = result["refresh_token"]?.ToString();
                _tokenExpiry = DateTime.UtcNow.AddSeconds(result["expires_in"].Value<int>());

                Log.Trace("TastyTradeOAuthHandler.ExchangeCodeForToken(): Successfully exchanged code for access token");

                return _accessToken;
            }
            catch (Exception e)
            {
                throw new BrokerageException($"Error exchanging code for token: {e.Message}", e);
            }
        }

        public static string GetAuthorizationUrl(string clientId, string redirectUri, bool isSandbox = true)
        {
            var authUrl = isSandbox ? "https://cert-auth.staging-tasty.works" : "https://oauth.tastytrade.com";

            var queryParams = new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["redirect_uri"] = redirectUri,
                ["response_type"] = "code",
                ["scope"] = "trade openid"  // Include both trade and openid scopes
            };

            var queryString = string.Join("&", queryParams.Select(kvp =>
                $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));

            return $"{authUrl}/authorize?{queryString}";
        }

        public static async Task<(string accessToken, string refreshToken)> ExchangeCodeForTokens(
            string code,
            string clientId,
            string clientSecret,
            string redirectUri,
            bool isSandbox = true)
        {
            var baseUrl = isSandbox ? "https://api.cert.tastyworks.com" : "https://api.tastyworks.com";

            using var client = new HttpClient();
            var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/oauth/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["client_id"] = clientId,
                    ["client_secret"] = clientSecret,
                    ["redirect_uri"] = redirectUri
                })
            };

            var response = await client.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Failed to exchange code for tokens: {content}");
            }

            var result = JObject.Parse(content);
            return (
                result["access_token"]?.ToString(),
                result["refresh_token"]?.ToString()
            );
        }

        public static async Task<string> RefreshAccessToken(
            string refreshToken,
            string clientId,
            string clientSecret,
            bool isSandbox = true)
        {
            var baseUrl = isSandbox ? "https://api.cert.tastyworks.com" : "https://api.tastyworks.com";

            using var client = new HttpClient();
            var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/oauth/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refreshToken,
                    ["client_id"] = clientId,
                    ["client_secret"] = clientSecret
                })
            };

            var response = await client.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Failed to refresh token: {content}");
            }

            var result = JObject.Parse(content);
            return result["access_token"]?.ToString();
        }
    }
}
