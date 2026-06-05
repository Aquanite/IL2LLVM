using System.Collections.Frozen;
using System.Reflection.PortableExecutable;
using System.Text;
using Microsoft.VisualBasic;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IL2LLVM.Compiler
{
    public record LLVMObject
    {
        public string Value {
            get {
                if (field == "0" && Type == "ptr")
                {
                    return "null";
                }
                else return field;
            } 
            set; 
        }
        public string Type {get; set;}
        public bool isUnsigned {get; set;}

        public LLVMObject(string value, string type, bool isunsigned)
        {
            Value = value;
            Type = type;
            isUnsigned = isunsigned;
        }
    }

    public class Spratcher
    {
        
        private StreamWriter? emitter;
        private ModuleDefinition module;

        private Stack<LLVMObject> analyticalStack = new();
        private LLVMObject[]? localVars;
        private string[]? argTypes; // Names will always be %arg0, %arg1
        private uint tempRegisterCounter = 0;
        private uint stringCounter = 0;
        private bool unicodeStrings = true;
        private byte ptrWidth = 8; // Assume 64-bit 
        private bool nextIsVolatile = false;
        private bool bundleCorelib = false;
        private Dictionary<Instruction, string>? instructionLabels;
        private List<string>? declareLabels;
        private readonly Dictionary<Code, Action<Instruction>> instructionHandlers;

        public Spratcher(ModuleDefinition module, byte ptrWidth, bool bundleCorelib = false, bool unicodeStrings = true)
        {
            this.module = module;
            this.ptrWidth = ptrWidth;
            this.bundleCorelib = bundleCorelib;
            this.unicodeStrings = unicodeStrings;
            instructionHandlers = BuildInstructionHandlers();
            declareLabels = new List<string>();
        }

        private StreamWriter Emitter => emitter ?? throw new InvalidOperationException("Emitter not initialized.");
        private LLVMObject[] LocalVars => localVars ?? throw new InvalidOperationException("Local variables not initialized.");
        private string[] ArgTypes => argTypes ?? throw new InvalidOperationException("Argument types not initialized.");
        private Dictionary<Instruction, string> InstructionLabels => instructionLabels ?? throw new InvalidOperationException("Instruction labels not initialized.");
        private List<string> DeclareLabels => declareLabels ?? throw new InvalidOperationException("Declare labels not initialized.");

        private void EmitCorelibIfNeeded() 
        {
            if (bundleCorelib)
            {
                EmitCorelib();
            }
        }

        private void EmitDeclareLabels()
        {
            Emitter.WriteLine("; DECLARE START");
            foreach (string label in DeclareLabels)
            {
                Emitter.WriteLine(label);
            }
            Emitter.WriteLine("; DECLARE END\n");
        }

        public void Run(string outFile)
        {
            try
            {
                using (StreamWriter write = new(File.Open(outFile, FileMode.Create)))
                {
                    emitter = write;

                    // Get all types in the module
                    var namespaceGroups = module.Types
                        .Where(t => !t.IsNested)
                        .GroupBy(t => t.Namespace);

                    // Precache all native calls
                    foreach (var namespaceGroup in namespaceGroups)
                    {
                        foreach (var type in namespaceGroup)
                        {
                            foreach (var method in type.Methods)
                            {
                                if (IsNativeCall(method))
                                {
                                    string? callName = GetNativeCallName(method);
                                    if (!string.IsNullOrEmpty(callName)) 
                                        Mangler.AddNativeCall(method.FullName, callName);
                                }
                            }
                        }
                    }

                    foreach (var namespaceGroup in namespaceGroups)
                    {
                        string namespaceName = namespaceGroup.Key;
                        Emitter.WriteLine($"; Namespace: {namespaceName}");

                        foreach (var type in namespaceGroup)
                        {
                            string typeName = type.Name;
                            Emitter.WriteLine($"; Type: {typeName}");
                            // declare fields to LLVm

                            foreach (var field in type.Fields)
                            {
                                string fieldName = Mangler.Mangle(field);
                                string fieldType = GetVarType(field.FieldType);
                                Emitter.WriteLine($"@{fieldName} = global {fieldType} 0, align {GetAlignmentForType(fieldType)}");
                            }

                            foreach (var method in type.Methods)
                            {
                                string methodName = method.Name;
                                Emitter.WriteLine($"; Method: {methodName}");

                                if (method.HasBody)
                                {
                                    CompileMethod(method);
                                }
                            }
                        }
                    }

                    EmitDeclareLabels();
                    EmitCorelibIfNeeded();
                    Emitter.Flush();
                }

                Console.WriteLine($"Successfully compiled to {outFile}");
                return;
            }
            catch (Exception e)
            {
                Console.WriteLine("FATAL: " + e.Message + "\n" + e.StackTrace);
                Environment.Exit(-1);
            }
        }

        private void CompileMethod(MethodDefinition method)
        {
            
            // Reset state
            analyticalStack.Clear();
            tempRegisterCounter = 0;
            nextIsVolatile = false;

            bool hasThis = method.HasThis;

            // Setup branch targets
            InitializeInstructionLabels([.. method.Body.Instructions]);

            // Setup local vars
            localVars = new LLVMObject[method.Body.Variables.Count];
            for (int i = 0; i < method.Body.Variables.Count; i++)
            {
                LocalVars[i] = new($"%V_{i}", GetVarType(method.Body.Variables[i].VariableType), false);
            }

            // Setup arg types
            argTypes = new string[method.Parameters.Count + (hasThis ? 1 : 0)];

            if (hasThis)
            {
                ArgTypes[0] = "ptr";
            }

            for (int i = 0; i < method.Parameters.Count; i++)
            {
                ArgTypes[i + (hasThis ? 1 : 0)] = GetVarType(method.Parameters[i].ParameterType);
            }

            // Get return type
            string returnType = GetVarType(method.ReturnType);

            if (IsNativeCall(method))
            {
                string? callName = GetNativeCallName(method);
                if (!string.IsNullOrEmpty(callName))
                {
                    string args = string.Join(",", argTypes);

                    DeclareLabels.Add($"declare {returnType} @{callName}({args})");
                }
                
                return; // No need to compile instructions or headers
            }

            // Emit function header
            string mangledName = Mangler.Mangle(method);
            Emitter.WriteLine($"define {returnType} @{mangledName}({string.Join(", ", ArgTypes.Select((t, i) => $"{t} %arg{i}"))}) {{");

            if (mangledName == "main")
                Emitter.WriteLine("    call void @main.cctor()");

            // Compile method body
            foreach (var instruction in method.Body.Instructions)
            {
                if (InstructionLabels.TryGetValue(instruction, out string? label) && !string.IsNullOrEmpty(label))
                {
                    
                    Emitter.WriteLine($"{label}:");
                }
                CompileInstruction(instruction);
            }

            // Emit function footer
            Emitter.WriteLine("}");
        }

        private void CompileInstruction(Instruction instruction)
        {
            if (instructionHandlers.TryGetValue(instruction.OpCode.Code, out Action<Instruction>? handler))
            {
                handler(instruction);
                return;
            }

            Console.WriteLine($"FATAL: Unsupported instruction: {instruction.OpCode.Code}");
            Environment.Exit(-1);
        }

        private void InitializeInstructionLabels(Instruction[] instructions)
        {
            instructionLabels = new Dictionary<Instruction, string>();
            
            foreach (Instruction ins in instructions)
            {
                if (IsBranchInstruction(ins))
                {
                    if (!IsInstruction(ins.Operand)) throw new InvalidOpcodeException("Invalid branch operand!");

                    Instruction operand = (Instruction)ins.Operand;

                    InstructionLabels.TryAdd(operand, $"IL_{operand.Offset:X4}");
                }
            }

            
        }
 
        private static bool IsBranchInstruction(Instruction ins)
        {
            return ins.OpCode.Code switch
            {
                Code.Br_S => true,
                Code.Br   => true,
                _         => false
            };
        }
        private static bool IsInstruction(object operand) => operand is Instruction;

        private static string? GetNativeCallName(MethodDefinition method)
        {
            // Is it a native call?
            var attr = method.CustomAttributes.FirstOrDefault(a =>
                a.AttributeType.FullName == "IL2LLVM.Attributes.NativeCall");

            return attr is null
                ? null
                : (string)attr.ConstructorArguments[0].Value;
        }

        private static bool IsNativeCall(MethodDefinition method) => GetNativeCallName(method) != null;

        private string ToLLVMUnicodeString(string str)
        {
            byte[] utf16Bytes = System.Text.Encoding.Unicode.GetBytes(str);
            return string.Concat(utf16Bytes.Select(b => $"\\{b:X2}")) + "\\00\\00";
        }

        private string ToLLVMAsciiString(string str)
        {
            string built = "";
            byte[] str_bytes = Encoding.ASCII.GetBytes(str);

            foreach (byte c in str_bytes)
            {
                built += $"\\{c:X2}";
            }

            return built + "\\00";
        }

        private Dictionary<Code, Action<Instruction>> BuildInstructionHandlers()
        {
            return new Dictionary<Code, Action<Instruction>>
            {
                [Code.Nop]          = _ => NOP(),
                [Code.Break]        = _ => BREAK(),
                [Code.Ldarg_0]      = _ => LDARG(0),
                [Code.Ldarg_1]      = _ => LDARG(1),
                [Code.Ldarg_2]      = _ => LDARG(2),
                [Code.Ldarg_3]      = _ => LDARG(3),
                [Code.Ldloc_0]      = _ => LDLOC(0),
                [Code.Ldloc_1]      = _ => LDLOC(1),
                [Code.Ldloc_2]      = _ => LDLOC(2),
                [Code.Ldloc_3]      = _ => LDLOC(3),
                [Code.Stloc_0]      = _ => STLOC(0),
                [Code.Stloc_1]      = _ => STLOC(1),
                [Code.Stloc_2]      = _ => STLOC(2),
                [Code.Stloc_3]      = _ => STLOC(3),
                [Code.Ldarg_S]      = instruction => LDARG_S((byte)instruction.Operand),
                [Code.Ldarga_S]     = instruction => LDARGA_S((byte)instruction.Operand),
                [Code.Ldloc_S]      = instruction => LDLOC_S((byte)instruction.Operand),
                [Code.Ldloca_S]     = instruction => LDLOCA_S((byte)instruction.Operand),
                [Code.Ldnull]       = _ => LDNULL(),
                [Code.Ldc_I4_M1]    = _ => LDC_I4(-1),
                [Code.Ldc_I4_0]     = _ => LDC_I4(0),
                [Code.Ldc_I4_1]     = _ => LDC_I4(1),
                [Code.Ldc_I4_2]     = _ => LDC_I4(2),
                [Code.Ldc_I4_3]     = _ => LDC_I4(3),
                [Code.Ldc_I4_4]     = _ => LDC_I4(4),
                [Code.Ldc_I4_5]     = _ => LDC_I4(5),
                [Code.Ldc_I4_6]     = _ => LDC_I4(6),
                [Code.Ldc_I4_7]     = _ => LDC_I4(7),
                [Code.Ldc_I4_8]     = _ => LDC_I4(8),
                [Code.Ldc_I4_S]     = instruction => LDC_I4_S((sbyte)instruction.Operand),
                [Code.Ldc_I4]       = instruction => LDC_I4((int)instruction.Operand),
                [Code.Ldc_I8]       = instruction => LDC_I8((long)instruction.Operand),
                [Code.Ldc_R4]       = instruction => LDC_R4((float)instruction.Operand),
                [Code.Ldc_R8]       = instruction => LDC_R8((double)instruction.Operand),
                [Code.Dup]          = _ => DUP(),
                [Code.Pop]          = _ => POP(),
                [Code.Call]         = instruction => CALL((MethodReference)instruction.Operand),
                [Code.Add]          = _ => ADD(),
                [Code.Sub]          = _ => SUB(),
                [Code.Stsfld]       = instruction => STSFLD((FieldDefinition)instruction.Operand),
                [Code.Ldsfld]       = instruction => LDSFLD((FieldDefinition)instruction.Operand),
                [Code.Ret]          = _ => RET(),
                [Code.Volatile]     = _ => VOLATILE(),
                [Code.Br_S]         = instruction => BR(instruction.Operand),
                [Code.Br]           = instruction => BR(instruction.Operand),
                [Code.Ldstr]        = instruction => LDSTR((string)instruction.Operand),
                [Code.Conv_U]       = _ => CONV_U(),
                [Code.Conv_U8]      = _ => CONV_U8(),
                [Code.Conv_I8]      = _ => CONV_I8()
            };
        }

        private string GetVarType(TypeReference variableType)
        {
            if (variableType.IsPointer)
            {
                return "ptr";
            }

            string typeName = variableType.FullName.Split(' ')[0];

            return typeName switch
            {
                "System.Void" => "void",
                "System.Boolean" => "i1",
                "System.Byte" => "i8",
                "System.SByte" => "i8",
                "System.Int16" => "i16",
                "System.UInt16" => "i16",
                "System.Int32" => "i32",
                "System.UInt32" => "i32",
                "System.Int64" => "i64",
                "System.UInt64" => "i64",
                "System.Single" => "float",
                "System.Double" => "double",
                "System.String" => "ptr",
                _ => throw new NotSupportedException($"Unsupported variable type: {variableType.FullName}")
            };
        }

        int GetAlignmentForType(string type)
        {
            return type switch
            {
                "i1"   => 1,
                "i8"   => 1,
                "i16"  => 2,
                "i32"  => 4,
                "i64"  => 8,
                "ptr"  => ptrWidth,
                _      => ptrWidth
            };
        }

        bool IsWorkableType(string type)
        {
            return type switch
            {
                "i1"   => true,
                "i8"   => true,
                "i16"  => true,
                "i32"  => true,
                "i64"  => true,
                "float" => true,
                "double" => true,
                "ptr"  => false, // Cant tell what it points to, so skip for now
                _      => false
            };
        }

        void EmitCorelib()
        {
            Emitter.WriteLine("; CORELIB START");
            Emitter.WriteLine("; [System.Runtime]System.Object::.ctor()");
            Emitter.WriteLine("define void @System.Object..ctor(ptr %arg0) { ret void }");
            Emitter.WriteLine("; CORELIB END");
        }

        void Push(LLVMObject obj)
        {
            analyticalStack.Push(obj);
        }

        LLVMObject Pop()
        {
            return analyticalStack.Pop();
        }
        
        LLVMObject Peek()
        {
            return analyticalStack.Peek();
        }

        void NOP() { }

        void BREAK()
        {
            Emitter.WriteLine("    call void @llvm.debugtrap()");
        }

        void LDARG(byte index)
        {
            Push(new($"%arg{index}", ArgTypes[index], false));
        }

        void LDLOC(byte index)
        {
            Push(LocalVars[index]);
        }

        void STLOC(byte index)
        {
            LocalVars[index] = Pop();
        }

        void LDARG_S(byte index)
        {
            LDARG(index);
        }

        void LDARGA_S(byte index)
        {
            Push(new($"%arg{index}", "ptr", false));
        }

        void LDLOC_S(byte index)
        {
            Push(LocalVars[index]);
        }

        void LDLOCA_S(byte index)
        {
            Push(new($"%V_{index}", "ptr", false));
        }

        void LDNULL()
        {
            Push(new("null", "ptr", false));
        }

        void LDC_I4_S(sbyte value)
        {
            LDC_I4(value);
        }

        void LDC_I4(int value)
        {
            Push(new(value.ToString(), "i32", false));
        }

        void LDC_I8(long value)
        {
            Push(new(value.ToString(), "i64", false));
        }

        void LDC_R4(float value)
        {
            Push(new(value.ToString("R"), "float", false));
        }

        void LDC_R8(double value)
        {
            Push(new(value.ToString("R"), "double", false));
        }

        void DUP()
        {
            LLVMObject top = Peek();
            Push(new(top.Value, top.Type, top.isUnsigned));
        }

        void POP()
        {
            Pop();
        }

        void CALL(MethodReference method)
        {
            // Assume name is mangled so we can mangle and call it directly
            string mangledName = Mangler.Mangle(method);

            // Get arg types
            string[] callArgTypes = new string[method.Parameters.Count + (method.HasThis ? 1 : 0)];
            if (method.HasThis)
            {
                callArgTypes[0] = "ptr";
            }
            for (int i = 0; i < method.Parameters.Count; i++)
            {
                callArgTypes[i + (method.HasThis ? 1 : 0)] = GetVarType(method.Parameters[i].ParameterType);
            }

            // Get return type
            string returnType = GetVarType(method.ReturnType);

            // Pop args in reverse order
            LLVMObject[] args = new LLVMObject[callArgTypes.Length];
            for (int i = callArgTypes.Length - 1; i >= 0; i--)
            {
                args[i] = Pop();
            }

            // Emit call
            if (returnType == "void")
            {
                Emitter.WriteLine($"    call void @{mangledName}({string.Join(", ", callArgTypes.Select((t, i) => $"{t} {args[i].Value}"))})");
            }
            else
            {
                string tempReg = $"%t_{tempRegisterCounter++}";
                Emitter.WriteLine($"    {tempReg} = call {returnType} @{mangledName}({string.Join(", ", callArgTypes.Select((t, i) => $"{t} {args[i].Value}"))})");
                Push(new(tempReg, returnType, false));
            }
        }

        void ADD()
        {
            LLVMObject b = Pop();
            LLVMObject a = Pop();

            if (!IsWorkableType(a.Type) || !IsWorkableType(b.Type))
            {
                // Check if B or A is a ptr
                if      (a.Type == "ptr" && IsWorkableType(b.Type)) {}                  // Correct order, do nothing
                else if (b.Type == "ptr" && IsWorkableType(a.Type)) (a, b) = (b, a);    // Swap so ptr is always A
                else
                {
                    Console.WriteLine($"FATAL: Invalid types for ADD: {a.Type}, {b.Type}");
                    Environment.Exit(-1);
                }

                if (b.Type != "i64" && b.Type != "i32") // CIL only allows ptr + i32/i64, so if its not one of those, then assume we have invalid CIL
                {
                    Console.WriteLine($"FATAL: Invalid non-integer type for pointer arithmetic: {b.Type}");
                    Environment.Exit(-1);
                }

                string tempReg = $"%t_{tempRegisterCounter++}";
                Emitter.WriteLine($"    {tempReg} = getelementptr inbounds i8, ptr {a.Value}, {b.Type} {b.Value}");
                Push(new(tempReg, "ptr", false));

                return;
            }

            // First check if both A and B are constants
            if (a.Value[0] == '%' || b.Value[0] == '%')
            {
                string tempReg = $"%t_{tempRegisterCounter++}";

                // Check if float
                if (a.Type == "float" || a.Type == "double")
                {
                    Emitter.WriteLine($"    {tempReg} = fadd {a.Type} {a.Value}, {b.Value}");
                    Push(new(tempReg, a.Type, false));
                    return;
                }

                // If one is unsigned, the result is unsigned. Otherwise, its signed
                bool isUnsigned = a.isUnsigned || b.isUnsigned;

                Emitter.WriteLine($"    {tempReg} = add {a.Type} {a.Value}, {b.Type} {b.Value}");
                Push(new(tempReg, a.Type, isUnsigned));
                return;
            }

            // We can do the math here
            if (a.Type != b.Type)
            {
                Console.WriteLine($"FATAL: Type mismatch for ADD: {a.Type}, {b.Type}");
                Environment.Exit(-1);
            }

            // Really only have 4 choices here, i32, i64, float, double. Knowing that i8+ gets wided, can just check for those 4 types and assume the rest is invalid CIL
            string? resultTypeA = a.Type switch
            {
                "i32" => "i32",
                "i64" => "i64",
                "float" => "float",
                "double" => "double",
                _ => null
            };

            string? resultTypeB = b.Type switch
            {
                "i32" => "i32",
                "i64" => "i64",
                "float" => "float",
                "double" => "double",
                _ => null
            };

            if (resultTypeA == null || resultTypeB == null)
            {
                Console.WriteLine($"FATAL: Invalid types for ADD: {a.Type}, {b.Type}");
                Environment.Exit(-1);
            }

            // Now we dont have to emit anything, and can just do the math here and push as constant
            if (resultTypeA == "i32")
            {
                int valA = int.Parse(a.Value);
                int valB = int.Parse(b.Value);
                Push(new((valA + valB).ToString(), "i32", false));
            }
            else if (resultTypeA == "i64")
            {
                long valA = long.Parse(a.Value);
                long valB = long.Parse(b.Value);
                Push(new((valA + valB).ToString(), "i64", false));
            }
            else if (resultTypeA == "float")
            {
                float valA = float.Parse(a.Value);
                float valB = float.Parse(b.Value);
                Push(new((valA + valB).ToString("R"), "float", false));
            }
            else if (resultTypeA == "double")
            {
                double valA = double.Parse(a.Value);
                double valB = double.Parse(b.Value);
                Push(new((valA + valB).ToString("R"), "double", false));
            }

            return;
        }

        void SUB()
        {
            LLVMObject b = Pop();
            LLVMObject a = Pop();

            if (!IsWorkableType(a.Type) || !IsWorkableType(b.Type))
            {
                // Check if B or A is a ptr
                if      (a.Type == "ptr" && IsWorkableType(b.Type)) {}                  // Correct order, do nothing
                else if (b.Type == "ptr" && IsWorkableType(a.Type)) (a, b) = (b, a);    // Swap so ptr is always A
                else
                {
                    Console.WriteLine($"FATAL: Invalid types for SUB: {a.Type}, {b.Type}");
                    Environment.Exit(-1);
                }

                if (b.Type != "i64" && b.Type != "i32") // CIL only allows ptr + i32/i64, so if its not one of those, then assume we have invalid CIL
                {
                    Console.WriteLine($"FATAL: Invalid non-integer type for pointer arithmetic: {b.Type}");
                    Environment.Exit(-1);
                }

                string tempReg = $"%t_{tempRegisterCounter++}";
                Emitter.WriteLine($"    {tempReg} = getelementptr inbounds i8, ptr {a.Value}, {b.Type} {b.Value}");
                Push(new(tempReg, "ptr", false));

                return;
            }

            // First check if both A and B are constants
            if (a.Value[0] == '%' || b.Value[0] == '%')
            {
                string tempReg = $"%t_{tempRegisterCounter++}";

                // Check if float
                if (a.Type == "float" || a.Type == "double")
                {
                    Emitter.WriteLine($"    {tempReg} = fsub {a.Type} {a.Value}, {b.Value}");
                    Push(new(tempReg, a.Type, false));
                    return;
                }

                // If one is unsigned, the result is unsigned. Otherwise, its signed
                bool isUnsigned = a.isUnsigned || b.isUnsigned;

                Emitter.WriteLine($"    {tempReg} = sub {a.Type} {a.Value}, {b.Type} {b.Value}");
                Push(new(tempReg, a.Type, isUnsigned));
                return;
            }

            // We can do the math here
            if (a.Type != b.Type)
            {
                Console.WriteLine($"FATAL: Type mismatch for SUB: {a.Type}, {b.Type}");
                Environment.Exit(-1);
            }

            // Really only have 4 choices here, i32, i64, float, double. Knowing that i8+ gets wided, can just check for those 4 types and assume the rest is invalid CIL
            string? resultTypeA = a.Type switch
            {
                "i32" => "i32",
                "i64" => "i64",
                "float" => "float",
                "double" => "double",
                _ => null
            };

            string? resultTypeB = b.Type switch
            {
                "i32" => "i32",
                "i64" => "i64",
                "float" => "float",
                "double" => "double",
                _ => null
            };

            if (resultTypeA == null || resultTypeB == null)
            {
                Console.WriteLine($"FATAL: Invalid types for SUB: {a.Type}, {b.Type}");
                Environment.Exit(-1);
            }

            // Now we dont have to emit anything, and can just do the math here and push as constant
            if (resultTypeA == "i32")
            {
                int valA = int.Parse(a.Value);
                int valB = int.Parse(b.Value);
                Push(new((valA - valB).ToString(), "i32", false));
            }
            else if (resultTypeA == "i64")
            {
                long valA = long.Parse(a.Value);
                long valB = long.Parse(b.Value);
                Push(new((valA - valB).ToString(), "i64", false));
            }
            else if (resultTypeA == "float")
            {
                float valA = float.Parse(a.Value);
                float valB = float.Parse(b.Value);
                Push(new((valA - valB).ToString("R"), "float", false));
            }
            else if (resultTypeA == "double")
            {
                double valA = double.Parse(a.Value);
                double valB = double.Parse(b.Value);
                Push(new((valA - valB).ToString("R"), "double", false));
            }

            return;
        }

        void STSFLD(FieldDefinition field)
        {

            string fieldName = Mangler.Mangle(field);
            LLVMObject value = Pop();
            Emitter.WriteLine($"    store {(nextIsVolatile ? "volatile" : "")} {value.Type} {value.Value}, ptr @{fieldName}, align {GetAlignmentForType(value.Type)}");

            nextIsVolatile = false;
        }

        void LDSFLD(FieldDefinition field)
        {
            string fieldName = Mangler.Mangle(field);
            string fieldType = GetVarType(field.FieldType);
            string tempReg = $"%t_{tempRegisterCounter++}";
            Emitter.WriteLine($"    {tempReg} = load {(nextIsVolatile ? "volatile" : "")} {fieldType}, ptr @{fieldName}, align {GetAlignmentForType(fieldType)}");
            Push(new(tempReg, fieldType, false));
            nextIsVolatile = false;
        }

        void RET()
        {
            if (analyticalStack.Count > 0 && Peek().Type != "void")
            {
                LLVMObject returnValue = Pop();
                Emitter.WriteLine($"    ret {returnValue.Type} {returnValue.Value}");
            }
            else
            {
                Emitter.WriteLine("    ret void");
                analyticalStack.Clear();
            }
        }

        void VOLATILE()
        {
            nextIsVolatile = true;
        }

        void BR(object operand)
        {
            if (!IsInstruction(operand))
                throw new InvalidOpcodeException($"Invalid Branch Instruction! typeof = {operand.GetType()}"); 

            if (InstructionLabels.TryGetValue((Instruction)operand, out string? label) && !string.IsNullOrEmpty(label))
            {
                Emitter.WriteLine($"    br label %{label}");
                return;
            }

            throw new InvalidOpcodeException($"Invalid Branch Instruction! typeof = {operand.GetType()}");
        }

        void LDSTR(string value)
        {
            string globalName = $"str_{stringCounter++}";
            string llvmString = unicodeStrings ? ToLLVMUnicodeString(value) : ToLLVMAsciiString(value);

            DeclareLabels.Add($"@{globalName} = private unnamed_addr constant [{llvmString.Length / 3} x i8] c\"{llvmString}\", align 1");
            
            string tempReg = $"%t_{tempRegisterCounter++}";
            Emitter.WriteLine($"    {tempReg} = getelementptr inbounds [{llvmString.Length / 3} x i8], ptr @{globalName}, i64 0, i64 0");

            Push(new(tempReg, "ptr", false));
        }

        void CONV_U8()
        {
            LLVMObject obj = Pop();
            if (obj.Type == "i32")
            {
                // If constant, we can just convert it here and push as i64 constant
                if (!obj.Value.StartsWith('%'))
                {
                    int val = int.Parse(obj.Value);
                    Push(new(((ulong)val).ToString(), "i64", true));
                    return;
                }

                string tempReg = $"%t_{tempRegisterCounter++}";
                Emitter.WriteLine($"    {tempReg} = zext i32 {obj.Value} to i64");
                Push(new(tempReg, "i64", true));
            }
            else if (obj.Type == "i64")
            {
                Push(new(obj.Value, "i64", true));
            }
            else
            {
                Console.WriteLine($"FATAL: Invalid type for conv.u8: {obj.Type}");
                Environment.Exit(-1);
            }
        }

        void CONV_I8()
        {
            LLVMObject obj = Pop();
            if (obj.Type == "i32")
            {
                // If constant, we can just convert it here and push as i64 constant
                if (!obj.Value.StartsWith('%'))
                {
                    int val = int.Parse(obj.Value);
                    Push(new(((long)val).ToString(), "i64", false));
                    return;
                }

                string tempReg = $"%t_{tempRegisterCounter++}";
                Emitter.WriteLine($"    {tempReg} = sext i32 {obj.Value} to i64");
                Push(new(tempReg, "i64", false));
            }
            else if (obj.Type == "i64")
            {
                Push(new(obj.Value, "i64", false));
            }
            else
            {
                Console.WriteLine($"FATAL: Invalid type for conv.i8: {obj.Type}");
                Environment.Exit(-1);
            }
        }

        void CONV_U()
        {
            LLVMObject obj = Pop();
            if (obj.Type == "i32")
            {
                // If constant, we can just convert it here and push as ptr constant
                if (!obj.Value.StartsWith('%'))
                {
                    int val = int.Parse(obj.Value);
                    Push(new(((ulong)val).ToString(), "ptr", true));
                    return;
                }

                string tempReg = $"%t_{tempRegisterCounter++}";
                Emitter.WriteLine($"    {tempReg} = zext i32 {obj.Value} to ptr");
                Push(new(tempReg, "ptr", true));
            }
            else if (obj.Type == "i64")
            {
                // If constant, we can just convert it here and push as ptr constant
                if (!obj.Value.StartsWith('%'))
                {
                    long val = long.Parse(obj.Value);
                    Push(new(((ulong)val).ToString(), "ptr", true));
                    return;
                }

                string tempReg = $"%t_{tempRegisterCounter++}";
                Emitter.WriteLine($"    {tempReg} = zext i64 {obj.Value} to ptr");
                Push(new(tempReg, "ptr", true));
            }
            else if (obj.Type == "ptr")
            {
                Push(new(obj.Value, "ptr", true));
            }
            else
            {
                Console.WriteLine($"FATAL: Invalid type for conv.u: {obj.Type}");
                Environment.Exit(-1);
            }
        }
    }
}