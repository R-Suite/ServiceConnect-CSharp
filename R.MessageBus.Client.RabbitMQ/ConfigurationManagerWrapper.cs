using System.Configuration;
using R.MessageBus.Interfaces;

namespace R.MessageBus.Client.RabbitMQ
{
    public class ConfigurationManagerWrapper : IConfigurationManager
    {
        private readonly Configuration _config;

        public ConfigurationManagerWrapper(string configPath)
        {
            var configFileMap = new ExeConfigurationFileMap {ExeConfigFilename = configPath};
            _config = ConfigurationManager.OpenMappedExeConfiguration(configFileMap, ConfigurationUserLevel.None);
        }

        public KeyValueConfigurationCollection AppSettings
        {
            get
            {
                return _config.AppSettings.Settings;
            }
        }

        public string ConnectionStrings(string name)
        {
            return _config.ConnectionStrings.ConnectionStrings[name].ConnectionString;
        }

        public T GetSection<T>(string sectionName) where T : ConfigurationSection
        {
            return (T)_config.GetSection(sectionName);
        }
    }
}
