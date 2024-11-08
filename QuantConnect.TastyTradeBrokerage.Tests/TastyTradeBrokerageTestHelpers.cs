using QuantConnect.Configuration;
using System;

namespace QuantConnect.Brokerages.TastyTrade.Tests;

public static class TastyTradeBrokerageTestHelpers
{
    public static (string Username, string Password, string SessionToken) GetConfigParameters(bool isValidateOnEmpty = true)
    {
        var (username, password, sessionToken) = (
            Config.Get("tasty-username"),
            Config.Get("tasty-password"),
            Config.Get("tasty-session-token")
        );

        if (!isValidateOnEmpty)
        {
            return (username, password, sessionToken);
        }

        if (string.IsNullOrEmpty(sessionToken) && (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)))
        {
            throw new ArgumentNullException("'Username' or 'Password' or 'Session Token' cannot be null or empty. Please check your configuration.");
        }

        return (username, password, sessionToken);
    }
}