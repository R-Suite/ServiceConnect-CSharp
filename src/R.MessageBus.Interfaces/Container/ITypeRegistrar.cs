using System;
using System.Collections.Generic;

namespace R.MessageBus.Interfaces.Container
{
    public interface ITypeRegistrar
    {
        ITypeRegistrar RegisterFor(Type implementation, IEnumerable<Type> interfaces);

        ITypeRegistrar RegisterFor(Type implementation, params Type[] interfaces);

        ITypeRegistrar RegisterFor(object instance, params Type[] interfaces);

        ITypeRegistrar RegisterForAll(IEnumerable<Type> implementations);

        ITypeRegistrar RegisterForAll(params Type[] implementations);
    }
}
