using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Microsoft.Scripting.Actions;
using Microsoft.Scripting.Actions.Calls;

namespace Python.Runtime
{
    internal class PythonBinder : DefaultBinder
    {
        private readonly Type[] _genericParameterTypes;
        private readonly Type[] _argTypes;

        private static readonly AssemblyBuilder AssemblyBuilder = AppDomain.CurrentDomain
            .DefineDynamicAssembly(new AssemblyName { Name = "PythonBinderDynamic" }, AssemblyBuilderAccess.RunAndCollect);

        private static readonly ModuleBuilder ModuleBuilder = AssemblyBuilder
            .DefineDynamicModule(typeof(PythonBinder).FullName + ".Dynamic");

        private static int _dynamicTypeId;

        public PythonBinder() : this(null, null) {
        }

        public PythonBinder(Type[] genericParameterTypes, Type[] argTypes) {
            _genericParameterTypes = genericParameterTypes;
            _argTypes = argTypes;
        }

        /// <summary>
        /// Implicit *widening* numeric conversions
        /// 
        /// There is a potential for accuracy-loss, e.g., when converting from Int64 to Double
        /// </summary>
        internal static bool HasImplicitNumericConversion(Type fromType, Type toType) {
            if (fromType == typeof(bool)) {
                if (toType == typeof(int)) return true;
                return HasImplicitNumericConversion(typeof(int), toType);
            }

            switch (Type.GetTypeCode(fromType)) {
                case TypeCode.SByte:
                    switch (Type.GetTypeCode(toType)) {
                        case TypeCode.Int16:
                        case TypeCode.Int32:
                        case TypeCode.Int64:
                        case TypeCode.Single:
                        case TypeCode.Double:
                        case TypeCode.Decimal:
                            return true;
                        default:
                            return false;
                    }
                case TypeCode.Byte:
                    switch (Type.GetTypeCode(toType)) {
                        case TypeCode.Int16:
                        case TypeCode.UInt16:
                        case TypeCode.Int32:
                        case TypeCode.UInt32:
                        case TypeCode.Int64:
                        case TypeCode.UInt64:
                        case TypeCode.Single:
                        case TypeCode.Double:
                        case TypeCode.Decimal:
                            return true;
                        default:
                            return false;
                    }
                case TypeCode.Int16:
                    switch (Type.GetTypeCode(toType)) {
                        case TypeCode.Int32:
                        case TypeCode.Int64:
                        case TypeCode.Single:
                        case TypeCode.Double:
                        case TypeCode.Decimal:
                            return true;
                        default:
                            return false;
                    }
                case TypeCode.UInt16:
                    switch (Type.GetTypeCode(toType)) {
                        case TypeCode.Int32:
                        case TypeCode.UInt32:
                        case TypeCode.Int64:
                        case TypeCode.UInt64:
                        case TypeCode.Single:
                        case TypeCode.Double:
                        case TypeCode.Decimal:
                            return true;
                        default:
                            return false;
                    }
                case TypeCode.Int32:
                    switch (Type.GetTypeCode(toType)) {
                        case TypeCode.Int64:
                        case TypeCode.Single:
                        case TypeCode.Double:
                        case TypeCode.Decimal:
                            return true;
                        default:
                            return false;
                    }
                case TypeCode.UInt32:
                    switch (Type.GetTypeCode(toType)) {
                        case TypeCode.Int64:
                        case TypeCode.UInt64:
                        case TypeCode.Single:
                        case TypeCode.Double:
                        case TypeCode.Decimal:
                            return true;
                        default:
                            return false;
                    }
                case TypeCode.Int64:
                    switch (Type.GetTypeCode(toType)) {
                        case TypeCode.Single:
                        case TypeCode.Double:
                        case TypeCode.Decimal:
                            return true;
                        default:
                            return false;
                    }
                case TypeCode.UInt64:
                    switch (Type.GetTypeCode(toType)) {
                        case TypeCode.Single:
                        case TypeCode.Double:
                        case TypeCode.Decimal:
                            return true;
                        default:
                            return false;
                    }
                case TypeCode.Char:
                    switch (Type.GetTypeCode(toType)) {
                        case TypeCode.UInt16:
                        case TypeCode.Int32:
                        case TypeCode.UInt32:
                        case TypeCode.Int64:
                        case TypeCode.UInt64:
                        case TypeCode.Single:
                        case TypeCode.Double:
                        case TypeCode.Decimal:
                            return true;
                        default:
                            return false;
                    }
                case TypeCode.Single:
                    switch (Type.GetTypeCode(toType)) {
                        case TypeCode.Double:
                            return true;
                        default:
                            return false;
                    }
                case TypeCode.Double:
                    switch (Type.GetTypeCode(toType)) {
                        default:
                            return false;
                    }
                default:
                    return false;
            }
        }

        public override bool CanConvertFrom(Type fromType, Type toType, bool toNotNullable, NarrowingLevel level) {
            if (base.CanConvertFrom(fromType, toType, toNotNullable, level))
                return true;
            // int => enum conversion
            if (fromType.IsPrimitive && Type.GetTypeCode(fromType) == Type.GetTypeCode(toType))
                return true;
            // numeric conversions, only accuracy loss
            if (level >= NarrowingLevel.One && HasImplicitNumericConversion(fromType, toType))
                return true;
            return false;
        }

