using System;

namespace ServiceConnect.Core
{
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    public class RoutingKey : System.Attribute
    {
        string value;

        public RoutingKey(string value)
        {
            this.value = value;
        }

        public string GetValue()
        {
            return value;
        }
    }
}