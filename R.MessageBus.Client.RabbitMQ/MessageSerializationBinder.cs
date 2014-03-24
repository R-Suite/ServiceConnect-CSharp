using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace R.MessageBus.Client.RabbitMQ
{
    public class MessageSerializationBinder : SerializationBinder
    {
        private readonly IList<Type> _messageTypes;

        public MessageSerializationBinder(IList<Type> messageTypes = null)
        {
            _messageTypes = messageTypes;
        }

        public override void BindToName(Type serializedType, out string assemblyName, out string typeName)
        {
            assemblyName = null;
            typeName = serializedType.AssemblyQualifiedName;
        }

        public override Type BindToType(string assemblyName, string typeName)
        {
            Type typeToDeserialize = null;

            if (_messageTypes != null) // when message types are know in advance
            {
                typeToDeserialize = _messageTypes.SingleOrDefault(t => t.Name.Contains(typeName));
            }

            if (typeToDeserialize == null) // when the message type has been loaded before
            {
                typeToDeserialize = Type.GetType(string.Format("{0}, {1}", typeName, assemblyName));
            }

            return typeToDeserialize ?? (AppDomain.CurrentDomain.GetAssemblies() // when message as never ben used before, need to scan assemblies
                .Select(a => a.GetType(typeName))
                .FirstOrDefault(t => t != null));
        }
    }
}