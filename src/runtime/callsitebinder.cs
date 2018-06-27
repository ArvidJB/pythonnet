using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.Scripting;
using Microsoft.Scripting.Actions;

namespace Python.Runtime
{
    /// <summary>
    /// Uses <see cref="CallSite"/>s to invoke CLR methods from Python
    ///
    /// We convert the Python arguments to their CLR counterparts, dispatch the call based
    /// on the converted arguments, and then convert the returned data back to Python
    /// </summary>
    internal class CallSiteBinder
    {
        // map CallInfo (== number of arguments, their names) to CallSite
        private readonly Dictionary<CallInfo, CallSite> _callSites = new Dictionary<CallInfo, CallSite>();

        /// <summary>
        /// in <see cref="Invoke"/> we have code for calling methods with up to 5 arguments
        /// if we have more than 5 arguments we generate code for the invocation of CallSite and cache it here
        /// </summary>
        private static readonly ConcurrentDictionary<Type, Func<CallSite, object, object[], object>> CallSiteTrampolinesInstance
            = new ConcurrentDictionary<Type, Func<CallSite, object, object[], object>>();

        public CallSiteBinder() {
        }

        /// <summary>
        /// Whether to allow Python threads during CLR evaluation
        /// </summary>
        public bool allow_threads { get; set; }

        private CallSite CallSite(string name, int nargs, string[] argNames) {
            var callInfo = new CallInfo(nargs, argNames);

            CallSite callSite;
            if (_callSites.TryGetValue(callInfo, out callSite))
                return callSite;

            var argTypes = new Type[nargs + 3];
            argTypes[0] = typeof(CallSite); // special first callsite arg
            argTypes[1] = typeof(object); // target instance
            for (var i = 0; i < nargs; i++)
            {
                argTypes[i + 2] = typeof(object);
            }
            argTypes[nargs + 2] = typeof(object); // return type

            callSite = _callSites[callInfo] = System.Runtime.CompilerServices.CallSite.Create(
                Expression.GetFuncType(argTypes),
                new PythonInvokeMemberBinder(name, callInfo)
            );

            return callSite;
        }

