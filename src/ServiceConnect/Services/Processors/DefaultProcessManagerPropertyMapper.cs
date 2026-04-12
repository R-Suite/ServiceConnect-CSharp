using System.Reflection;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

internal class DefaultProcessManagerPropertyMapper : IProcessManagerPropertyMapper
{
    public List<ProcessManagerToMessageMap> Mappings { get; set; } = [];

    public void ConfigureMapping<TProcessManagerData, TMessage>(
        System.Linq.Expressions.Expression<Func<TProcessManagerData, object>> processManagerProperty,
        System.Linq.Expressions.Expression<Func<TMessage, object>> messageExpression)
        where TProcessManagerData : IProcessManagerData
    {
        var map = new ProcessManagerToMessageMap
        {
            MessageType = typeof(TMessage),
            PropertiesHierarchy = new Dictionary<string, Type>(),
            MessageProp = BuildMessageFunc(messageExpression)
        };

        var body = processManagerProperty.Body;
        if (body is System.Linq.Expressions.UnaryExpression unary) body = unary.Operand;
        if (body is System.Linq.Expressions.MemberExpression member)
        {
            var propInfo = (PropertyInfo)member.Member;
            map.PropertiesHierarchy[propInfo.Name] = propInfo.PropertyType;
        }

        Mappings.Add(map);
    }

    private static Func<object, object> BuildMessageFunc<TMessage>(
        System.Linq.Expressions.Expression<Func<TMessage, object>> messageExpression)
    {
        var compiled = messageExpression.Compile();
        return obj => compiled((TMessage)obj);
    }
}
