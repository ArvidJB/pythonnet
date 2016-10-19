using System;
using System.Linq;
using System.Reflection;
using Microsoft.Scripting.Actions;

namespace Python.Runtime
{
    /// <summary>
    /// Module level functions
    /// </summary>
    internal class ModuleFunctionObject : MethodObject
    {
        private readonly MemberTracker _memberTracker;

        public ModuleFunctionObject(Type type, string name, MethodInfo info, bool allow_threads)
            : base(type, name, allow_threads) {
            _memberTracker = MemberTracker.FromMemberInfo(info);
        }

        /// <summary>
        /// __call__ implementation.
        /// </summary>
        public static IntPtr tp_call(IntPtr ob, IntPtr args, IntPtr kw)
        {
            var self = (ModuleFunctionObject)GetManagedObject(ob);
            try {
                return self.binder.Invoke(self._memberTracker, self.name, args, kw, null);
            }
            catch (Exception e) {
                Exceptions.SetError(Exceptions.TypeError, e.Message);
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// __repr__ implementation.
        /// </summary>
        public new static IntPtr tp_repr(IntPtr ob)
        {
            var self = (ModuleFunctionObject)GetManagedObject(ob);
            return Runtime.PyString_FromString($"<CLRModuleFunction '{self.name}'>");
        }
    }
}
