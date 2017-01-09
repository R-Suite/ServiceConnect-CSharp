using System;
using System.Collections.Generic;

namespace ServiceConnect.Interfaces.Container
{
    public interface IContainer
    {
        /// <summary>
        /// Resolves a registered service, provided an interface. Services are register as singleton
        /// </summary>
        /// <seealso cref="Resolve(Type)"/>
        /// <typeparam name="TService">Interface service type</typeparam>
        /// <returns>Service instance</returns>
        TService Resolve<TService>();

        /// <summary>
        /// Resolves a registered service, provided an interface. Services are register as singleton
        /// </summary>
        /// <seealso cref="Resolve{TService}()"/>
        /// <param name="tService">Interface service type</param>
        /// <returns>Service instance</returns>
        object Resolve(Type tService);

        /// <summary>
        /// Resolves a registered service with constructor parameters
        /// </summary>
        /// <param name="tService"></param>
        /// <param name="arguments"></param>
        /// <returns></returns>
        object Resolve(Type tService, IDictionary<string, object> arguments);
    }
}
