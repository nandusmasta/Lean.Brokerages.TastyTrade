using System;
using QuantConnect.Util;
using QuantConnect.Packets;
using QuantConnect.Interfaces;
using QuantConnect.Securities;
using System.Collections.Generic;
using QuantConnect.Configuration;

namespace QuantConnect.Brokerages.TastyTrade
{
    public class TastyTradeBrokerageFactory : BrokerageFactory
    {
        public override Dictionary<string, string> BrokerageData => new Dictionary<string, string>
        {
            // OAuth Client Credentials
            { "tastytrade-client-id", Config.Get("tastytrade-client-id") },
            { "tastytrade-client-secret", Config.Get("tastytrade-client-secret") },
        
            // Username/Password Authentication
            { "tastytrade-username", Config.Get("tastytrade-username") },
            { "tastytrade-password", Config.Get("tastytrade-password") },
        
            // Common Settings
            { "tastytrade-environment", Config.Get("tastytrade-environment", "sandbox") },
            { "tastytrade-paper-trading", Config.Get("tastytrade-paper-trading", "true") },
            { "tastytrade-auth-method", Config.Get("tastytrade-auth-method", "credentials") }
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

            TastyTradeBrokerage brokerage;

            if (authMethod == "oauth")
            {
                var clientId = job.BrokerageData["tastytrade-client-id"];
                var clientSecret = job.BrokerageData["tastytrade-client-secret"];

                if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
                {
                    throw new Exception("TastyTrade client ID and secret are required for OAuth authentication.");
                }

                brokerage = new TastyTradeBrokerage(
                    clientId: clientId,
                    clientSecret: clientSecret,
                    environment: environment,
                    paperTrading: paperTrading,
                    algorithm: algorithm);
            }
            else
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

            Composer.Instance.AddPart<IDataQueueHandler>(brokerage);

            return brokerage;
        }

        public override void Dispose()
        {
        }
    }
}