//Copyright (C) 2015  Timothy Watson, Jakub Pachansky

//This program is free software; you can redistribute it and/or
//modify it under the terms of the GNU General Public License
//as published by the Free Software Foundation; either version 2
//of the License, or (at your option) any later version.

//This program is distributed in the hope that it will be useful,
//but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//GNU General Public License for more details.

//You should have received a copy of the GNU General Public License
//along with this program; if not, write to the Free Software
//Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

using System.Configuration;
using R.MessageBus.Interfaces;

namespace R.MessageBus.Core
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
