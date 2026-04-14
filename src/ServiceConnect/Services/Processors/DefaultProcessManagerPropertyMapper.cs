using System.Reflection;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

internal sealed class DefaultProcessManagerPropertyMapper : IProcessManagerPropertyMapper
{
    private readonly List<ProcessManagerToMessageMap> _mappings = [];
    public IReadOnlyList<ProcessManagerToMessageMap> Mappings => _mappings;

    public void ConfigureMapping<TProcessManagerData, TMessage>(
        System.Linq.Expressions.Expression<Func<TProcessManagerData, object>> processManagerProperty,
        System.Linq.Expressions.Expression<Func<TMessage, object>> messageExpression)
        where TProcessManagerData : IProcessManagerData
    {
        var propertiesHierarchy = new Dictionary<string, Type>();

        var body = processManagerProperty.Body;
        if (body is System.Linq.Expressions.UnaryExpression unary) body = unary.Operand;
        if (body is System.Linq.Expressions.MemberExpression member)
        {
            var propInfo = (PropertyInfo)member.Member;
            propertiesHierarchy[propInfo.Name] = propInfo.PropertyType;
        }

        var map = new ProcessManagerToMessageMap
        {
            MessageType = typeof(TMessage),
            PropertiesHierarchy = propertiesHierarchy,
            MessageProp = BuildMessageFunc(messageExpression)
        };

        _mappings.Add(map);
    }

    private static Func<object, object> BuildMessageFunc<TMessage>(
        System.Linq.Expressions.Expression<Func<TMessage, object>> messageExpression)
    {
        var compiled = messageExpression.Compile();
        return obj => compiled((TMessage)obj);
    }
}
