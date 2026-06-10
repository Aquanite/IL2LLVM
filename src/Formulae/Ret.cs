using System.Reflection.Metadata;
using IL2LLVM.Compiler;

namespace IL2LLVM.Formulae
{
    public class Return( 
        string type,
        string value = ""
    ) : Formula
    {
        private readonly string _type = type;
        private readonly string _value = value;

        public override string Formulate()
        {
            return $"    ret {_type} {(_type != "void" ? _value : "")}";
        }

        public static string Formulate(
            string type,
            string value = ""
        )
        {
            return $"    ret {type} {(type != "void" ? value : "")}";
        }
    }
}