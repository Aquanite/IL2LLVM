using System.Collections.Frozen;
using Mono.Cecil;

namespace IL2LLVM.Compiler
{
    public static class Mangler
    {
        private static readonly FrozenDictionary<string, string> knownSignatures = new Dictionary<string, string>
        {
            { "System.String",  "st" },
            { "System.Char",    "cr" },
            { "System.SByte",   "sb" },
            { "System.Byte",    "ub" },
            { "System.Int16",   "ss" },
            { "System.UInt16",  "us" },
            { "System.Int32",   "si" },
            { "System.UInt32",  "ui" },
            { "System.Int64",   "sl" },
            { "System.UInt64",  "ul" },
            { "System.Single",  "sf" },
            { "System.Double",  "df" },
            { "System.Decimal", "dc" },
            { "System.Boolean", "bl" },
            { "System.Void",    "vo" },
            { "System.Object",  "ob" },
            { "System.IntPtr",  "ip" },
            { "System.UIntPtr", "up" },
        }.ToFrozenDictionary();

        private static Dictionary<string, string> nativeCalls = new Dictionary<string, string>();

        public static void AddNativeCall(string before, string after) => nativeCalls.Add(before, after);

        public static string Mangle(FieldDefinition field)
        {
            string fieldName = field.DeclaringType.FullName + ".f" + field.Name;
            fieldName = fieldName.Replace("`", ".bt."); // Sanitize generics
            return fieldName;
        }

        public static string Mangle(MethodReference method)
        {
            if (method is MethodDefinition def)
            {
                return Mangle(def);
            }

            return Mangle(method.DeclaringType.FullName, method.Name, method.Parameters);
        }

        public static string Mangle(MethodDefinition method)
        {
            if (nativeCalls.TryGetValue(method.FullName, out string? nativeName) && !string.IsNullOrEmpty(nativeName))
            {
                return nativeName; // No mangling
            }
            return Mangle(method.DeclaringType.FullName, method.Name, method.Parameters);
        }

        private static string Mangle(string declaringTypeName, string methodName, IEnumerable<ParameterDefinition> parameters)
        {

            methodName = declaringTypeName + "." + methodName;
            methodName = methodName.Replace("`", ".bt."); // Sanitize generics

            var parameterList = parameters.ToArray();
            if (parameterList.Length == 0)
            {
                return methodName;
            }

            methodName += "_P";

            foreach (ParameterDefinition parameter in parameterList)
            {
                methodName += EncodeType(parameter.ParameterType);
                methodName += 'E';
            }

            return methodName;
        }

        private static string EncodeType(TypeReference type)
        {
            string local = type.FullName;
            int startCollection = local.IndexOfAny(['*', '[', '&']);
            string collectionElements = string.Empty;

            if (startCollection != -1)
            {
                collectionElements = local.Substring(startCollection);
                local = local.Substring(0, startCollection);
            }

            string encoded = knownSignatures.TryGetValue(local, out string? sig) ? sig : local;

            if (startCollection != -1)
            {
                foreach (char c in collectionElements)
                {
                    encoded += c switch
                    {
                        '[' => 'A',
                        '*' => 'P',
                        '&' => 'R',
                        _ => ""
                    };
                }
            }

            return encoded;
        }
    }
}
