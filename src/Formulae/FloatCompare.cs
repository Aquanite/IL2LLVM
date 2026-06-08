using System.Reflection.Metadata;
using IL2LLVM.Compiler;

namespace IL2LLVM.Formulae
{
    public class FloatCompare( 
        LLVMComparison predicate,
        string type,
        string op1,
        string op2,
        string returnValue
    ) : Formula
    {
        private readonly LLVMComparison _predicate = predicate;
        private readonly string _type = type;
        private readonly string _op1 = op1;
        private readonly string _op2 = op2;
        private readonly string _returnValue = returnValue; 

        public override string Formulate()
        {
            return $"    {_returnValue} = fcmp {_predicate.Flatten()} {_type} {_op1}, {_op2}";
        }

        public static string Formulate(
            LLVMComparison predicate,
            string type,
            string op1,
            string op2,
            string returnValue
        )
        {
            return $"    {returnValue} = fcmp {predicate.Flatten()} {type} {op1}, {op2}";
        }
    }
}