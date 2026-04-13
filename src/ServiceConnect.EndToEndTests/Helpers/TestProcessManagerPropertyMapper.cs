using System.Linq.Expressions;
using System.Reflection;
using ServiceConnect.Interfaces;

namespace ServiceConnect.EndToEndTests.Helpers;

public class TestProcessManagerPropertyMapper : IProcessManagerPropertyMapper
{
    private readonly List<ProcessManagerToMessageMap> _mappings = new();
    public IReadOnlyList<ProcessManagerToMessageMap> Mappings => _mappings;

    public void ConfigureMapping<TProcessManagerData, TMessage>(
        Expression<Func<TProcessManagerData, object>> processManagerProperty,
        Expression<Func<TMessage, object>> messageExpression)
        where TProcessManagerData : IProcessManagerData
    {
        var map = new ProcessManagerToMessageMap
        {
            MessageType = typeof(TMessage),
            PropertiesHierarchy = new Dictionary<string, Type>(),
            MessageProp = BuildMessageFunc(messageExpression)
        };

        var body = processManagerProperty.Body;
        if (body is UnaryExpression unary) body = unary.Operand;
        if (body is MemberExpression member)
        {
            var propInfo = (PropertyInfo)member.Member;
            map.PropertiesHierarchy[propInfo.Name] = propInfo.PropertyType;
        }

        _mappings.Add(map);
    }

    private static Func<object, object> BuildMessageFunc<TMessage>(Expression<Func<TMessage, object>> messageExpression)
    {
        var compiled = messageExpression.Compile();
        return obj => compiled((TMessage)obj);
    }
}
