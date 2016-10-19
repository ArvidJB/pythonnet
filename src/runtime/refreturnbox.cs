using System;

namespace Python.Runtime
{
    /// <summary>
    /// Wrapper for the returned value(s) of a "by-ref" method
    /// </summary>
    internal class RefReturnBox {
        private readonly object[] _objs;

        public RefReturnBox(object[] objs) {
            _objs = objs;
        }

        public IntPtr ToPythonTuple() {
            var count = _objs.Length;
            var tuple = Runtime.PyTuple_New(count);
            int i = 0;
            foreach (var o in _objs)
            {
                IntPtr ptr = Converter.ToPython(o, o?.GetType());
                Runtime.XIncref(ptr);
                if (Runtime.PyTuple_SetItem(tuple, i++, ptr) < 0)
                {
                    throw new PythonException();
                }
            }

            Runtime.XIncref(tuple);
            return tuple;
        }
    }
}