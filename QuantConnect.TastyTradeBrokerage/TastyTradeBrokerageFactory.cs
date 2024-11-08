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
            { "tasty-username", Config.Get("tasty-username") },
            { "tasty-password", Config.Get("tasty-password") },
            { "tasty-session-token", Config.Get("tasty-session-token") }
        };

        public TastyTradeBrokerageFactory() : base(typeof(TastyTradeBrokerage))
        {
        }

        public override IBrokerageModel GetBrokerageModel(IOrderProvider orderProvider) => new TastyTradeBrokerageModel();

        public override IBrokerage CreateBrokerage(LiveNodePacket job, IAlgorithm algorithm)
        {
            job.BrokerageData.TryGetValue("tasty-username", out var username);
            job.BrokerageData.TryGetValue("tasty-password", out var password);
            job.BrokerageData.TryGetValue("tasty-session-token", out var sessionToken);

            var tastyBrokerage = new TastyTradeBrokerage(username, password, sessionToken, algorithm);

            if (!string.IsNullOrEmpty(username) || !string.IsNullOrEmpty(sessionToken))
            {
                Composer.Instance.AddPart<IDataQueueHandler>(tastyBrokerage);
            }

            return tastyBrokerage;
        }

        public override void Dispose()
        {
        }
    }
}