        /// <summary>
        /// Invoke member <paramref name="name"/> using <paramref name="memberToInvoke"/>
        /// </summary>
        /// <param name="memberToInvoke">should be
        /// a <see cref="Microsoft.Scripting.Actions.BoundMemberTracker"/> for instance calls
        /// or a <see cref="Microsoft.Scripting.Actions.MethodGroup"/> for static calls</param>
        public IntPtr Invoke(MemberTracker memberToInvoke, string name, IntPtr args, IntPtr kw, Type[] argTypes) {
            try
            {
                bool delayedConversion;

                // convert all Python arguments to CLR types
                var clrArgs = ClrArgs(args, kw, argTypes, out delayedConversion);
                if (clrArgs == null)
                    return IntPtr.Zero;

                var nargs = clrArgs.Length;

                string[] argNames = new string[0];
                if (kw != IntPtr.Zero)
                {
                    var numKeys = Runtime.PyDict_Size(kw);
                    var kwKeys = Runtime.PyDict_Keys(kw);
                    argNames = new string[numKeys];
                    for (var k = 0; k < numKeys; k++)
                    {
                        var kOp = Runtime.PyList_GetItem(kwKeys, k);
                        argNames[k] = Runtime.GetManagedString(kOp);
                    }
                }

                IntPtr ts = IntPtr.Zero;
                if (allow_threads && !delayedConversion)
                {
                    ts = PythonEngine.BeginAllowThreads();
                }

                object retVal;
                try
                {
                    // get or construct the callSite
                    var callSite = CallSite(name, nargs, argNames);

                    // dispatch
                    switch (nargs)
                    {
                        case 0:
                            retVal = ((CallSite<Func<CallSite, object, object>>) callSite)
                                .Target(callSite, memberToInvoke);
                            break;
                        case 1:
                            retVal = ((CallSite<Func<CallSite, object, object, object>>) callSite)
                                .Target(callSite, memberToInvoke, clrArgs[0]);
                            break;
                        case 2:
                            retVal = ((CallSite<Func<CallSite, object, object, object, object>>) callSite)
                                .Target(callSite, memberToInvoke, clrArgs[0], clrArgs[1]);
                            break;
                        case 3:
                            retVal = ((CallSite<Func<CallSite, object, object, object, object, object>>) callSite)
                                .Target(callSite, memberToInvoke, clrArgs[0], clrArgs[1], clrArgs[2]);
                            break;
                        case 4:
                            retVal = ((CallSite<Func<CallSite, object, object, object, object, object, object>>)
                                    callSite)
                                .Target(callSite, memberToInvoke, clrArgs[0], clrArgs[1], clrArgs[2], clrArgs[3]);
                            break;
                        case 5:
                            retVal = ((CallSite<Func<CallSite, object, object, object, object, object, object, object>>)
                                    callSite)
                                .Target(callSite, memberToInvoke, clrArgs[0], clrArgs[1], clrArgs[2], clrArgs[3],
                                    clrArgs[4]);
                            break;
                        default:
                            // to deal with an arbitrary callSite type we build a "trampoline" to invoke callSite
                            var callSiteType = callSite.GetType();
                            var callSiteInvoke = CallSiteTrampolinesInstance.GetOrAdd(callSiteType, callSiteType2 =>
                            {
                                // parameters
                                var callSiteParam = Expression.Parameter(typeof(CallSite), "callSite");
                                var memberToInvokeParam = Expression.Parameter(typeof(object), "type");
                                var clrArgsParam = Expression.Parameter(typeof(object[]), "clrArgs");
                                // get target delegate from callSite
                                var callSiteCasted = Expression.Convert(callSiteParam, callSiteType2);
                                var targetFieldInfo = callSiteType2.GetField("Target");
                                var targetDelegate = Expression.Field(callSiteCasted, targetFieldInfo);
                                // invoke targetDelegate with clrArgs
                                var targetDelegateArgs = new List<Expression> {callSiteParam, memberToInvokeParam};
                                for (var i = 0; i < nargs; i++)
                                {
                                    targetDelegateArgs.Add(Expression.ArrayIndex(clrArgsParam, Expression.Constant(i)));
                                }
                                var invokeExpression = Expression.Invoke(targetDelegate, targetDelegateArgs);
                                // now compile this
                                return Expression.Lambda<Func<CallSite, object, object[], object>>(
                                    invokeExpression,
                                    new List<ParameterExpression> {callSiteParam, memberToInvokeParam, clrArgsParam}
                                ).Compile();
                            });
                            retVal = callSiteInvoke(callSite, memberToInvoke, clrArgs);
                            break;
                    }
                }
                finally
                {
                    if (allow_threads && !delayedConversion)
                    {
                        PythonEngine.EndAllowThreads(ts);
                    }
                }

                // if we called a function which contains out/ref arguments, convert the returned value to a python tuple
                var refReturnBox = retVal as RefReturnBox;
                if (refReturnBox != null)
                {
                    return refReturnBox.ToPythonTuple();
                }

                // otherwise use standard conversions
                return Converter.ToPython(retVal);
            }
            catch (ArgumentTypeException e)
            {
                Exceptions.SetError(Exceptions.TypeError, e.Message);
                return IntPtr.Zero;
            }
            catch (Exception e)
            {
                Exceptions.SetError(e);
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// Construct CLR args from Python <paramref name="args"/>
        /// </summary>
        /// <param name="args">Python tuple with function call arguments</param>
        /// <param name="kw">Python dictionary with named arguments</param>
        /// <param name="argTypes">types the args should be converted to</param>
        /// <param name="delayedConversion">if conversion is delayed, will be set to false</param>
        /// <returns>null if an error occured</returns>
        private static object[] ClrArgs(IntPtr args, IntPtr kw, Type[] argTypes, out bool delayedConversion) {
            var nargs = Runtime.PyTuple_Size(args);

            var nkw = 0;
            var kwValues = IntPtr.Zero;
            if (kw != IntPtr.Zero)
            {
                nkw = Runtime.PyDict_Size(kw);
                kwValues = Runtime.PyDict_Values(kw);
            }

            var clrArgs = new object[nargs + nkw];

            delayedConversion = false;

            for (var n = 0; n < nargs + nkw; n++)
            {
                IntPtr op;
                if (n < nargs)
                    op = Runtime.PyTuple_GetItem(args, n);
                else
                    op = Runtime.PyList_GetItem(kwValues, n - nargs);
                if (op != IntPtr.Zero)
                {
                    var pyoptype = Runtime.PyObject_Type(op);
                    if (pyoptype != IntPtr.Zero)
                    {
                        if (pyoptype == Runtime.PyNoneType)
                        {
                            clrArgs[n] = null;
                        }
                        else
                        {
                            // if specific overload was selected use its type
                            Type clrtype;
                            if (argTypes != null && n < argTypes.Length)
                                clrtype = argTypes[n];
                            else
                            {
#if (PYTHON3)
                                if (pyoptype == Runtime.PyIntType) {
                                    // should convert to int/long depending on size
                                    clrtype = typeof(Int32);
                                    var l = Runtime.PyLong_AsLong(op);
                                    if (l == -1 && Exceptions.ExceptionMatches(Exceptions.OverflowError))
                                    {
                                        Exceptions.Clear();
                                        clrtype = typeof(Int64);
                                    }
                                }
                                else
#endif
                                {
                                    clrtype = Converter.GetTypeByAlias(pyoptype);
                                }
                            }

                            if (clrtype == null && Runtime.PySequence_Check(op))
                            {
                                // box it and postpone decision
                                clrArgs[n] = new PythonSequenceArgBox(op);
                                // do not allow other threads to run in this case
                                delayedConversion = false;
                            }
                            else if (clrtype != null)
                            {
                                object clrArg = null;
                                if (Converter.ToManaged(op, clrtype, out clrArg, false))
                                {
                                    clrArgs[n] = clrArg;
                                }
                                else
                                {
                                    Exceptions.SetError(Exceptions.TypeError,
                                        "cannot convert argument " + n + " to " + clrtype);
                                    return null;
                                }
                            }
                            else
                            {
                                var clrArg = ManagedType.GetManagedObject(op);
                                var clrObject = clrArg as CLRObject;
                                if (clrObject != null)
                                {
                                    clrArgs[n] = clrObject.inst;
                                }
                                else
                                {
                                    var classBase = clrArg as ClassBase;
                                    if (classBase != null)
                                    {
                                        clrArgs[n] = classBase.type;
                                    }
                                    else
                                    {
                                        Exceptions.SetError(Exceptions.TypeError,
                                            "cannot convert argument " + n + " to CLR object");
                                        return null;
                                    }
                                }
                            }
                        }
                        Runtime.XDecref(pyoptype);
                    }
                }
            }
            return clrArgs;
        }
    }
}
