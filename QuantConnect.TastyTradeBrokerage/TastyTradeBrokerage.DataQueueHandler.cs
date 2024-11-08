using System;
using QuantConnect.Data;
using QuantConnect.Packets;
using QuantConnect.Interfaces;
using System.Collections.Generic;

namespace QuantConnect.Brokerages.TastyTrade
{
    public partial class TastyTradeBrokerage : IDataQueueHandler
    {
        public IEnumerator<BaseData> Subscribe(SubscriptionDataConfig dataConfig, EventHandler newDataAvailableHandler)
        {
            if (!CanSubscribe(dataConfig.Symbol))
            {
                return null;
            }

            var enumerator = _aggregator.Add(dataConfig, newDataAvailableHandler);
            _subscriptionManager.Subscribe(dataConfig);

            return enumerator;
        }

        public void Unsubscribe(SubscriptionDataConfig dataConfig)
        {
            _subscriptionManager.Unsubscribe(dataConfig);
            _aggregator.Remove(dataConfig);
        }

        public void SetJob(LiveNodePacket job)
        {
            job.BrokerageData.TryGetValue("tasty-username", out var username);
            job.BrokerageData.TryGetValue("tasty-password", out var password);
            job.BrokerageData.TryGetValue("tasty-session-token", out var sessionToken);

            Initialize(username, password, sessionToken, null, null);
            if (!IsConnected)
            {
                Connect();
            }
        }

        private bool CanSubscribe(Symbol symbol)
        {
            if (symbol.Value.IndexOfInvariant("universe", true) != -1 || symbol.IsCanonical())
            {
                return false;
            }
            return _symbolMapper.SupportedSecurityType.Contains(symbol.SecurityType);
        }
    }
}