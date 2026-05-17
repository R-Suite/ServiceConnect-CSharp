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
        where TMessage : Message
    {
        var propertiesHierarchy = new Dictionary<string, Type>(StringComparer.Ordinal);

        var body = processManagerProperty.Body;
        if (body is System.Linq.Expressions.UnaryExpression unary)
        {
            body = unary.Operand;
        }

        // Walk the MemberExpression chain from outer to inner so nested-property mappings
        // (d => d.Inner.Id) are honoured alongside single-property mappings (d => d.OrderId).
        // The walk visits the OUTERMOST property first (Id), then its parent (Inner); the
        // persistor's foreach-over-PropertiesHierarchy.Reverse() then iterates inner-to-outer
        // (Inner, Id) which is the order Expression.MakeMemberAccess needs to navigate
        // data.Data → data.Data.Inner → data.Data.Inner.Id. The terminal Expression must be
        // the lambda parameter; method calls, constants, and non-property members are
        // rejected loudly at registration time to prevent silent miscorrelation.
        var current = body;
        while (current is System.Linq.Expressions.MemberExpression mem)
        {
            if (mem.Member is not PropertyInfo memberProp)
            {
                throw new ArgumentException(
                    $"Process manager property mapping must be a property-access chain (e.g. d => d.{nameof(IProcessManagerData.CorrelationId)} or d => d.Inner.Id). " +
                    $"Encountered non-property member '{mem.Member.Name}' in: {processManagerProperty.Body}",
                    nameof(processManagerProperty));
            }
            propertiesHierarchy[memberProp.Name] = memberProp.PropertyType;
            current = mem.Expression!;
        }

        if (current is not System.Linq.Expressions.ParameterExpression || propertiesHierarchy.Count == 0)
        {
            throw new ArgumentException(
                $"Process manager property mapping must be a property-access chain rooted on the data parameter (e.g. d => d.{nameof(IProcessManagerData.CorrelationId)} or d => d.Inner.Id). Got: {processManagerProperty.Body}",
                nameof(processManagerProperty));
        }

        // Reject duplicate mappings for the same TMessage. FindData picks the first match
        // via FirstOrDefault — if duplicates were allowed, the second ConfigureMapping call
        // would be dead code with no warning. Throwing loudly surfaces misconfigurations at
        // startup instead of as silent miscorrelation at runtime.
        var messageType = typeof(TMessage);
        for (int i = 0; i < _mappings.Count; i++)
        {
            if (_mappings[i].MessageType == messageType)
            {
                throw new InvalidOperationException(
                    $"ConfigureMapping was called more than once for message type '{messageType.FullName}'. " +
                    "Each TMessage may be configured at most once per saga; subsequent calls would be silently ignored by FirstOrDefault lookup. " +
                    "If you intended to replace the mapping, refactor the configuration to call ConfigureMapping a single time.");
            }
        }

        var map = new ProcessManagerToMessageMap
        {
            MessageType = messageType,
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
