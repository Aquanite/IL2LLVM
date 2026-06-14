using System.Reflection.Metadata;
using IL2LLVM.Compiler;
using Mono.Cecil;

namespace IL2LLVM.Formulae
{
    public class Call( 
        string returnType,
        string name,
        string[] parameters,
        string toSet = "",
        MethodCallingConvention callingConvention = MethodCallingConvention.Default,
        bool isIndirect = false
    ) : Formula
    {
        private readonly string _returnType = returnType;
        private readonly string _name = name;
        private readonly string _toSet = toSet;
        private readonly string[] _parameters = parameters;
        private readonly MethodCallingConvention _callingConvention = callingConvention;
        private readonly bool _isIndirect = isIndirect;

        public override string Formulate()
        {
            if (_returnType == "void")
                return $"    call {Utility.GetLLVMConvention(_callingConvention)} void {(_isIndirect ? _name : '@' + _name)}({string.Join(", ", _parameters)})";
            else
                return $"    {_toSet} = call {Utility.GetLLVMConvention(_callingConvention)} {_returnType} {(_isIndirect ? _name : '@' + _name)}({string.Join(", ", _parameters)})";
        }

        public static string Formulate(
            string returnType,
            string name,
            string[] parameters,
            string toSet = "",
            MethodCallingConvention callingConvention = MethodCallingConvention.Default,
            bool isIndirect = false
        )
        {
            if (returnType == "void")
                return $"    call {Utility.GetLLVMConvention(callingConvention)} void {(isIndirect ? name : '@' + name)}({string.Join(", ", parameters)})";
            else
                return $"    {toSet} = call {Utility.GetLLVMConvention(callingConvention)} {returnType} {(isIndirect ? name : '@' + name)}({string.Join(", ", parameters)})";
        }
    }
}