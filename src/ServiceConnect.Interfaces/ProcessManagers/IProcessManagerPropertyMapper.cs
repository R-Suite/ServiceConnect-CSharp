using System.Linq.Expressions;

namespace ServiceConnect.Interfaces;

public interface IProcessManagerPropertyMapper
{
    IReadOnlyList<ProcessManagerToMessageMap> Mappings { get; }
    void ConfigureMapping<TProcessManagerData, TMessage>(Expression<Func<TProcessManagerData, object>> processManagerProperty, Expression<Func<TMessage, object>> messageExpression) where TProcessManagerData : IProcessManagerData;
}
