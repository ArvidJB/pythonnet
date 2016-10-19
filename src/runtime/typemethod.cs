using System;
using System.Reflection;

namespace Python.Runtime
{
    /// <summary>
    /// Implements a Python type that provides access to CLR object methods.
    /// </summary>
    internal class TypeMethod : MethodObject
    {
        private readonly MethodInfo _info;

        public TypeMethod(Type type, string name, MethodInfo info) :
            base(type, name)
        {
            _info = info;
        }

        public TypeMethod(Type type, string name, MethodInfo info, bool allow_threads) :
            base(type, name, allow_threads)
        {
            _info = info;
        }

        public IntPtr Invoke(IntPtr ob, IntPtr args, IntPtr kw)
        {
            var arglist = new object[3];
            arglist[0] = ob;
            arglist[1] = args;
            arglist[2] = kw;

            try
            {
                object inst = null;
                if (ob != IntPtr.Zero)
                {
                    inst = GetManagedObject(ob);
                }
                return (IntPtr)_info.Invoke(inst, BindingFlags.Default, null, arglist, null);
            }
            catch (Exception e)
            {
                Exceptions.SetError(e);
                return IntPtr.Zero;
            }
        }
    }
}
