using System.Collections.Frozen;
using System.Globalization;
using System.Reflection.PortableExecutable;
using System.Text;
using IL2LLVM.Formulae;
using IL2LLVM.ILException;
using Microsoft.VisualBasic;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IL2LLVM.Compiler
{

    public class Spratcher
    {
        private StreamWriter? emitter;
        private readonly ModuleDefinition module;
        private readonly Stack<LLVMObject> analyticalStack = new();
        private LLVMObject[]? localVars;
        private string[]? localVarTypes;
        private string[]? argTypes; // Names will always be %arg0, %arg1
        private uint tempRegisterCounter = 0;
        private uint tempBranchCounter = 0;
        private uint stringCounter = 0;
        private readonly bool unicodeStrings = true;
        private readonly byte ptrWidth = 8; // Assume 64-bit 
        private readonly byte nativeWord = 8; // Assume 64-bit
        private bool nextIsVolatile = false;
        private readonly bool bundleCorelib = false;
        private readonly string targetDouble = "";
        private Dictionary<Instruction, string>? instructionLabels;
        private readonly List<string>? declareLabels;
        private readonly List<string>? calledCctors;
        private MethodDefinition currentMethod;
        private readonly Dictionary<Code, Action<Instruction>> instructionHandlers;
        private readonly List<string> allCctors;
        private static readonly Dictionary<string, string> targetMatch = new Dictionary<string, string>
        {
            ["i686-windows"]    = "i686-pc-windows-msvc",
            ["i686-linux"]      = "i686-pc-linux-gnu",
            ["i686-darwin"]     = "i386-apple-darwin",
            ["i686-generic"]    = "i686-unknown-unknown",

            ["x86_64-windows"]  = "x86_64-pc-windows-msvc",
            ["x86_64-linux"]    = "x86_64-pc-linux-gnu",
            ["x86_64-darwin"]   = "x86_64-apple-darwin",
            ["x86_64-generic"]  = "x86_64-unknown-unknown",

            ["aarch64-windows"] = "aarch64-pc-windows-msvc",
            ["aarch64-linux"]   = "aarch64-linux-gnu",
            ["aarch64-darwin"]  = "aarch64-apple-darwin",
            ["aarch64-generic"] = "aarch64-unknown-unknown",

            ["none-none"]       = ""
        };
        private string TargetNativeIntType
        {
            get
            {
                return "i" + nativeWord * 8;
            }
        }

        private int TargetNativeIntTypeBits
        {
            get
            {
                return nativeWord * 8;
            }
        }

        public Spratcher(ModuleDefinition module, byte ptrWidth, byte nativeWord, string targetDouble, bool bundleCorelib = false, bool unicodeStrings = true)
        {
            this.module = module;
            this.ptrWidth = ptrWidth;
            this.nativeWord = nativeWord;
            this.bundleCorelib = bundleCorelib;
            this.unicodeStrings = unicodeStrings;
            this.targetDouble = targetDouble;
            instructionHandlers = BuildInstructionHandlers();
            declareLabels = new List<string>();
            calledCctors = new List<string>();
            allCctors = new List<string>();
            currentMethod = null!; // Is okay because it's not used until it IS set
        }

        private StreamWriter Emitter => emitter ?? throw new InvalidOperationException("Emitter not initialized.");
        private LLVMObject[] LocalVars => localVars ?? throw new InvalidOperationException("Local variables not initialized.");
        private string[] LocalVarTypes => localVarTypes ?? throw new InvalidOperationException("Local variable types not initialized.");
        private string[] ArgTypes => argTypes ?? throw new InvalidOperationException("Argument types not initialized.");
        private Dictionary<Instruction, string> InstructionLabels => instructionLabels ?? throw new InvalidOperationException("Instruction labels not initialized.");
        private List<string> DeclareLabels => declareLabels ?? throw new InvalidOperationException("Declare labels not initialized.");
        private List<string> CalledCctors => calledCctors ?? throw new InvalidOperationException("CCTORs called not initialized.");
        private List<string> AllCctors => allCctors ?? throw new InvalidOperationException("All CCTORs called not initialized.");
        private MethodDefinition CurrentMethod => currentMethod ?? throw new InvalidOperationException("Current Method not initialized.");
        private Dictionary<string, string> TargetMatch => targetMatch ?? throw new InvalidOperationException("Target Match not initialized.");

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

        private void CallCctorIfNeeded(string cctor)
        {
            if (Mangler.Mangle(CurrentMethod) == cctor)
                return; // Don't want self calls

            if (CalledCctors.Contains(cctor))
                return; // Already initialized
            
            if (!AllCctors.Contains(cctor))
                AllCctors.Add(cctor);
            
            Emitter.WriteLine(Call.Formulate("void", "_cctor_check", [$"i32 {AllCctors.IndexOf(cctor)}"]));

            CalledCctors.Add(cctor);
        }

        private void GenerateCctorList()
        {
            if (AllCctors.Count == 0) return; // None

            Emitter.WriteLine($"@_cctor_init_array = constant [{AllCctors.Count} x ptr] [{string.Join(", ", AllCctors.Select(n => $"ptr @{n}"))}], align 16");
            Emitter.WriteLine($"@_cctor_init_state = global [{AllCctors.Count} x i8]  [{string.Join(", ", AllCctors.Select(n => $"i8 0"))}], align 1");

            Emitter.WriteLine("define void @_cctor_check(i32 %_cctor_init_a) {");

            Emitter.WriteLine("_cctor_init_b:");
            Emitter.WriteLine("    %_cctor_init_c = zext i32 %_cctor_init_a to i64");
            Emitter.WriteLine($"    %_cctor_init_d = getelementptr inbounds [{AllCctors.Count} x i8], ptr @_cctor_init_state, i64 0, i64 %_cctor_init_c");
            Emitter.WriteLine("    br label %_cctor_init_retry"); 

            Emitter.WriteLine("_cctor_init_retry:");
            Emitter.WriteLine("    %_cctor_init_old = load atomic i8, ptr %_cctor_init_d acquire, align 1");
            Emitter.WriteLine("    %_cctor_init_is_done = icmp eq i8 %_cctor_init_old, 2");
            Emitter.WriteLine("    br i1 %_cctor_init_is_done, label %_cctor_init_done, label %_cctor_init_check");

            Emitter.WriteLine("_cctor_init_check:");
            Emitter.WriteLine("    %_cctor_init_is_running = icmp eq i8 %_cctor_init_old, 1");
            Emitter.WriteLine("    br i1 %_cctor_init_is_running, label %_cctor_init_spin, label %_cctor_init_try_claim");

            Emitter.WriteLine("_cctor_init_try_claim:");
            Emitter.WriteLine("    %_cctor_init_cmp = cmpxchg ptr %_cctor_init_d, i8 0, i8 1 acquire acquire");
            Emitter.WriteLine("    %_cctor_init_success = extractvalue { i8, i1 } %_cctor_init_cmp, 1");
            Emitter.WriteLine("    br i1 %_cctor_init_success, label %_cctor_init_run, label %_cctor_init_retry");

            Emitter.WriteLine("_cctor_init_spin:");
            Emitter.WriteLine("    br label %_cctor_init_retry");

            Emitter.WriteLine("_cctor_init_run:");
            Emitter.WriteLine($"    %_cctor_init_h = getelementptr inbounds [{AllCctors.Count} x ptr], ptr @_cctor_init_array, i64 0, i64 %_cctor_init_c");
            Emitter.WriteLine("    %_cctor_init_i = load ptr, ptr %_cctor_init_h, align 8");
            Emitter.WriteLine("    call void %_cctor_init_i()");
            Emitter.WriteLine("    store atomic i8 2, ptr %_cctor_init_d release, align 1");
            Emitter.WriteLine("    br label %_cctor_init_done");

            Emitter.WriteLine("_cctor_init_done:");
            Emitter.WriteLine("    ret void");
            Emitter.WriteLine("}");
        }

        public void Run(string outFile)
        {
            try
            {
                using (StreamWriter write = new(File.Open(outFile, FileMode.Create)))
                {
                    emitter = write;

                    Emitter.WriteLine("; Compiled with IL2LLVM | Aquanite, LLC");
                    // Emitter.WriteLine($"target datalayout = \"p:{ptrWidth * 8}:{ptrWidth * 8}\"");
                    string llvmTarget = TargetMatch[targetDouble];
                    if (!string.IsNullOrEmpty(llvmTarget))
                        Emitter.WriteLine($"target triple = \"{llvmTarget}\"");

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
                    GenerateCctorList();
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
            currentMethod = method;

            // Reset state
            analyticalStack.Clear();
            tempRegisterCounter = 0;
            nextIsVolatile = false;
            CalledCctors.Clear();

            bool hasThis = method.HasThis;

            // Setup branch targets
            InitializeInstructionLabels([.. method.Body.Instructions]);


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
                    if (targetDouble == "i686-windows")
                        DeclareLabels.Add($"declare x86_stdcallcc {returnType} @{callName}({args})"); // 32 bit windows weird
                    else
                        DeclareLabels.Add($"declare {returnType} @{callName}({args})");
                }
                
                return; // No need to compile instructions or headers
            }

            // Emit function header
            string mangledName = "";
            if (IsEntryPoint(method))
            {
                mangledName = GetEntryPoint(method)!;
            }

            else mangledName = Mangler.Mangle(method);
            Emitter.WriteLine($"define {returnType} @{mangledName}({string.Join(", ", ArgTypes.Select((t, i) => $"{t} %arg{i}"))}) {{");

            // Setup local vars
            localVars = new LLVMObject[method.Body.Variables.Count];
            localVarTypes = new string[method.Body.Variables.Count];
            for (int i = 0; i < method.Body.Variables.Count; i++)
            {
                string localType = GetVarType(method.Body.Variables[i].VariableType);
                LocalVarTypes[i] = localType;
                Emitter.WriteLine($"    %V_{i} = alloca {localType}, align {GetAlignmentForType(localType)}");
                LocalVars[i] = new($"%V_{i}", "ptr", false);
            }

            // Compile method body
            foreach (var instruction in method.Body.Instructions)
            {
                if (InstructionLabels.TryGetValue(instruction, out string? label) && !string.IsNullOrEmpty(label))
                {
                    Emitter.WriteLine($"    br label %{label} ; SEPERATOR"); // god awful branch rules, let llvm optimize this out
                    Emitter.WriteLine($"{label}:");
                }

                // Emitter.WriteLine($"; IL_{instruction.Offset:X8}: {instruction.OpCode.Code}");
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

                    InstructionLabels.TryAdd(operand, $"IL_{operand.Offset:X8}");
                }
            }
        }
 
        private static bool IsBranchInstruction(Instruction ins)
        {
            return ins.OpCode.Code switch
            {
                Code.Br_S       => true,
                Code.Br         => true,
                Code.Brfalse    => true,
                Code.Brfalse_S  => true,
                Code.Brtrue     => true,
                Code.Brtrue_S   => true,
                _               => false
            };
        }

        private static bool IsLoadLocalInstruction(Instruction ins)
        {
            return ins.OpCode.Code switch
            {
                Code.Ldloc_0 => true,
                Code.Ldloc_1 => true,
                Code.Ldloc_2 => true,
                Code.Ldloc_3 => true,
                Code.Ldloc_S => true,
                Code.Ldloc   => true,
                _            => false
            };
        }

        private static bool IsLoadLocalAddressInstruction(Instruction ins)
        {
            return ins.OpCode.Code switch
            {
                Code.Ldloca => true,
                Code.Ldloca_S => true,
                _            => false
            };
        }
        private static bool IsInstruction(object operand) => operand is Instruction;
        private static bool IsVariable(object operand) => operand is VariableDefinition;
        private static bool IsFloat(string type) => type == "float" || type == "double";
        private string TemporaryRegister() => $"%t_{tempRegisterCounter++}";
        private string TemporaryBranch() => $"br_{tempBranchCounter++}";
        private static string? GetNativeCallName(MethodDefinition method)
        {
            // Is it a native call?
            var attr = method.CustomAttributes.FirstOrDefault(a =>
                a.AttributeType.FullName == "IL2LLVM.Attributes.NativeCall");

            return attr is null
                ? null
                : (string)attr.ConstructorArguments[0].Value;
        }

        private static string? GetNativeCallName(MethodReference methodref)
        {
            if (methodref is MethodDefinition method)
            {
                // Is it a native call?
                var attr = method.CustomAttributes.FirstOrDefault(a =>
                    a.AttributeType.FullName == "IL2LLVM.Attributes.NativeCall");

                return attr is null
                    ? null
                    : (string)attr.ConstructorArguments[0].Value;
            }

            return null;
        }

        private static string? GetEntryPoint(MethodDefinition method)
        {
            // Is it an entry point?
            var attr = method.CustomAttributes.FirstOrDefault(a =>
                a.AttributeType.FullName == "IL2LLVM.Attributes.EntryPoint");

            if (attr is null)
                return null;

            // Check if the paramless constructor
            if (attr.ConstructorArguments.Count == 0)
            {
                return "main";
            }

            // ret name
            string? renameTo = (string?)attr.ConstructorArguments[0].Value;
            return string.IsNullOrEmpty(renameTo) ? "main" : renameTo;
        }

        private static bool IsNativeCall(MethodDefinition method) => GetNativeCallName(method) != null;
        private static bool IsNativeCall(MethodReference method) => GetNativeCallName(method) != null;
        private static bool IsEntryPoint(MethodDefinition method) => GetEntryPoint(method) != null;

        private static string ToLLVMUnicodeString(string str)
        {
            byte[] utf16Bytes = Encoding.Unicode.GetBytes(str);
            return string.Concat(utf16Bytes.Select(b => $"\\{b:X2}")) + "\\00\\00";
        }

        private static string ToLLVMAsciiString(string str)
        {
            byte[] str_bytes = Encoding.ASCII.GetBytes(str);
            return string.Concat(str_bytes.Select(b => $"\\{b:X2}")) + "\\00";
        }

        private Dictionary<Code, Action<Instruction>> BuildInstructionHandlers()
        {
            return new Dictionary<Code, Action<Instruction>>
            {
                [Code.Nop]          = _ => {},
                [Code.Break]        = _ => Emitter.WriteLine(Call.Formulate("void", "llvm.debugtrap", [])),
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
                [Code.Ldarg_S]      = instruction => LDARG((ushort)instruction.Operand),
                [Code.Ldarga_S]     = instruction => LDARGA((VariableDefinition)instruction.Operand),
                [Code.Ldloc_S]      = instruction => LDLOC((ushort)((VariableDefinition)instruction.Operand).Index),
                [Code.Ldloca_S]     = instruction => LDLOCA((VariableDefinition)instruction.Operand),
                [Code.Ldarg]        = instruction => LDARG((ushort)instruction.Operand),
                [Code.Ldarga]       = instruction => LDARGA((VariableDefinition)instruction.Operand),
                [Code.Ldloc]        = instruction => LDLOC((ushort)((VariableDefinition)instruction.Operand).Index),
                [Code.Ldloca]       = instruction => LDLOCA((VariableDefinition)instruction.Operand),
                [Code.Stloc]        = instruction => STLOC((ushort)((VariableDefinition)instruction.Operand).Index),
                [Code.Stloc_S]      = instruction => STLOC((ushort)((VariableDefinition)instruction.Operand).Index),
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
                [Code.Brfalse_S]    = instruction => BRFALSE(instruction.Operand),
                [Code.Brfalse]      = instruction => BRFALSE(instruction.Operand),
                [Code.Brtrue]       = instruction => BRTRUE(instruction.Operand),
                [Code.Brtrue_S]     = instruction => BRTRUE(instruction.Operand),
                [Code.Ceq]          = _ => CEQ(),
                [Code.Clt]          = _ => CLT(),
                [Code.Cgt]          = _ => CGT(),
                [Code.Ldstr]        = instruction => LDSTR((string)instruction.Operand),
                [Code.Conv_U]       = _ => CONV_U(),
                [Code.Conv_I]       = _ => CONV_I(),
                [Code.Conv_U8]      = _ => CONV_U8(),
                [Code.Conv_I8]      = _ => CONV_I8(),
                [Code.Conv_U4]      = _ => CONV_U4(),
                [Code.Conv_I4]      = _ => CONV_I4(),
                [Code.Conv_U2]      = _ => CONV_U2(),
                [Code.Conv_I2]      = _ => CONV_I2(),
                [Code.Conv_U1]      = _ => CONV_U1(),
                [Code.Conv_I1]      = _ => CONV_I1(),
                [Code.Conv_R4]      = _ => CONV_R4(),
                [Code.Conv_R8]      = _ => CONV_R8(),
                [Code.Stind_I8]     = _ => STIND_I8(),
                [Code.Stind_I1]     = _ => STIND_I1(),
                [Code.Stind_I2]     = _ => STIND_I2(),
                [Code.Stind_I4]     = _ => STIND_I4(),
                [Code.Stind_I]      = _ => STIND_I(),
                [Code.Stind_R4]     = _ => STIND_R4(),
                [Code.Stind_R8]     = _ => STIND_R8(),
                [Code.Ldind_I8]     = _ => LDIND_I8(),
                [Code.Ldind_I1]     = _ => LDIND_I1(),
                [Code.Ldind_I2]     = _ => LDIND_I2(),
                [Code.Ldind_I4]     = _ => LDIND_I4(),
                [Code.Ldind_I]      = _ => LDIND_I(),
                [Code.Ldind_R4]     = _ => LDIND_R4(),
                [Code.Ldind_R8]     = _ => LDIND_R8(),
                [Code.Ldind_U1]     = _ => LDIND_U1(),
                [Code.Ldind_U2]     = _ => LDIND_U2(),
                [Code.Ldind_U4]     = _ => LDIND_U4(),
                [Code.Ldind_Ref]    = _ => LDIND_REF(),
                [Code.Stind_Ref]    = _ => STIND_REF(),
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
                "System.Void"    => "void",
                "System.Boolean" => "i1",
                "System.Byte"    => "i8",
                "System.SByte"   => "i8",
                "System.Int16"   => "i16",
                "System.UInt16"  => "i16",
                "System.Int32"   => "i32",
                "System.UInt32"  => "i32",
                "System.Int64"   => "i64",
                "System.UInt64"  => "i64",
                "System.Single"  => "float",
                "System.Double"  => "double",
                "System.String"  => "ptr",
                "System.IntPtr"  => TargetNativeIntType,
                _                => throw new NotSupportedException($"Unsupported variable type: {variableType.FullName}")
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
                "ptr"  => (int)Utility.PowerOf8(ptrWidth),
                _      => (int)Utility.PowerOf8(ptrWidth)
            };
        }

        static bool IsWorkableType(string type)
        {
            return type switch
            {
                "i1"     => true,
                "i8"     => true,
                "i16"    => true,
                "i32"    => true,
                "i64"    => true,
                "float"  => true,
                "double" => true,
                "ptr"    => false, // Cant tell what it points to, so skip for now
                _        => false
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

        void LDARG(ushort index)
        {
            Push(new($"%arg{index}", ArgTypes[index], false));
        }

        void LDLOC(ushort index)
        {
            string tempReg = TemporaryRegister();
            Emitter.WriteLine($"    {tempReg} = load {LocalVarTypes[index]}, ptr {LocalVars[index].Value}, align {GetAlignmentForType(LocalVarTypes[index])}");
            Push(new(tempReg, LocalVarTypes[index], false));
        }

        void STLOC(ushort index)
        {
            LLVMObject value = Pop();
            value = ConvertValueToType(value, LocalVarTypes[index]);
            LLVMObject finalValue = value;

            if ((value.Type == "float" || value.Type == "double") && double.TryParse(value.Value, CultureInfo.InvariantCulture, out double doubleValue))
            {
                ulong bits = BitConverter.DoubleToUInt64Bits(doubleValue);
                finalValue = new("0x" + bits.ToString("X16"), value.Type, false);
            }
            
            
            Emitter.WriteLine($"    store {(nextIsVolatile ? "volatile " : "")}{value.Type} {finalValue.Value}, ptr {LocalVars[index].Value}, align {GetAlignmentForType(LocalVarTypes[index])}");
            nextIsVolatile = false;
        }

        void LDARGA(VariableDefinition index)
        {
            Push(new($"%arg{index.Index}", "ptr", false));
        }

        void LDLOCA(VariableDefinition index)
        {
            string tempReg = TemporaryRegister();
            Emitter.WriteLine(GetElementPointer.Formulate(tempReg, 
                                                          LocalVarTypes[index.Index], 
                                                          LocalVars[index.Index].Value, 
                                                          ["i64 0"]));
            
            Push(new(tempReg, "ptr", false));
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
            if (!method.HasThis)
            {
                TypeDefinition? typeDef = null;
                try
                {
                    typeDef = method.DeclaringType.Resolve();
                }
                catch {}

                if (typeDef != null)
                {
                    var cctorMethod = typeDef.Methods
                        .FirstOrDefault(m => m.Name == ".cctor");

                    if (cctorMethod != null)
                        CallCctorIfNeeded(Mangler.Mangle(cctorMethod));
                }
            }
            
            string mangledName = Mangler.Mangle(method);

            string[] callArgTypes = new string[method.Parameters.Count + (method.HasThis ? 1 : 0)];
            if (method.HasThis)
            {
                callArgTypes[0] = "ptr";
            }
            for (int i = 0; i < method.Parameters.Count; i++)
            {
                callArgTypes[i + (method.HasThis ? 1 : 0)] = GetVarType(method.Parameters[i].ParameterType);
            }

            string returnType = GetVarType(method.ReturnType);

            LLVMObject[] args = new LLVMObject[callArgTypes.Length];
            for (int i = callArgTypes.Length - 1; i >= 0; i--)
            {
                args[i] = Pop();
            }

            var formattedArgs = callArgTypes.Select((t, i) => 
            {
                string val = args[i].Value;
                string currentType = args[i].Type;

                if (t == "ptr" && (val == "0" || string.IsNullOrEmpty(val)))
                {
                    return "ptr null";
                }

                // If function expects a pointer, but stack has an integer, cast it inline
                if (t == "ptr" && currentType.StartsWith("i"))
                {
                    string castReg = TemporaryRegister();
                    Emitter.WriteLine($"    {castReg} = inttoptr {currentType} {val} to ptr");
                    return $"ptr {castReg}";
                }

                return $"{t} {val}";
            });

            if (returnType == "void")
            {
                Emitter.WriteLine(Call.Formulate(returnType, mangledName, [.. formattedArgs], isWindowsNative: IsNativeCall(method)));
            }
            else
            {
                string tempReg = TemporaryRegister();
                Emitter.WriteLine(Call.Formulate(returnType, mangledName, [.. formattedArgs], tempReg, isWindowsNative: IsNativeCall(method))); 
                Push(new(tempReg, returnType, false));
            }
        }

        void ADD()
        {
            LLVMObject b = Pop();
            LLVMObject a = Pop();

            if (IsWorkableType(a.Type) && IsWorkableType(b.Type) && a.Type == b.Type)
            {
                string tempReg = TemporaryRegister();
                string llvmOp = a.Type switch
                {
                    "float" => "fadd",
                    "double" => "fadd",
                    _ => "add"
                };
                Emitter.WriteLine($"    {tempReg} = {llvmOp} {a.Type} {a.Value}, {b.Value}");
                Push(new(tempReg, a.Type, false));
                return;
            }
        }

        void SUB()
        {
            LLVMObject b = Pop();
            LLVMObject a = Pop();

            if (IsWorkableType(a.Type) && IsWorkableType(b.Type) && a.Type == b.Type)
            {
                string tempReg = TemporaryRegister();
                string llvmOp = a.Type switch
                {
                    "float" => "fsub",
                    "double" => "fsub",
                    _ => "sub"
                };
                Emitter.WriteLine($"    {tempReg} = {llvmOp} {a.Type} {a.Value}, {b.Value}");
                Push(new(tempReg, a.Type, false));
                return;
            }
        }

        void STSFLD(FieldDefinition field)
        {

            // See if CCTOR is called
            var cctorMethod = field.DeclaringType.Methods
                .FirstOrDefault(m => m.Name == ".cctor");

            if (cctorMethod != null)
                CallCctorIfNeeded(Mangler.Mangle(cctorMethod));

            string fieldName = Mangler.Mangle(field);
            LLVMObject value = Pop();
            value = ConvertValueToType(value, GetVarType(field.FieldType));
            Emitter.WriteLine($"    store {(nextIsVolatile ? "volatile" : "")} {value.Type} {value.Value}, ptr @{fieldName}, align {GetAlignmentForType(value.Type)}");

            nextIsVolatile = false;
        }

        void LDSFLD(FieldDefinition field)
        {
            // See if CCTOR is called
            var cctorMethod = field.DeclaringType.Methods
                .FirstOrDefault(m => m.Name == ".cctor");

            if (cctorMethod != null)
                CallCctorIfNeeded(Mangler.Mangle(cctorMethod));

            string fieldName = Mangler.Mangle(field);
            string fieldType = GetVarType(field.FieldType);
            string tempReg = TemporaryRegister();
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

        void BRFALSE(object operand)
        {
            if (!IsInstruction(operand))
                throw new InvalidOpcodeException($"Invalid Branch Instruction! typeof = {operand.GetType()}"); 

            if (InstructionLabels.TryGetValue((Instruction)operand, out string? label) && !string.IsNullOrEmpty(label))
            {
                LLVMObject cond = Pop();
                string br = TemporaryBranch();
                string tmp = TemporaryRegister();
                
                Emitter.WriteLine(IntegerCompare.Formulate(LLVMComparison.Equal, cond.Type, cond.Value, Utility.GetNativeFalse(cond.Type), tmp));
                Emitter.WriteLine($"    br i1 {tmp}, label %{label}, label %{br}");
                Emitter.WriteLine($"{br}:");

                return;
            }

            throw new InvalidOpcodeException($"Invalid Branch Instruction! typeof = {operand.GetType()}");
        }

        void BRTRUE(object operand)
        {
            if (!IsInstruction(operand))
                throw new InvalidOpcodeException($"Invalid Branch Instruction! typeof = {operand.GetType()}"); 

            if (InstructionLabels.TryGetValue((Instruction)operand, out string? label) && !string.IsNullOrEmpty(label))
            {
                LLVMObject cond = Pop();
                string br = TemporaryBranch();
                string tmp = TemporaryRegister();
                
                Emitter.WriteLine(IntegerCompare.Formulate(LLVMComparison.NotEqual, cond.Type, cond.Value, Utility.GetNativeFalse(cond.Type), tmp));
                Emitter.WriteLine($"    br i1 {tmp}, label %{label}, label %{br}");
                Emitter.WriteLine($"{br}:");

                return;
            }

            throw new InvalidOpcodeException($"Invalid Branch Instruction! typeof = {operand.GetType()}");
        }

        void LDSTR(string value)
        {
            string globalName = $"str_{stringCounter++}";
            string llvmString = unicodeStrings ? ToLLVMUnicodeString(value) : ToLLVMAsciiString(value);

            DeclareLabels.Add($"@{globalName} = private unnamed_addr constant [{llvmString.Length / 3} x i8] c\"{llvmString}\", align 1");
            
            string tempReg = TemporaryRegister();
            Emitter.WriteLine(GetElementPointer.Formulate(tempReg, 
                                        "i8", 
                                        $"@{globalName}", 
                                        ["i64 0", "i64 0"],
                                        true,
                                        llvmString.Length / 3));
            

            Push(new(tempReg, "ptr", false));
        }

        void CONV_R4()
        {
            LLVMObject value = Pop();
            if (value.Type == "float") { Push(value); return; }
            string tempReg = TemporaryRegister();
            string llvmOp = value.Type switch
            {
                "double" => "fptrunc",
                _ => value.isUnsigned ? "uitofp" : "sitofp"
            };
            Emitter.WriteLine($"    {tempReg} = {llvmOp} {value.Type} {value.Value} to float");
            Push(new(tempReg, "float", false));
        }

        void CONV_R8()
        {
            LLVMObject value = Pop();
            if (value.Type == "double") { Push(value); return; }
            string tempReg = TemporaryRegister();
            string llvmOp = value.Type switch
            {
                "float" => "fpext",
                _ => value.isUnsigned ? "uitofp" : "sitofp"
            };
            Emitter.WriteLine($"    {tempReg} = {llvmOp} {value.Type} {value.Value} to double");
            Push(new(tempReg, "double", false));
        }

        void EmitIntegerConv(string targetType, int targetWidth, bool targetUnsigned)
        {
            LLVMObject value = Pop();
            if (value.Type == targetType) { Push(new(value.Value, targetType, targetUnsigned)); return; }
            string tempReg = TemporaryRegister();
            if (value.Type == "ptr")
            {
                Emitter.WriteLine($"    {tempReg} = ptrtoint ptr {value.Value} to {targetType}");
                Push(new(tempReg, targetType, targetUnsigned));
                return;
            }
            if (value.Type == "float" || value.Type == "double")
            {
                string fpOp = targetUnsigned ? "fptoui" : "fptosi";
                Emitter.WriteLine($"    {tempReg} = {fpOp} {value.Type} {value.Value} to {targetType}");
                Push(new(tempReg, targetType, targetUnsigned));
                return;
            }
            int sourceWidth = int.Parse(value.Type[1..]);
            string llvmOp = sourceWidth > targetWidth ? "trunc" : (value.isUnsigned ? "zext" : "sext");
            Emitter.WriteLine($"    {tempReg} = {llvmOp} {value.Type} {value.Value} to {targetType}");
            Push(new(tempReg, targetType, targetUnsigned));
        }

        void CONV_U1() => EmitIntegerConv("i8", 8, true);
        void CONV_I1() => EmitIntegerConv("i8", 8, false);
        void CONV_U2() => EmitIntegerConv("i16", 16, true);
        void CONV_I2() => EmitIntegerConv("i16", 16, false);
        void CONV_U4() => EmitIntegerConv("i32", 32, true);
        void CONV_I4() => EmitIntegerConv("i32", 32, false);
        void CONV_U8() => EmitIntegerConv("i64", 64, true);
        void CONV_I8() => EmitIntegerConv("i64", 64, false);

        void CONV_U()
        {
            LLVMObject value = Pop();
            string targetType = $"i{nativeWord * 8}";
            if (value.Type == "ptr")
            {
                string tempReg = TemporaryRegister();
                Emitter.WriteLine($"    {tempReg} = ptrtoint ptr {value.Value} to {targetType}");
                Push(new(tempReg, targetType, true));
                return;
            }
            Push(value);
            EmitIntegerConv(targetType, nativeWord * 8, true);
        }

        void CONV_I()
        {
            LLVMObject value = Pop();
            string targetType = $"i{nativeWord * 8}";
            if (value.Type == "ptr")
            {
                string tempReg = TemporaryRegister();
                Emitter.WriteLine($"    {tempReg} = ptrtoint ptr {value.Value} to {targetType}");
                Push(new(tempReg, targetType, false));
                return;
            }
            Push(value);
            EmitIntegerConv(targetType, nativeWord * 8, false);
        }

        void STIND_I1()
        {
            LLVMObject value = Pop();
            LLVMObject address = Pop();

            if (address.Type != "ptr")
            {
                Console.WriteLine($"FATAL: Invalid address type for stind.i1: {address.Type} [value type: {value.Type}]");
                Environment.Exit(-1);
            }

            if (value.Type != "i1")
            {
                Console.WriteLine($"FATAL: Invalid value type for stind.i1: {value.Type}");
                Environment.Exit(-1);
            }

            Emitter.WriteLine($"    store {(nextIsVolatile ? "volatile" : "")} i1 {value.Value}, ptr {address.Value}, align 1");
            nextIsVolatile = false;
        }

        void STIND_I2()
        {
            LLVMObject value = Pop();
            LLVMObject address = Pop();

            if (address.Type != "ptr")
            {
                Console.WriteLine($"FATAL: Invalid address type for stind.i2: {address.Type} [value type: {value.Type}]");
                Environment.Exit(-1);
            }

            if (value.Type != "i16")
            {
                Console.WriteLine($"FATAL: Invalid value type for stind.i2: {value.Type}");
                Environment.Exit(-1);
            }

            Emitter.WriteLine($"    store {(nextIsVolatile ? "volatile" : "")} i16 {value.Value}, ptr {address.Value}, align 2");
            nextIsVolatile = false;
        }

        void STIND_I4()
        {
            LLVMObject value = Pop();
            LLVMObject address = Pop();

            if (address.Type != "ptr")
            {
                Console.WriteLine($"FATAL: Invalid address type for stind.i4: {address.Type} [value type: {value.Type}]");
                Environment.Exit(-1);
            }

            if (value.Type != "i32")
            {
                Console.WriteLine($"FATAL: Invalid value type for stind.i4: {value.Type}");
                Environment.Exit(-1);
            }

            Emitter.WriteLine($"    store {(nextIsVolatile ? "volatile" : "")} i32 {value.Value}, ptr {address.Value}, align 4");
            nextIsVolatile = false;
        }

        void STIND_I8()
        {
            LLVMObject value = Pop();
            LLVMObject address = Pop();

            if (address.Type != "ptr")
            {
                Console.WriteLine($"FATAL: Invalid address type for stind.i8: {address.Type} [value type: {value.Type}]");
                Environment.Exit(-1);
            }

            if (value.Type != "i64")
            {
                Console.WriteLine($"FATAL: Invalid value type for stind.i8: {value.Type}");
                Environment.Exit(-1);
            }


            Emitter.WriteLine($"    store {(nextIsVolatile ? "volatile" : "")} i64 {value.Value}, ptr {address.Value}, align 8");
            nextIsVolatile = false;
        }

        void STIND_R4()
        {
            LLVMObject value = Pop();
            LLVMObject address = Pop();

            if (address.Type != "ptr")
            {
                Console.WriteLine($"FATAL: Invalid address type for stind.r4: {address.Type} [value type: {value.Type}]");
                Environment.Exit(-1);
            }

            if (value.Type != "float")
            {
                Console.WriteLine($"FATAL: Invalid value type for stind.r4: {value.Type}");
                Environment.Exit(-1);
            }

            Emitter.WriteLine($"    store {(nextIsVolatile ? "volatile" : "")} float {value.Value}, ptr {address.Value}, align 4");
            nextIsVolatile = false;
        }

        void STIND_R8()
        {
            LLVMObject value = Pop();
            LLVMObject address = Pop();

            if (address.Type != "ptr")
            {
                Console.WriteLine($"FATAL: Invalid address type for stind.r8: {address.Type} [value type: {value.Type}]");
                Environment.Exit(-1);
            }

            if (value.Type != "double")
            {
                Console.WriteLine($"FATAL: Invalid value type for stind.r8: {value.Type}");
                Environment.Exit(-1);
            }

            Emitter.WriteLine($"    store {(nextIsVolatile ? "volatile" : "")} double {value.Value}, ptr {address.Value}, align 8");
            nextIsVolatile = false;
        }

        void STIND_I()
        {
            LLVMObject value = Pop();
            LLVMObject address = Pop();

            if (address.Type != "ptr")
            {
                Console.WriteLine($"FATAL: Invalid address type for stind.i: {address.Type} [value type: {value.Type}]");
                Environment.Exit(-1);
            }

            if (!value.Type.StartsWith("i"))
            {
                Console.WriteLine($"FATAL: Invalid value type for stind.i: {value.Type}");
                Environment.Exit(-1);
            }

            Emitter.WriteLine($"    store {(nextIsVolatile ? "volatile" : "")} {value.Type} {value.Value}, ptr {address.Value}, align {GetAlignmentForType(value.Type)}");
            nextIsVolatile = false;
        }

        void LDIND_I8()
        {
            LLVMObject address = Pop();

            if (address.Type != "ptr")
            {
                Console.WriteLine($"FATAL: Invalid address type for ldind.i8: {address.Type}");
                Environment.Exit(-1);
            }

            string tempReg = TemporaryRegister();
            Emitter.WriteLine($"    {tempReg} = load i64, ptr {address.Value}, align 8");
            Push(new(tempReg, "i64", false));
        }

        void LDIND_I1()
        {
            LLVMObject address = Pop();

            if (address.Type != "ptr")
            {
                Console.WriteLine($"FATAL: Invalid address type for ldind.i1: {address.Type}");
                Environment.Exit(-1);
            }

            string tempReg = TemporaryRegister();
            Emitter.WriteLine($"    {tempReg} = load i1, ptr {address.Value}, align 1");
            Push(new(tempReg, "i1", false));
        }

        void LDIND_I2()
        {
            LLVMObject address = Pop();

            if (address.Type != "ptr")
            {
                Console.WriteLine($"FATAL: Invalid address type for ldind.i2: {address.Type}");
                Environment.Exit(-1);
            }

            string tempReg = TemporaryRegister();
            Emitter.WriteLine($"    {tempReg} = load i16, ptr {address.Value}, align 2");
            Push(new(tempReg, "i16", false));
        }

        void LDIND_I4()
        {
            LLVMObject address = Pop();

            if (address.Type != "ptr")
            {
                Console.WriteLine($"FATAL: Invalid address type for ldind.i4: {address.Type}");
                Environment.Exit(-1);
            }

            string tempReg = TemporaryRegister();
            Emitter.WriteLine($"    {tempReg} = load i32, ptr {address.Value}, align 4");
            Push(new(tempReg, "i32", false));
        }

        void LDIND_I()
        {
            LLVMObject address = Pop();

            if (address.Type != "ptr")
            {
                Console.WriteLine($"FATAL: Invalid address type for ldind.i: {address.Type}");
                Environment.Exit(-1);
            }

            string tempReg = TemporaryRegister();
            Emitter.WriteLine($"    {tempReg} = load i64, ptr {address.Value}, align 8");
            Push(new(tempReg, "i64", false));
        }

        void LDIND_R4()
        {
            LLVMObject address = Pop();

            if (address.Type != "ptr")
            {
                Console.WriteLine($"FATAL: Invalid address type for ldind.r4: {address.Type}");
                Environment.Exit(-1);
            }

            string tempReg = TemporaryRegister();
            Emitter.WriteLine($"    {tempReg} = load float, ptr {address.Value}, align 4");
            Push(new(tempReg, "float", false));
        }

        void LDIND_R8()
        {
            LLVMObject address = Pop();

            if (address.Type != "ptr")
            {
                Console.WriteLine($"FATAL: Invalid address type for ldind.r8: {address.Type}");
                Environment.Exit(-1);
            }

            string tempReg = TemporaryRegister();
            Emitter.WriteLine($"    {tempReg} = load double, ptr {address.Value}, align 8");
            Push(new(tempReg, "double", false));
        }

        void LDIND_U1()
        {
            LLVMObject address = Pop();

            if (address.Type != "ptr")
            {
                Console.WriteLine($"FATAL: Invalid address type for ldind.u1: {address.Type}");
                Environment.Exit(-1);
            }

            string tempReg = TemporaryRegister();
            Emitter.WriteLine($"    {tempReg} = load i8, ptr {address.Value}, align 1");
            string tempReg2 = TemporaryRegister();
            Emitter.WriteLine(Extend.Formulate(true, "i8", tempReg, "i32", tempReg2));
            Push(new(tempReg2, "i32", true));
        }

        void LDIND_U2()
        {
            LLVMObject address = Pop();

            if (address.Type != "ptr")
            {
                Console.WriteLine($"FATAL: Invalid address type for ldind.u2: {address.Type}");
                Environment.Exit(-1);
            }

            string tempReg = TemporaryRegister();
            Emitter.WriteLine($"    {tempReg} = load i16, ptr {address.Value}, align 2");
            string tempReg2 = TemporaryRegister();
            Emitter.WriteLine(Extend.Formulate(true, "i16", tempReg, "i32", tempReg2));
            Push(new(tempReg2, "i32", true));
        }

        void LDIND_U4()
        {
            LLVMObject address = Pop();

            if (address.Type != "ptr")
            {
                Console.WriteLine($"FATAL: Invalid address type for ldind.u4: {address.Type}");
                Environment.Exit(-1);
            }

            string tempReg = TemporaryRegister();
            Emitter.WriteLine($"    {tempReg} = load i32, ptr {address.Value}, align 4");
            string tempReg2 = TemporaryRegister();
            Emitter.WriteLine(Extend.Formulate(true, "i32", tempReg, "i64", tempReg2));
            Push(new(tempReg2, "i64", true));
        }
        
        void LDIND_REF()
        {
            LLVMObject address = Pop();

            if (address.Type != "ptr")
            {
                Console.WriteLine($"FATAL: Invalid address type for ldind.ref: {address.Type}");
                Environment.Exit(-1);
            }

            string tempReg = TemporaryRegister();
            Emitter.WriteLine($"    {tempReg} = load ptr, ptr {address.Value}, align {Utility.PowerOf8(ptrWidth)}");
            Push(new(tempReg, "ptr", false));
        }

        void STIND_REF()
        {
            LLVMObject value = Pop();
            LLVMObject address = Pop();

            if (address.Type != "ptr")
            {
                Console.WriteLine($"FATAL: Invalid address type for stind.ref: {address.Type} [value type: {value.Type}]");
                Environment.Exit(-1);
            }

            if (value.Type != "ptr")
            {
                Console.WriteLine($"FATAL: Invalid value type for stind.ref: {value.Type}");
                Environment.Exit(-1);
            }

            Emitter.WriteLine($"    store {(nextIsVolatile ? "volatile" : "")} ptr {value.Value}, ptr {address.Value}, align {Utility.PowerOf8(ptrWidth)}");
            nextIsVolatile = false;
        }

        void CEQ()
        {
            LLVMObject b = Pop();
            LLVMObject a = Pop();

            if (a.Type != b.Type)
                throw new InvalidDataException($"CEQ invalid comparison: a={a.Type}, b={b.Type}");
            
            string tmp = TemporaryRegister();
            string res = TemporaryRegister();
            bool isFloat = IsFloat(a.Type);

            Compare cmp = new(
                isFloat ? LLVMComparison.UnorderedEqual : LLVMComparison.Equal, 
                a.Type,
                a.Value, b.Value, tmp, 
                isFloat
            );

            Emitter.WriteLine(cmp.Formulate());
            Emitter.WriteLine(Extend.Formulate(false, "i1", tmp, "i32", res));

            Push(new(res, "i32", false));
        }

        void CLT()
        {
            LLVMObject b = Pop();
            LLVMObject a = Pop();

            if (a.Type != b.Type)
                throw new InvalidDataException($"CEQ invalid comparison: a={a.Type}, b={b.Type}");
            
            string tmp = TemporaryRegister();
            string res = TemporaryRegister();
            bool isFloat = IsFloat(a.Type);

            Compare cmp = new(
                isFloat ? LLVMComparison.UnorderedLessThan : LLVMComparison.LessThan, 
                a.Type,
                a.Value, b.Value, tmp, 
                isFloat
            );

            Emitter.WriteLine(cmp.Formulate());
            Emitter.WriteLine(Extend.Formulate(false, "i1", tmp, "i32", res));

            Push(new(res, "i32", false));
        }

        void CGT()
        {
            LLVMObject b = Pop();
            LLVMObject a = Pop();

            if (a.Type != b.Type)
                throw new InvalidDataException($"CEQ invalid comparison: a={a.Type}, b={b.Type}");
            
            string tmp = TemporaryRegister();
            string res = TemporaryRegister();
            bool isFloat = IsFloat(a.Type);

            Compare cmp = new(
                isFloat ? LLVMComparison.UnorderedGreaterThan : LLVMComparison.GreaterThan, 
                a.Type,
                a.Value, b.Value, tmp, 
                isFloat
            );

            Emitter.WriteLine(cmp.Formulate());
            Emitter.WriteLine(Extend.Formulate(false, "i1", tmp, "i32", res));

            Push(new(res, "i32", false));
        }

        LLVMObject ConvertValueToType(LLVMObject value, string targetType)
        {
            if (value.Type == targetType)
                return value;

            string tempReg = TemporaryRegister();

            if (targetType == "ptr")
            {
                if (value.Type.StartsWith("i"))
                {
                    Emitter.WriteLine($"    {tempReg} = inttoptr {value.Type} {value.Value} to ptr");
                    return new(tempReg, "ptr", false);
                }

                if (value.Type == "null")
                    return new("null", "ptr", false);
            }

            if (value.Type == "ptr" && targetType.StartsWith("i"))
            {
                Emitter.WriteLine($"    {tempReg} = ptrtoint ptr {value.Value} to {targetType}");
                return new(tempReg, targetType, true);
            }

            if (targetType == "float" || targetType == "double")
            {
                if (value.Type == "float" && targetType == "double")
                {
                    Emitter.WriteLine($"    {tempReg} = fpext float {value.Value} to double");
                    return new(tempReg, "double", false);
                }

                if (value.Type == "double" && targetType == "float")
                {
                    Emitter.WriteLine($"    {tempReg} = fptrunc double {value.Value} to float");
                    return new(tempReg, "float", false);
                }

                if (value.Type.StartsWith("i"))
                {
                    string fpOp = value.isUnsigned ? "uitofp" : "sitofp";
                    Emitter.WriteLine($"    {tempReg} = {fpOp} {value.Type} {value.Value} to {targetType}");
                    return new(tempReg, targetType, false);
                }
            }

            if (targetType.StartsWith("i") && value.Type.StartsWith("i"))
            {
                int targetWidth = int.Parse(targetType[1..]);
                int sourceWidth = int.Parse(value.Type[1..]);
                string llvmOp = sourceWidth > targetWidth ? "trunc" : (value.isUnsigned ? "zext" : "sext");
                Emitter.WriteLine($"    {tempReg} = {llvmOp} {value.Type} {value.Value} to {targetType}");
                return new(tempReg, targetType, targetType == "i1" ? false : value.isUnsigned);
            }

            throw new NotSupportedException($"Cannot convert value type {value.Type} to {targetType}");
        }
    }
}