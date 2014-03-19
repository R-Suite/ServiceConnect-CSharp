using System.Configuration;

namespace R.MessageBus.Interfaces
{
    public interface IConfigurationManager
    {
        KeyValueConfigurationCollection AppSettings
        {
            get;
        }

        string ConnectionStrings(string name);

        T GetSection<T>(string sectionName) where T : ConfigurationSection;
    }
}
