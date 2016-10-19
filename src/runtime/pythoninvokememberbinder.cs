using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq.Expressions;
using Microsoft.Scripting.Actions;
using Microsoft.Scripting.Runtime;
using System.Linq;
using System.Reflection;
using Microsoft.Scripting.Utils;

namespace Python.Runtime
{
    /// <summary>
    /// Actual member invocation, reuses most of the default implementation in <see cref="InvokeMemberBinder"/>
    /// </summary>
    internal class PythonInvokeMemberBinder : InvokeMemberBinder
    {
        private readonly CallSignature _callSignature;

        public PythonInvokeMemberBinder(string name, CallInfo callInfo)
            : base(name, /*ignoreCase*/ false, callInfo) {

            var argumentCount = callInfo.ArgumentCount;
            var argumentNames = callInfo.ArgumentNames;

            var positionalArgumentCount = argumentCount - argumentNames.Count;

            var arguments = new Argument[argumentCount];
            for (var i = 0; i < argumentCount; i++)
            {
                if (i >= positionalArgumentCount)
                    arguments[i] = new Argument(argumentNames[i-positionalArgumentCount]);
                else
                    arguments[i] = Argument.Simple;
            }
            _callSignature = new CallSignature(arguments);
        }

        public override DynamicMetaObject FallbackInvokeMember(DynamicMetaObject target, DynamicMetaObject[] args,
            DynamicMetaObject errorSuggestion) {
            // defer if we don't have any values
            if (!target.HasValue || args.Any(arg => !arg.HasValue))
            {
                var deferArgs = new DynamicMetaObject[args.Length + 1];
                for (var i = 0; i < args.Length; i++)
                {
                    deferArgs[i + 1] = args[i];
                }
                deferArgs[0] = target;
                return Defer(deferArgs);
            }

            var binder = PythonBinder.Instance;
            var resolverFactory = new PythonOverloadResolverFactory(binder);

            var callArgs = args;
            MethodBase[] methodTargets;
            var callType = CallTypes.None;
            if (typeof(BoundMemberTracker).IsAssignableFrom(target.LimitType))
            {
                // call member method on instance
                var boundMemberTracker = (BoundMemberTracker) target.Value;
                var methodGroup = (MethodGroup) boundMemberTracker.BoundTo;
                var boundMemberTrackerExpr = Expression.Convert(target.Expression, typeof(BoundMemberTracker));
                var objectInstance = new DynamicMetaObject(
                    Expression.Property(boundMemberTrackerExpr, typeof(BoundMemberTracker), "ObjectInstance"),
                    BindingRestrictions.Empty,
                    boundMemberTracker.ObjectInstance);
                callArgs = ArrayUtils.Insert(objectInstance, args);
                methodTargets = methodGroup.GetMethodBases();
                callType = CallTypes.ImplicitInstance;
            }
            else if (typeof(MethodGroup).IsAssignableFrom(target.LimitType))
            {
                // call static method after resolving method overload
                methodTargets = ((MethodGroup) target.Value).GetMethodBases();
            }
            else if (typeof(MethodTracker).IsAssignableFrom(target.LimitType))
            {
                // call single static method
                methodTargets = new MethodBase[] {((MethodTracker) target.Value).Method};
            }
            else
            {
                // target is something we don't recognize
                return errorSuggestion ?? new DynamicMetaObject(
                           Expression.Throw(Expression.New(typeof(ArgumentException)), ReturnType),
                           target.Restrictions.Merge(BindingRestrictions.Combine(args)));
            }
            
            // resolve and do the actual call
            var result = binder.CallMethod(
                resolverFactory.CreateOverloadResolver(callArgs, _callSignature, callType),
                methodTargets);

            var convertedResult = result.BindConvert(new PythonReturnConvertBinder());

            // generated call code is valid as long as target does not change and the restrictions on args are satisfied
            var targetInstanceRestriction = BindingRestrictions.GetInstanceRestriction(target.Expression, target.Value);
            var bindingRestrictions = targetInstanceRestriction.Merge(result.Restrictions);

            return new DynamicMetaObject(convertedResult.Expression, bindingRestrictions);
        }

        public override DynamicMetaObject FallbackInvoke(DynamicMetaObject target, DynamicMetaObject[] args, DynamicMetaObject errorSuggestion) {
            throw new NotImplementedException();
        }
    }
}