using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ServiceConnect.Interfaces.Container;

namespace ServiceConnect.Container.Default
{
    /// <summary>
    /// Custom implementation of IoC Container
    /// </summary>
    public class Container : IServicesRegistrar
    {
        #region Fields

        private readonly IDictionary<Type, ServiceDescriptor> _services = new Dictionary<Type, ServiceDescriptor>();

        #endregion

        #region Public Properties

        public IDictionary<Type, ServiceDescriptor> AllInstances
        {
            get { return _services; }
        }

        #endregion

        #region IServiceContainer Members

        /// <inheritdoc/>
        public TService Resolve<TService>()
        {
            return (TService)Resolve(typeof(TService));
        }

        /// <inheritdoc/>
        public object Resolve(Type tService)
        {
            var result = GetInstance(tService);

            return result;
        }

        /// <inheritdoc/>
        public object Resolve(Type tService, IDictionary<string, object> arguments)
        {
            if (_services.ContainsKey(tService))
            {
                var ctor = _services[tService].ServiceType.GetConstructors().First();

                IList<object> dependecies = new List<object>();
                ParameterInfo[] ctorParams = ctor.GetParameters();
                foreach (ParameterInfo parameterInfo in ctorParams)
                {
                    if (arguments.ContainsKey(parameterInfo.Name))
                        dependecies.Add(arguments[parameterInfo.Name]);
                }

                return ctor.Invoke(dependecies.ToArray());
            }

            throw new Exception("Type not registered" + tService);
        }

        private object GetInstance(Type tService)
        {
            if (_services.ContainsKey(tService))
            {
                return GetInstance(_services[tService]);
            }

            if (_services.Values.Any(v => v.ServiceType == tService))
            {
                var serviceDesc = _services.Values.First(v => v.ServiceType == tService);
                var instance = serviceDesc.Instance;
                return instance ?? CreateInstance(serviceDesc.ServiceType);
            }

            var genericDefinition = tService.GetGenericTypeDefinition();
            if (genericDefinition != null && _services.ContainsKey(genericDefinition))
            {
                return GetGenericInstance(tService, _services[genericDefinition]
                    .ServiceType);
            }

            throw new Exception("Type not registered" + tService);
        }

        private object GetInstance(ServiceDescriptor serviceDescriptor)
        {
            return serviceDescriptor.Instance ?? (
                serviceDescriptor.Instance = CreateInstance(serviceDescriptor.ServiceType));
        }

        private object GetGenericInstance(Type tService, Type genericDefinition)
        {
            var genericArguments = tService.GetGenericArguments();
            var actualType = genericDefinition.MakeGenericType(genericArguments);
            var result = CreateInstance(actualType);

            _services[tService] = new ServiceDescriptor
            {
                ServiceType = actualType,
                Instance = result
            };

            return result;
        }

        private object CreateInstance(Type serviceType)
        {
            var ctor = serviceType.GetConstructors().First();
            var dependecies = ctor.GetParameters().Select(p => Resolve(p.ParameterType)).ToArray();

            return ctor.Invoke(dependecies);
        }

        #endregion

        #region IConfigurableServiceRepository Members

        public ITypeRegistrar RegisterForAll(params Type[] implementations)
        {
            return RegisterForAll((IEnumerable<Type>)implementations);
        }

        public ITypeRegistrar RegisterForAll(IEnumerable<Type> implementations)
        {
            foreach (var impl in implementations)
            {
                var types = impl.GetInterfaces().ToList();
                if (impl.BaseType != null && impl.BaseType != typeof(object))
                {
                    types.Add(impl.BaseType);
                }
                RegisterFor(impl, types.ToArray());
            }

            return this;
        }

        public ITypeRegistrar RegisterFor(Type implementation, params Type[] interfaces)
        {
            return RegisterFor(implementation, (IEnumerable<Type>)interfaces);
        }

        public ITypeRegistrar RegisterFor(Type implementation, IEnumerable<Type> interfaces)
        {
            foreach (var @interface in interfaces)
            {
                var descriptor = new ServiceDescriptor
                {
                    ServiceType = implementation
                };
                _services[GetRegistrableType(@interface)] = descriptor;
            }

            return this;
        }

        public ITypeRegistrar RegisterFor(object instance, params Type[] interfaces)
        {
            foreach (var @interface in interfaces)
            {
                var descriptor = new ServiceDescriptor
                {
                    ServiceType = instance.GetType(),
                    Instance = instance
                };
                _services[GetRegistrableType(@interface)] = descriptor;
            }

            return this;
        }

        private static Type GetRegistrableType(Type type)
        {
            return type.IsGenericType && type.ContainsGenericParameters
                ? type.GetGenericTypeDefinition()
                : type;
        }

        #endregion
    }
}