        public override MemberGroup GetMember(MemberRequestKind action, Type type, string name) {
            var memberGroup = base.GetMember(action, type, name);

            // if we have generic parameter types instantiate generic methods
            if (_genericParameterTypes != null) {
                // match on generic parameter length, no partial match
                var methods = memberGroup.OfType<MethodTracker>().Select(mt => mt.Method)
                    .Where(method => method.ContainsGenericParameters && method.GetGenericArguments().Length == _genericParameterTypes.Length)
                    .Select(method => method.MakeGenericMethod(_genericParameterTypes))
                    .Select(MemberTracker.FromMemberInfo)
                    .ToArray();
                memberGroup = new MemberGroup(methods);
            }

            // memberGroup should have all methods with a matching name, filter here by argument types
            if (_argTypes != null)
            {
                var methods = memberGroup.OfType<MethodTracker>().Select(mt => mt.Method).ToArray<MethodBase>();
                if (methods.Length == 0)
                    memberGroup = MemberGroup.EmptyGroup;
                else
                {
                    var selectedMethod = Type.DefaultBinder.SelectMethod(BindingFlags.Static, methods, _argTypes, null);
                    memberGroup = selectedMethod != null ? new MemberGroup(selectedMethod) : MemberGroup.EmptyGroup;
                }
            }

            var outAugmentedMemberTrackers = memberGroup.SelectMany(AddOverloadsForOutParameters).ToArray();
            return new MemberGroup(outAugmentedMemberTrackers);
        }

        private static string NextTypeName() {
            return "PythonBinderDynamic" + Interlocked.Increment(ref _dynamicTypeId);
        }

        private static IEnumerable<MemberTracker> AddOverloadsForOutParameters(MemberTracker memberTracker) {
            // keep the original 
            yield return memberTracker;

            var methodTracker = memberTracker as MethodTracker;
            if (methodTracker != null) {
                var declaringType = methodTracker.DeclaringType;

                var method = methodTracker.Method;
                var parameters = method.GetParameters();
                // go over arguments, if there are out parameters add another version of method using ref parameters
                // backwards because out parameters are normally at the end of the parameter list
                for (var pIdx = parameters.Length - 1; pIdx >= 0; pIdx--) {
                    if (parameters[pIdx].IsOut) {
                        var wrapArgTypes = new Type[parameters.Length];
                        var underlyingParamExprs = new ParameterExpression[parameters.Length];

                        for (var wrapArgIdx = 0; wrapArgIdx < parameters.Length; wrapArgIdx++) {
                            var paramType = parameters[wrapArgIdx].ParameterType;
                            wrapArgTypes[wrapArgIdx] = paramType;
                            underlyingParamExprs[wrapArgIdx] = Expression.Parameter(paramType, parameters[wrapArgIdx].Name);
                        }

                        MethodInfo methodInfo;

                        // build a type with the dynamic expression. If we originally called
                        // bool System.Collections.Generic.Dictionary<TKey, TValue>.TryGetValue(TKey, out TValue)
                        // we generate
                        // static bool PythonBinderDynamic123.TryGetValue.Call(TKey, ref TValue)

                        var wrapTypeName = NextTypeName();

                        var typeBuilder = ModuleBuilder.DefineType(wrapTypeName, TypeAttributes.Public);

                        var wrappedMethodName = method.Name;

                        var methodBuilder = typeBuilder.DefineMethod(wrappedMethodName,
                            MethodAttributes.Public | MethodAttributes.Static,
                            method.ReturnType, wrapArgTypes);

                        // create the expression, this uses "ref" for all parameters with "out" (or "ref") types
                        Type createdType;
                        if (method.IsStatic) {
                            var wrapDelegateSignature = new List<Type>(wrapArgTypes) { method.ReturnType }.ToArray();
                            var wrapDelegateType = Expression.GetDelegateType(wrapDelegateSignature);
                            var callExpr = Expression.Call(method, underlyingParamExprs.ToArray<Expression>());
                            var lambdaExpr = Expression.Lambda(wrapDelegateType, callExpr, underlyingParamExprs);

                            lambdaExpr.CompileToMethod(methodBuilder);
                            createdType = typeBuilder.CreateType();
                        }
                        else {
                            // we also wrap the instance call as a static call
                            var staticWrapArgTypes = new List<Type>() { declaringType };
                            staticWrapArgTypes.AddRange(wrapArgTypes);
                            var wrapDelegateType = Expression.GetDelegateType(
                                    new List<Type>(staticWrapArgTypes) { method.ReturnType }.ToArray());

                            var instanceParamExpr = Expression.Parameter(declaringType);
                            var wrapParamExprs = new List<ParameterExpression>() { instanceParamExpr };
                            wrapParamExprs.AddRange(underlyingParamExprs);
                            var callExpr = Expression.Call(instanceParamExpr, method, underlyingParamExprs.ToArray<Expression>());
                            var lambdaExpr = Expression.Lambda(wrapDelegateType, callExpr, wrapParamExprs);

                            lambdaExpr.CompileToMethod(methodBuilder);
                            createdType = typeBuilder.CreateType();
                        }


                        if (method.IsStatic) {
                            methodInfo = createdType.GetMethod(wrappedMethodName, wrapArgTypes);
                        }
                        else {
                            var staticWrapArgTypes = new List<Type>() { declaringType };
                            staticWrapArgTypes.AddRange(wrapArgTypes);

                            methodInfo = createdType.GetMethod(wrappedMethodName, staticWrapArgTypes.ToArray());
                        }

                        yield return MemberTracker.FromMemberInfo(methodInfo);

                        // break out of loop over parameters
                        break;
                    }
                }
            }
        }

        public static readonly PythonBinder Instance = new PythonBinder();

        public static PythonBinder ForOverload(Type[] genericParameterTypes, Type[] argTypes) {
            return new PythonBinder(genericParameterTypes, argTypes);
        }
    }
}