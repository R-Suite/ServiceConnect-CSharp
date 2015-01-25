using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;

namespace R.MessageBus.Interfaces
{
    public class ProcessManagerToMessageMap
    {
        public Func<object, object> MessageProp;
        public Type MessageType;
        public Dictionary<string, Type> PropertiesHierarchy;
    }

    /// <summary>
    /// Creates mapping between ProcessManager property and Message property.
    /// </summary>
    public class ProcessManagerPropertyMapper
    {
        public readonly List<ProcessManagerToMessageMap> Mappings = new List<ProcessManagerToMessageMap>();

        public void ConfigureMapping<TProcessManagerData, TMessage>(Expression<Func<TProcessManagerData, object>> processManagerProperty, Expression<Func<TMessage, object>> messageExpression) where TProcessManagerData : IProcessManagerData
        {
            MemberExpression me = GetMemberExpression(processManagerProperty);
            MemberInfo mi = me.Member;

            var propertiesHierarchy = new Dictionary<String, Type>();

            while (true)
            {
                var pi = mi as PropertyInfo;
                if (null == pi) throw new ArgumentException("Member is not a property");

                propertiesHierarchy.Add(mi.Name, pi.PropertyType);

                if (mi.ReflectedType == typeof (TProcessManagerData))
                {
                    break;
                }
                me = (me.Expression as MemberExpression);
                if (me == null)
                    throw new ArgumentException("Expression is not a member access");

                mi = me.Member;
            }

            Func<TMessage, object> compiledMessageExpression = messageExpression.Compile();
            var messageFunc = new Func<object, object>(o => compiledMessageExpression((TMessage)o));

            Mappings.Add(new ProcessManagerToMessageMap
            {
                MessageProp = messageFunc,
                MessageType = typeof(TMessage),
                PropertiesHierarchy = propertiesHierarchy
            });
        }

        /// <summary>
        /// http://stackoverflow.com/questions/671968/retrieving-property-name-from-lambda-expression
        /// </summary>
        /// <param name="propertyExpression"></param>
        /// <returns></returns>
        static MemberExpression GetMemberExpression(Expression propertyExpression)
        {
            if (propertyExpression == null) 
                throw new ArgumentNullException("propertyExpression");

            var lambda = propertyExpression as LambdaExpression;
            if (lambda == null) 
                throw new ArgumentException("Not a lambda expression", "propertyExpression");

            MemberExpression memberExpr = null;

            if (lambda.Body.NodeType == ExpressionType.Convert)
            {
                memberExpr = ((UnaryExpression)lambda.Body).Operand as MemberExpression;
            }
            else if (lambda.Body.NodeType == ExpressionType.MemberAccess)
            {
                memberExpr = lambda.Body as MemberExpression;
            }

            if (memberExpr == null) 
                throw new ArgumentException("Expression is not a member access", "propertyExpression");

            return memberExpr;
        }
    }
}
