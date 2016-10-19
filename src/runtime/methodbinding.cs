using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Reflection;

namespace Python.Runtime
{
    /// <summary>
    /// Implements a Python binding type for CLR methods. These work much like
    /// standard Python method bindings, but the same type is used to bind
    /// both static and instance methods.
    /// </summary>
    internal class MethodBinding : ExtensionType
    {
        internal MethodObject m;
        internal IntPtr target;
        internal IntPtr targetType;
        private readonly Type[] _genericParameterTypes;
        private readonly Type[] _argTypes;
        private readonly CallSite<Func<CallSite, object, object>> _memberCallsite;

        public MethodBinding(MethodObject m, IntPtr target, IntPtr targetType, Type[] genericParameterTypes, Type[] argTypes)
        {
            Runtime.XIncref(target);
            this.target = target;

            Runtime.XIncref(targetType);
            if (targetType == IntPtr.Zero)
            {
                targetType = Runtime.PyObject_Type(target);
            }
            this.targetType = targetType;

            this.m = m;

            _genericParameterTypes = genericParameterTypes;
            _argTypes = argTypes;

            _memberCallsite = CallSite<Func<CallSite, object, object>>.Create(new PythonGetMemberBinder(m.name, _genericParameterTypes, _argTypes));
        }

        public MethodBinding(MethodObject m, IntPtr target, IntPtr targetType) : this(m, target, targetType, null, null)
        {
        }

        public MethodBinding(MethodObject m, IntPtr target) : this(m, target, IntPtr.Zero, null, null)
        {
        }

        public MethodBinding(MethodObject m, IntPtr target, Type[] genericParameterTypes, Type[] argTypes)
            : this(m, target, IntPtr.Zero, genericParameterTypes, argTypes)
        {
        }

        /// <summary>
        /// Implement binding of generic methods using the subscript syntax [].
        /// </summary>
        public static IntPtr mp_subscript(IntPtr tp, IntPtr idx)
        {
            var self = (MethodBinding)GetManagedObject(tp);

            Type[] types = Runtime.PythonArgsToTypeArray(idx);
            if (types == null)
            {
                return Exceptions.RaiseTypeError("type(s) expected");
            }

            MethodBinding mb = new MethodBinding(self.m, self.target, genericParameterTypes: types, argTypes: self._argTypes);
            Runtime.XIncref(mb.pyHandle);
            return mb.pyHandle;
        }


        /// <summary>
        /// MethodBinding __getattribute__ implementation.
        /// </summary>
        public static IntPtr tp_getattro(IntPtr ob, IntPtr key)
        {
            var self = (MethodBinding)GetManagedObject(ob);

            if (!Runtime.PyString_Check(key))
            {
                Exceptions.SetError(Exceptions.TypeError, "string expected");
                return IntPtr.Zero;
            }

            string name = Runtime.GetManagedString(key);
            switch (name)
            {
                case "__doc__":
                    IntPtr doc = self.m.GetDocString();
                    Runtime.XIncref(doc);
                    return doc;
                // FIXME: deprecate __overloads__ soon...
                case "__overloads__":
                case "Overloads":
                    var om = new OverloadMapper(self.m, self.target);
                    Runtime.XIncref(om.pyHandle);
                    return om.pyHandle;
                default:
                    return Runtime.PyObject_GenericGetAttr(ob, key);
            }
        }


        /// <summary>
        /// MethodBinding  __call__ implementation.
        /// </summary>
        public static IntPtr tp_call(IntPtr ob, IntPtr args, IntPtr kw)
        {
            var self = (MethodBinding)GetManagedObject(ob);

            return self.m.Invoke(self.target, args, kw, self._argTypes, self._memberCallsite);
        }


        /// <summary>
        /// MethodBinding  __hash__ implementation.
        /// </summary>
        public static IntPtr tp_hash(IntPtr ob)
        {
            var self = (MethodBinding)GetManagedObject(ob);
            long x = 0;
            long y = 0;

            if (self.target != IntPtr.Zero)
            {
                x = Runtime.PyObject_Hash(self.target).ToInt64();
                if (x == -1)
                {
                    return new IntPtr(-1);
                }
            }

            y = Runtime.PyObject_Hash(self.m.pyHandle).ToInt64();
            if (y == -1)
            {
                return new IntPtr(-1);
            }

            x ^= y;

            if (x == -1)
            {
                x = -1;
            }

            return new IntPtr(x);
        }

        /// <summary>
        /// MethodBinding  __repr__ implementation.
        /// </summary>
        public static IntPtr tp_repr(IntPtr ob)
        {
            var self = (MethodBinding)GetManagedObject(ob);
            string type = self.target == IntPtr.Zero ? "unbound" : "bound";
            string name = self.m.name;
            return Runtime.PyString_FromString($"<{type} method '{name}'>");
        }

        /// <summary>
        /// MethodBinding dealloc implementation.
        /// </summary>
        public new static void tp_dealloc(IntPtr ob)
        {
            var self = (MethodBinding)GetManagedObject(ob);
            Runtime.XDecref(self.target);
            Runtime.XDecref(self.targetType);
            FinalizeObject(self);
        }
    }
}
