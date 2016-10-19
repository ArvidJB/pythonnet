using System;
using System.Dynamic;
using System.Runtime.CompilerServices;

namespace Python.Runtime
{
    internal class PythonGetMemberBinder : DynamicMetaObjectBinder
    {
        private readonly string _name;
        private readonly Type[] _genericParameterTypes;
        private readonly Type[] _argTypes;

        public PythonGetMemberBinder(string name) : this(name, null, null) {
        }

        public PythonGetMemberBinder(string name, Type[] genericParameterTypes, Type[] argTypes) : base() {
            _name = name;
            _genericParameterTypes = genericParameterTypes;
            _argTypes = argTypes;
        }

        public DynamicMetaObject FallbackGetMember(DynamicMetaObject target,
            DynamicMetaObject errorSuggestion) {

            var pythonBinder = (_genericParameterTypes != null || _argTypes != null)
                ? PythonBinder.ForOverload(_genericParameterTypes, _argTypes)
                : PythonBinder.Instance;

            var memberToInvokeMetaObject = pythonBinder.GetMember(_name, target);

            return memberToInvokeMetaObject;
        }

        public override DynamicMetaObject Bind(DynamicMetaObject target, DynamicMetaObject[] args) {
            return FallbackGetMember(target, null);
        }
    }
}