using System.Reflection.Metadata;
using IL2LLVM.Compiler;

namespace IL2LLVM.Formulae
{
    public class Extend( 
        bool isUnsigned,
        string oldType,
        string value,
        string newType,
        string returnValue
    ) : Formula
    {
        private readonly bool _isUnsigned = isUnsigned;
        private readonly string _oldType = oldType;
        private readonly string _value = value;
        private readonly string _newType = newType;
        private readonly string _returnValue = returnValue;

        public override string Formulate()
        {
            return $"    {_returnValue} = {(_isUnsigned ? "zext" : "sext")} {_oldType} {_value} to {_newType}";
        }

        public static string Formulate(
            bool isUnsigned,
            string oldType,
            string value,
            string newType,
            string returnValue
        )
        {
            return $"    {returnValue} = {(isUnsigned ? "zext" : "sext")} {oldType} {value} to {newType}";
        }
    }
}