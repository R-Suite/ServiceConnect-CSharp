namespace ServiceConnect.Core
{
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