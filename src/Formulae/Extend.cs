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
        private bool _isUnsigned = isUnsigned;
        private string _oldType = oldType;
        private string _value = value;
        private string _newType = newType;
        private string _returnValue = returnValue;

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