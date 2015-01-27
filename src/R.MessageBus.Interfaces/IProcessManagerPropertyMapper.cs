using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace R.MessageBus.Interfaces
{
    public interface IProcessManagerPropertyMapper
    {
        List<ProcessManagerToMessageMap> Mappings { get; set; }
        void ConfigureMapping<TProcessManagerData, TMessage>(Expression<Func<TProcessManagerData, object>> processManagerProperty, Expression<Func<TMessage, object>> messageExpression) where TProcessManagerData : IProcessManagerData;
    }
}