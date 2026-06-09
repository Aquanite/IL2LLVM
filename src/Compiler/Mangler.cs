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

        private static readonly Dictionary<string, string> nativeCalls = [];
        private static readonly Dictionary<string, MethodDefinition> plugReference = [];

        public static void AddNativeCall(string before, string after) => nativeCalls.Add(before, after);
        public static void AddPlugReference(string toPlug, MethodDefinition toMangle) => plugReference.Add(toPlug, toMangle);
        public static MethodDefinition? GetPlugReference(string toPlug)
        {
            if (plugReference.TryGetValue(toPlug, out MethodDefinition? value))
                return value;
            else return null;
        }

        public static string Mangle(FieldDefinition field)
        {
            string fieldName = field.DeclaringType.FullName + ".f" + field.Name;
            fieldName = fieldName.Replace("`", ".bt."); // Sanitize generics
            return fieldName;
        }

        public static string Mangle(MethodReference method)
        {
            try
            {
                var def = method.Resolve();
                return Mangle(def);
            }
            catch (Exception) {}

            if (plugReference.TryGetValue(method.FullName, out MethodDefinition? plugName) && plugName != null)
            {
                return Mangle(plugName);
            }

            // Assume no native calls since we don't have the module

            return Mangle(method.DeclaringType.FullName, method.Name, method.Parameters);
        }

        public static string Mangle(MethodDefinition method)
        {
            if (nativeCalls.TryGetValue(method.FullName, out string? nativeName) && !string.IsNullOrEmpty(nativeName))
            {
                return nativeName; // No mangling
            }

            if (plugReference.TryGetValue(method.FullName, out MethodDefinition? plugName) && plugName != null)
            {
                if (plugName == method)
                    throw new InvalidOperationException($"Mangling failed for '{method.FullName}' as `plugName` is the target of `method`.");

                return Mangle(plugName);
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
