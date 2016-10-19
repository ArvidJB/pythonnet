using System;
using System.Dynamic;
using System.Linq.Expressions;

namespace Python.Runtime
{
    internal class PythonReturnConvertBinder : ConvertBinder {
        public PythonReturnConvertBinder() : base(typeof(object), false) {
        }

        public override DynamicMetaObject FallbackConvert(DynamicMetaObject target, DynamicMetaObject errorSuggestion) {
            Expression convertExpr;
            if (target.LimitType == typeof(void))
                convertExpr = Expression.Block(target.Expression, Expression.Constant(null, Type));
            else
                convertExpr = Expression.Convert(target.Expression, Type);
            return new DynamicMetaObject(
                convertExpr,
                BindingRestrictions.GetTypeRestriction(target.Expression, target.LimitType)
            );
        }
    }
}