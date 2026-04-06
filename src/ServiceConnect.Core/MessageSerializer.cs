// Copyright (C) 2015 Timothy Watson, Jakub Pachansky

// This program is free software; you can redistribute it and/or
// modify it under the terms of the GNU General Public License
// as published by the Free Software Foundation; either version 2
// of the License, or (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program; if not, write to the Free Software
// Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA 02110-1301, USA.

using System;
using System.Text;
using Newtonsoft.Json;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Core
{
    public static class MessageSerializer
    {
        public static byte[] Serialize<T>(T message) where T : Message
        {
            string messageString = JsonConvert.SerializeObject(message);
            return Encoding.UTF8.GetBytes(messageString);
        }

        public static byte[] SerializeObject(object message)
        {
            string messageString = JsonConvert.SerializeObject(message);
            return Encoding.UTF8.GetBytes(messageString);
        }

        public static T Deserialize<T>(byte[] data) where T : class
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            string messageString = Encoding.UTF8.GetString(data);
            return JsonConvert.DeserializeObject<T>(messageString);
        }

        public static object Deserialize(byte[] data, Type type)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            if (type == null)
                throw new ArgumentNullException(nameof(type));

            string messageString = Encoding.UTF8.GetString(data);
            return JsonConvert.DeserializeObject(messageString, type);
        }
    }
}