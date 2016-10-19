using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Scripting.Actions;
using Microsoft.Scripting.Actions.Calls;
using Microsoft.Scripting.Runtime;

namespace Python.Runtime
{
    /// <summary>
    /// Creates OverloadResolver, delegates almost everything to DefaultOverloadResolver
    /// 
    /// This boxes "ref arrays", i.e., the arrays returned by function with "ref" arguments in <see cref="RefReturnBox"/>
    /// so we can later unbox them properly and convert them to Python tuples
    /// 
    /// Also resolves the Python sequence to .NET enumerable conversion
    /// </summary>
    internal class PythonOverloadResolverFactory : OverloadResolverFactory
    {
        private readonly PythonBinder _binder;

        public PythonOverloadResolverFactory(PythonBinder binder) {
            _binder = binder;
        }

        internal class PythonOverloadResolver : DefaultOverloadResolver {

            private static readonly Lazy<ConstructorInfo> RefReturnBoxConstructorInfo = new Lazy<ConstructorInfo>(
                () => typeof(RefReturnBox).GetConstructor(new[] { typeof(object[]) }));

            private static readonly Lazy<MethodInfo> PythonSequenceArgBoxToArrayMethodInfo = new Lazy<MethodInfo>(
                () => typeof(PythonSequenceArgBox).GetMethod("ToArray"));

            public PythonOverloadResolver(ActionBinder binder, IList<DynamicMetaObject> args, CallSignature signature, CallTypes callType)
                : base(binder, args, signature, callType) {
            }

            /// <summary>
            /// Boxing here so we know that we need to unpack to a Python tuple
            /// </summary>
            protected override Expression GetByRefArrayExpression(Expression argumentArrayExpression) {
                var byRefArrayExpression = base.GetByRefArrayExpression(argumentArrayExpression);
                Debug.Assert(RefReturnBoxConstructorInfo != null, "refReturnBoxConstructorInfo != null");
                return Expression.New(RefReturnBoxConstructorInfo.Value, byRefArrayExpression);
            }

            public override bool CanConvertFrom(Type fromType, DynamicMetaObject fromArgument, ParameterWrapper toParameter, NarrowingLevel level) {
                // claim that we can convert python sequences to any IEnumerable (including arrays)
                if (fromType == typeof(PythonSequenceArgBox) && typeof(IEnumerable).IsAssignableFrom(toParameter.Type))
                    return true;
                return base.CanConvertFrom(fromType, fromArgument, toParameter, level);
            }

            /// <summary>
            /// Resolve ambiguity in favor of type identity
            /// </summary>
            public override Candidate SelectBestConversionFor(DynamicMetaObject arg, ParameterWrapper candidateOne, ParameterWrapper candidateTwo, NarrowingLevel level) {
                var argRuntimeType = arg.RuntimeType;
                if (argRuntimeType != null) {
                    if (argRuntimeType == candidateOne.Type && argRuntimeType != candidateTwo.Type)
                        return Candidate.One;
                    if (argRuntimeType != candidateOne.Type && argRuntimeType == candidateTwo.Type)
                        return Candidate.Two;
                }
                return base.SelectBestConversionFor(arg, candidateOne, candidateTwo, level);
            }

            /// <summary>
            /// Resolve conversion when multiple conversions exist.
            /// 
            /// This only works for numeric types for nwo and picks the "wider" type
            /// </summary>
            public override Candidate PreferConvert(Type t1, Type t2) {
                var ifc1 = GetEnumerableInterface(t1);
                var ifc2 = GetEnumerableInterface(t2);
                if (ifc1 != null && ifc2 != null) {
                    var et1 = ifc1.GetGenericArguments()[0];
                    var et2 = ifc2.GetGenericArguments()[0];
                    var et1leqet2 = PythonBinder.HasImplicitNumericConversion(et1, et2);
                    var et2leqet1 = PythonBinder.HasImplicitNumericConversion(et2, et1);
                    if (et1leqet2 && !et2leqet1)
                        return Candidate.Two;
                    if (!et1leqet2 && et2leqet1)
                        return Candidate.One;
                }
                return base.PreferConvert(t1, t2);
            }

            /// <summary>
            /// Conversion logic for Python sequences to C# enumerables
            /// </summary>
            public override Expression Convert(DynamicMetaObject metaObject, Type restrictedType, ParameterInfo info, Type toType) {
                if (metaObject.LimitType == typeof(PythonSequenceArgBox))
                {
                    if (toType.IsArray)
                    {
                        // easy, just convert to correct array
                        return Expression.Call(
                            Expression.Convert(metaObject.Expression, typeof(PythonSequenceArgBox)),
                            PythonSequenceArgBoxToArrayMethodInfo.Value,
                            Expression.Constant(toType, typeof(Type)));
                    }

                    // figure out the type argument in IEnumerable<T>
                    var iEnumerableIfc = GetEnumerableInterface(toType);
                    var elementType = iEnumerableIfc.GetGenericArguments()[0];

                    // convert to T[] array
                    var arrayType = elementType.MakeArrayType();
                    var toArrayCallExpr = Expression.Call(
                        Expression.Convert(metaObject.Expression, typeof(PythonSequenceArgBox)),
                        PythonSequenceArgBoxToArrayMethodInfo.Value,
                        Expression.Constant(arrayType, typeof(Type)));

                    // use collection initializer to create requested type
                    var collectionInitializerConstructorInfo = toType.GetConstructor(new[] { iEnumerableIfc });
                    if (collectionInitializerConstructorInfo != null)
                        return Expression.New(collectionInitializerConstructorInfo, Expression.Convert(toArrayCallExpr, arrayType));

                    // this might be an interface - maybe our base class can handle this if it's a straightforward cast
                    var arrayDynamicMetaObject = new DynamicMetaObject(toArrayCallExpr, BindingRestrictions.Empty);
                    return base.Convert(arrayDynamicMetaObject, arrayType, info, toType);
                }
                return base.Convert(metaObject, restrictedType, info, toType);
            }

            private static Type GetEnumerableInterface(Type toType) {
                Type iEnumerableIfc;
                if (toType.IsGenericType && typeof(IEnumerable<>) == toType.GetGenericTypeDefinition())
                {
                    iEnumerableIfc = toType;
                }
                else
                {
                    var ifcs = toType.GetInterfaces();
                    iEnumerableIfc = ifcs.SingleOrDefault(ifc => ifc.IsGenericType && typeof(IEnumerable<>) == ifc.GetGenericTypeDefinition());
                }
                return iEnumerableIfc;
            }
        }

        public override DefaultOverloadResolver CreateOverloadResolver(IList<DynamicMetaObject> args,
            CallSignature signature, CallTypes callType) {
            return new PythonOverloadResolver(_binder, args, signature, callType);
        }
    }
}