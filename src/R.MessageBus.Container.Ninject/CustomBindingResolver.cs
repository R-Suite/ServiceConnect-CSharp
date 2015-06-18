using System;
using System.Collections.Generic;
using System.Linq;
using Ninject.Components;
using Ninject.Infrastructure;
using Ninject.Planning.Bindings;
using Ninject.Planning.Bindings.Resolvers;

namespace R.MessageBus.Container.Ninject
{
    public class CustomBindingResolver : NinjectComponent, IBindingResolver
    {
        /// <summary>
        /// Returns any bindings from the specified collection that match the specified GenericTypeDefinition.
        /// </summary>
        public IEnumerable<IBinding> Resolve(Multimap<Type, IBinding> bindings, Type service)
        {
            if (service.IsGenericTypeDefinition)
            {
                var genericType = service.GetGenericTypeDefinition();
                return bindings.Where(kvp => kvp.Key.IsGenericType
                                             && kvp.Key.GetGenericTypeDefinition() == genericType)
                    .SelectMany(kvp => kvp.Value);
            }

            return Enumerable.Empty<IBinding>();
        }
    }
}
