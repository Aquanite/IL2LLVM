using System.Reflection.Metadata;

namespace IL2LLVM.Formulae
{
    public class Call( 
        string returnType,
        string name,
        string[] parameters,
        string toSet = "",
        bool isWindowsNative = false
    ) : Formula
    {
        private readonly string _returnType = returnType;
        private readonly string _name = name;
        private readonly string _toSet = toSet;
        private readonly string[] _parameters = parameters;
        private readonly bool _isWindowsNative = isWindowsNative;

        public override string Formulate()
        {
            if (_returnType == "void")
                return $"    call {(_isWindowsNative ? "x86_stdcallcc " : "")}void @{_name}({string.Join(", ", _parameters)})";
            else
                return $"    {_toSet} = call {(_isWindowsNative ? "x86_stdcallcc " : "")}{_returnType} @{_name}({string.Join(", ", _parameters)})";
        }

        public static string Formulate(
            string returnType,
            string name,
            string[] parameters,
            string toSet = "",
            bool isWindowsNative = false
        )
        {
            if (returnType == "void")
                return $"    call {(isWindowsNative ? "x86_stdcallcc " : "")}void @{name}({string.Join(", ", parameters)})";
            else
                return $"    {toSet} = call {(isWindowsNative ? "x86_stdcallcc " : "")}{returnType} @{name}({string.Join(", ", parameters)})";
        }
    }
}