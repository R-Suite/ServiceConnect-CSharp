using System.Collections.Generic;
using R.MessageBus.Interfaces;

namespace R.MessageBus.Client.ZeroMQ
{
    public static class ZeroMQExtensions
    {
        public static void SetPublisherHost(this IConfiguration configuration, string host)
        {
            if (configuration.TransportSettings.ClientSettings == null)
            {
                configuration.TransportSettings.ClientSettings = new Dictionary<string, object>();
            }
            configuration.TransportSettings.ClientSettings.Add("PublisherHost", host);
        }

        public static void SetSubscriberHost(this IConfiguration configuration, string host)
        {
            if (configuration.TransportSettings.ClientSettings == null)
            {
                configuration.TransportSettings.ClientSettings = new Dictionary<string, object>();
            }
            configuration.TransportSettings.ClientSettings.Add("SubscriberHost", host);
        }

        public static void SetSenderHost(this IConfiguration configuration, string host)
        {
            if (configuration.TransportSettings.ClientSettings == null)
            {
                configuration.TransportSettings.ClientSettings = new Dictionary<string, object>();
            }
            configuration.TransportSettings.ClientSettings.Add("SenderHost", host);
        }

        public static void SetReceiverHost(this IConfiguration configuration, string host)
        {
            if (configuration.TransportSettings.ClientSettings == null)
            {
                configuration.TransportSettings.ClientSettings = new Dictionary<string, object>();
            }
            configuration.TransportSettings.ClientSettings.Add("ReceiverHost", host);
        }
    }
}
