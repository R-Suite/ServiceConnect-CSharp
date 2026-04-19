using System.Linq.Expressions;

namespace ServiceConnect.Interfaces;

/// <summary>
/// Defines how process-manager properties are matched against incoming message properties.
/// </summary>
public interface IProcessManagerPropertyMapper
{
    /// <summary>
    /// Gets the configured process-manager to message mappings.
    /// </summary>
    IReadOnlyList<ProcessManagerToMessageMap> Mappings { get; }

    /// <summary>
    /// Adds a mapping between a process-manager property and a message property.
    /// </summary>
    /// <typeparam name="TProcessManagerData">The process-manager data type.</typeparam>
    /// <typeparam name="TMessage">The message type.</typeparam>
    /// <param name="processManagerProperty">The process-manager property selector.</param>
    /// <param name="messageExpression">The message property selector.</param>
    void ConfigureMapping<TProcessManagerData, TMessage>(Expression<Func<TProcessManagerData, object>> processManagerProperty, Expression<Func<TMessage, object>> messageExpression) where TProcessManagerData : IProcessManagerData;
}
