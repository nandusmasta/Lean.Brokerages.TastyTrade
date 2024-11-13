using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace QuantConnect.Brokerages.TastyTrade
{
    public static class TastyTradeOAuthHelper
    {
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
