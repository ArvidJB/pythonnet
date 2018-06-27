using System;
using Microsoft.Scripting;

namespace Python.Runtime
{
    internal class PythonSequenceArgBox
    {
        private readonly IntPtr _op;

        public PythonSequenceArgBox(IntPtr op) {
            _op = op;
        }

        public object ToArray(Type elementType) {
            object result;
            if (!Converter.ToArray(_op, elementType, out result, false)) {
                var ob = IntPtr.Zero;
                var val = IntPtr.Zero;
                var tb = IntPtr.Zero;
                Runtime.PyErr_Fetch(ref ob, ref val, ref tb);
                var message = "unknown sequence conversion error";
                if (val != IntPtr.Zero) {
                    var strval = Runtime.PyObject_Unicode(val);
                    message = Runtime.GetManagedString(strval);
                    Runtime.XDecref(strval);
                }
                throw new ArgumentTypeException(message);
            }
            return result;
        }
    }
}
