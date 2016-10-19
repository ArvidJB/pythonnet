using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Scripting.Actions;

namespace Python.Runtime
{
    /// <summary>
    /// Implements a Python type that represents a CLR method. Method objects
    /// support a subscript syntax [] to allow explicit overload selection.
    /// </summary>
    /// <remarks>
    /// TODO: ForbidPythonThreadsAttribute per method info
    /// </remarks>
    internal class MethodObject : ExtensionType
    {
        internal string name;
        internal MethodBinding unbound;
        internal CallSiteBinder binder;
        internal IntPtr doc;
        internal Type type;

        public MethodObject(Type type, string name) : this(type, name, true) {
        }

        public MethodObject(Type type, string name, bool allow_threads)
        {
            this.type = type;
            this.name = name;
            binder = new CallSiteBinder() { allow_threads = allow_threads};
        }


        public virtual IntPtr Invoke(IntPtr inst, IntPtr args, IntPtr kw, Type[] argTypes, CallSite<Func<CallSite, object, object>> memberCallsite)
        {
            object clrTarget = null;

            if (inst == IntPtr.Zero) {
                clrTarget = new NestedTypeTracker(type);
            }
            else {
                var managedObject = ManagedType.GetManagedObject(inst);

        var clrObject = managedObject as CLRObject;
                if (clrObject != null) {
                    clrTarget = clrObject.inst;
                }
                else {
                    var classBase = managedObject as ClassBase;
                    if (classBase != null) {
                        clrTarget = classBase.type;
                    }
                    else {
                        Exceptions.SetError(Exceptions.TypeError, "Cannot determine target");
                        return IntPtr .Zero;
                    }
                }
            }

                try
{
            var memberToInvoke = (MemberTracker)memberCallsite.Target(memberCallsite, clrTarget);

            return binder.Invoke( memberToInvoke , name, args, kw, argTypes);
                }
                catch (Exception e)
                {
                    Exceptions.SetError(Exceptions.TypeError, e.Message);
                    return IntPtr.Zero;
                }
            }

        /// <summary>
        /// Helper to get docstrings from reflected method / param info.
        /// </summary>
        internal IntPtr GetDocString()
        {
            if (doc != IntPtr.Zero)
            {
                return doc;
            }
            var str = "";
            Type marker = typeof(DocStringAttribute);
            var methods = type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var method in methods)
            {
                if (method.Name == name)
                {if (str.Length > 0)
                {    str += Environment.NewLine;
                }
                var attrs = (Attribute[])method.GetCustomAttributes(marker, false);
                if (attrs.Length == 0)
                {
                    str += method.ToString();
                }
                else
                {
                    var attr = (DocStringAttribute)attrs[0];
                    str += attr.DocString;}
                }
            }
            doc = Runtime.PyString_FromString(str);
            return doc;
        }


        /// <summary>
        /// Descriptor __getattribute__ implementation.
        /// </summary>
        public static IntPtr tp_getattro(IntPtr ob, IntPtr key)
        {
            var self = (MethodObject)GetManagedObject(ob);

            if (!Runtime.PyString_Check(key))
            {
                return Exceptions.RaiseTypeError("string expected");
            }

            string name = Runtime.GetManagedString(key);
            if (name == "__doc__")
            {
                IntPtr doc = self.GetDocString();
                Runtime.XIncref(doc);
                return doc;
            }

            return Runtime.PyObject_GenericGetAttr(ob, key);
        }

        /// <summary>
        /// Descriptor __get__ implementation. Accessing a CLR method returns
        /// a "bound" method similar to a Python bound method.
        /// </summary>
        public static IntPtr tp_descr_get(IntPtr ds, IntPtr ob, IntPtr tp)
        {
            var self = (MethodObject)GetManagedObject(ds);
            MethodBinding binding;

            // If the method is accessed through its type (rather than via
            // an instance) we return an 'unbound' MethodBinding that will
            // cached for future accesses through the type.

            if (ob == IntPtr.Zero)
            {
                if (self.unbound == null)
                {
                    self.unbound = new MethodBinding(self, IntPtr.Zero, tp);
                }
                binding = self.unbound;
                Runtime.XIncref(binding.pyHandle);
                ;
                return binding.pyHandle;
            }

            if (Runtime.PyObject_IsInstance(ob, tp) < 1)
            {
                return Exceptions.RaiseTypeError("invalid argument");
            }

            // If the object this descriptor is being called with is a subclass of the type
            // this descriptor was defined on then it will be because the base class method
            // is being called via super(Derived, self).method(...).
            // In which case create a MethodBinding bound to the base class.
            var obj = GetManagedObject(ob) as CLRObject;
            if (obj != null
                && obj.inst.GetType() != self.type
                && obj.inst is IPythonDerivedType
                && self.type.IsInstanceOfType(obj.inst))
            {
                ClassBase basecls = ClassManager.GetClass(self.type);
                binding = new MethodBinding(self, ob, basecls.pyHandle);
                return binding.pyHandle;
            }

            binding = new MethodBinding(self, ob, tp);
            return binding.pyHandle;
        }

        /// <summary>
        /// Descriptor __repr__ implementation.
        /// </summary>
        public static IntPtr tp_repr(IntPtr ob)
        {
            var self = (MethodObject)GetManagedObject(ob);
            return Runtime.PyString_FromString($"<method '{self.name}'>");
        }

        /// <summary>
        /// Descriptor dealloc implementation.
        /// </summary>
        public new static void tp_dealloc(IntPtr ob)
        {
            var self = (MethodObject)GetManagedObject(ob);
            Runtime.XDecref(self.doc);
            if (self.unbound != null)
            {
                Runtime.XDecref(self.unbound.pyHandle);
            }
            ExtensionType.FinalizeObject(self);
        }
    }
}
