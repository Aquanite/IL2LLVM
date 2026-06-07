using System.Reflection.Metadata;
using IL2LLVM.Compiler;

namespace IL2LLVM.Formulae
{
    public class Compare( 
        LLVMComparison predicate,
        string type,
        string op1,
        string op2,
        string returnValue,
        bool isFloat = false
    ) : Formula
    {
        private LLVMComparison _predicate = predicate;
        private string _type = type;
        private string _op1 = op1;
        private string _op2 = op2;
        private string _returnValue = returnValue;
        private bool _isFloat = isFloat;

        public override string Formulate()
        {
            return _isFloat
                ? FloatCompare.Formulate(_predicate, _type, _op1, _op2, _returnValue)
                : IntegerCompare.Formulate(_predicate, _type, _op1, _op2, _returnValue);
        }

        public static string Formulate(
            LLVMComparison predicate,
            string type,
            string op1,
            string op2,
            string returnValue,
            bool isFloat = false
        )
        {
            return isFloat
                ? FloatCompare.Formulate(predicate, type, op1, op2, returnValue)
                : IntegerCompare.Formulate(predicate, type, op1, op2, returnValue);
        }
    }
}