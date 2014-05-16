using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using R.MessageBus.Interfaces;

namespace R.MessageBus
{
    public class InlineRequestConfiguration : IInlineRequestConfiguration
    {
        public void Handle<TResponse>(Action<TResponse> handler) where TResponse : class
        {
            throw new NotImplementedException();
        }
    }
}
