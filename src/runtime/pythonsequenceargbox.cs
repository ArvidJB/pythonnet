using System;

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
            Converter.ToArray(_op, elementType, out result, false);
            return result;
        }
    }
}