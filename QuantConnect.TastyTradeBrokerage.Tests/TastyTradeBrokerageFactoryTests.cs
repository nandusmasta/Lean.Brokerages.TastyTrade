using NUnit.Framework;
using QuantConnect.Util;
using QuantConnect.Interfaces;

namespace QuantConnect.Brokerages.TastyTrade.Tests
{
    [TestFixture]
    public class TastyTradeBrokerageFactoryTests
    {
        [Test]
        public void InitializesFactoryFromComposer()
        {
            using var factory = Composer.Instance.Single<IBrokerageFactory>(instance => instance.BrokerageType == typeof(TastyTradeBrokerage));
            Assert.IsNotNull(factory);
        }
    }
}