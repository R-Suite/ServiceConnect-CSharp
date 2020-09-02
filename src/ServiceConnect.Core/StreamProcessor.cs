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

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Core
{
    public class StreamProcessor : IStreamProcessor
    {
       private readonly IBusContainer _container;

        public StreamProcessor(IBusContainer container)
        {
            _container = container;
        }

        public void ProcessMessage<T>(T message, IMessageBusReadStream stream) where T : Message
        {
            IList<HandlerReference> handlerReferences = _container.GetHandlerTypes(typeof(IStreamHandler<T>)).ToList();
            foreach (HandlerReference handlerReference in handlerReferences)
            {
                var handler = (IStreamHandler<T>)_container.GetInstance(handlerReference.HandlerType);
                handler.Stream = stream;
                new Task(() => handler.Execute(message)).Start();
            }
            stream.HandlerCount = handlerReferences.Count();
        }
    }
}
