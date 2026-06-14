using System.Reflection.Metadata;
using IL2LLVM.Compiler;

namespace IL2LLVM.Formulae
{
    public class IntegerCompare( 
        LLVMComparison predicate,
        string type,
        string op1,
        string op2,
        string returnValue,
        bool isUnsigned = false
    ) : Formula
    {
        private readonly LLVMComparison _predicate = predicate;
        private readonly string _type = type;
        private readonly string _op1 = op1;
        private readonly string _op2 = op2;
        private readonly string _returnValue = returnValue; 
        private readonly bool _isUnsigned = isUnsigned;

        public override string Formulate()
        {
            return $"    {_returnValue} = icmp {_predicate.Flatten(_isUnsigned)} {_type} {_op1}, {_op2}";
        }

        public static string Formulate(
            LLVMComparison predicate,
            string type,
            string op1,
            string op2,
            string returnValue,
            bool isUnsigned = false
        )
        {
            return $"    {returnValue} = icmp {predicate.Flatten(isUnsigned)} {type} {op1}, {op2}";
        }
    }
}