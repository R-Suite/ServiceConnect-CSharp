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
        if (body is System.Linq.Expressions.UnaryExpression unary)
        {
            body = unary.Operand;
        }

        // Only support a direct property access on the lambda parameter (e.g. d => d.OrderId).
        // Anything else — nested chains (d => d.Inner.Id), method calls, constants — silently
        // produced an empty correlation key historically, so sagas would load the wrong
        // instance or create a duplicate. Fail loudly at registration time instead.
        if (body is not System.Linq.Expressions.MemberExpression member
            || member.Expression is not System.Linq.Expressions.ParameterExpression
            || member.Member is not PropertyInfo propInfo)
        {
            throw new ArgumentException(
                $"Process manager property mapping must be a direct property access on the data parameter (e.g. d => d.{nameof(IProcessManagerData.CorrelationId)}). Got: {processManagerProperty.Body}",
                nameof(processManagerProperty));
        }

        propertiesHierarchy[propInfo.Name] = propInfo.PropertyType;

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
