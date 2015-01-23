using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;

namespace R.MessageBus.Interfaces
{
    public class ProcessManagerToMessageMap
    {
        public Func<object, object> MessageProp;
        public string ProcessManagerPropName;
        public Type MessageType;
    }

    public class ProcessManagerPropertyMapper
    {
        public List<ProcessManagerToMessageMap> Mappings = new List<ProcessManagerToMessageMap>();

        public void ConfigureMapping<TProcessManagerData, TMessage>(Expression<Func<TProcessManagerData, object>> processManagerProperty, Expression<Func<TMessage, object>> messageExpression) where TProcessManagerData : IProcessManagerData
        {
            var processManagerPropertyInfo = GetMemberInfo(processManagerProperty) as PropertyInfo;
            if (null == processManagerPropertyInfo) throw new ArgumentException("Member is not a property");

            Func<TMessage, object> compiledMessageExpression = messageExpression.Compile();
            var messageFunc = new Func<object, object>(o => compiledMessageExpression((TMessage)o));

            Mappings.Add(new ProcessManagerToMessageMap
            {
                MessageProp = messageFunc,
                ProcessManagerPropName = processManagerPropertyInfo.Name,
                MessageType = typeof(TMessage)
            });
        }

        static MemberInfo GetMemberInfo(Expression member)
        {
            if (member == null) throw new ArgumentNullException("member");

            var lambda = member as LambdaExpression;
            if (lambda == null) throw new ArgumentException("Not a lambda expression", "member");

            MemberExpression memberExpr = null;

            if (lambda.Body.NodeType == ExpressionType.Convert)
            {
                memberExpr = ((UnaryExpression)lambda.Body).Operand as MemberExpression;
            }
            else if (lambda.Body.NodeType == ExpressionType.MemberAccess)
            {
                memberExpr = lambda.Body as MemberExpression;
            }

            if (memberExpr == null) throw new ArgumentException("Not a member access", "member");

            return memberExpr.Member;
        }
    }
}
