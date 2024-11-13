using System;
using QuantConnect.Util;
using QuantConnect.Packets;
using QuantConnect.Interfaces;
using QuantConnect.Securities;
using System.Collections.Generic;
using QuantConnect.Configuration;
using QuantConnect.Logging;

namespace QuantConnect.Brokerages.TastyTrade
{
    public class TastyTradeBrokerageFactory : BrokerageFactory
    {
        public override Dictionary<string, string> BrokerageData => new Dictionary<string, string>
        {
            // OAuth Client Credentials
            { "tastytrade-client-id", Config.Get("tastytrade-client-id") },
            { "tastytrade-client-secret", Config.Get("tastytrade-client-secret") },
            { "tastytrade-access-token", Config.Get("tastytrade-access-token") },
            { "tastytrade-refresh-token", Config.Get("tastytrade-refresh-token") },
            { "tastytrade-redirect-uri", Config.Get("tastytrade-redirect-uri") },
    
            // Username/Password Authentication
            { "tastytrade-username", Config.Get("tastytrade-username") },
            { "tastytrade-password", Config.Get("tastytrade-password") },
    
            // Common Settings
            { "tastytrade-environment", Config.Get("tastytrade-environment", "sandbox") },
            { "tastytrade-paper-trading", Config.Get("tastytrade-paper-trading", "true") },
            { "tastytrade-auth-method", Config.Get("tastytrade-auth-method", "oauth") }
        };

        public TastyTradeBrokerageFactory() : base(typeof(TastyTradeBrokerage))
        {
        }

        public override IBrokerageModel GetBrokerageModel(IOrderProvider orderProvider) => new TastyTradeBrokerageModel();

        public override IBrokerage CreateBrokerage(LiveNodePacket job, IAlgorithm algorithm)
        {
            var environment = job.BrokerageData["tastytrade-environment"];
            var paperTrading = job.BrokerageData["tastytrade-paper-trading"].ConvertInvariant<bool>();
            var authMethod = job.BrokerageData["tastytrade-auth-method"].ToLowerInvariant();

            Log.Trace($"TastyTradeBrokerageFactory.CreateBrokerage(): Creating brokerage with auth method: {authMethod}");

            TastyTradeBrokerage brokerage;

            if (authMethod == "oauth")
            {
                var clientId = job.BrokerageData["tastytrade-client-id"];
                var clientSecret = job.BrokerageData["tastytrade-client-secret"];
                var accessToken = job.BrokerageData["tastytrade-access-token"];
                var refreshToken = job.BrokerageData["tastytrade-refresh-token"];
                var redirectUri = job.BrokerageData["tastytrade-redirect-uri"];

                if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
                {
                    throw new Exception("TastyTrade client ID and secret are required for OAuth authentication.");
                }

                if (string.IsNullOrEmpty(accessToken) && string.IsNullOrEmpty(refreshToken))
                {
                    throw new Exception("TastyTrade requires either an access token or a refresh token for OAuth authentication.");
                }

                brokerage = new TastyTradeBrokerage(
                    clientId: clientId,
                    clientSecret: clientSecret,
                    accessToken: accessToken,
                    refreshToken: refreshToken,
                    redirectUri: redirectUri,
                    environment: environment,
                    paperTrading: paperTrading,
                    algorithm: algorithm,
                    useOAuth: true);
            }
            else if (authMethod == "credentials")
            {
                var username = job.BrokerageData["tastytrade-username"];
                var password = job.BrokerageData["tastytrade-password"];

                if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                {
                    throw new Exception("TastyTrade username and password are required for credentials authentication.");
                }

                brokerage = new TastyTradeBrokerage(
                    username: username,
                    password: password,
                    environment: environment,
                    paperTrading: paperTrading,
                    algorithm: algorithm);
            }
            else
            {
                throw new Exception($"Unknown authentication method '{authMethod}'. Valid options are 'oauth' or 'credentials'.");
            }

            Composer.Instance.AddPart<IDataQueueHandler>(brokerage);

            return brokerage;
        }

        public override void Dispose()
        {
        }
    }
}