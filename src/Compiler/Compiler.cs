using System.Globalization;
using System.Text;
using IL2LLVM.Formulae;
using IL2LLVM.ILException;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace IL2LLVM.Compiler
{

    public class Spratcher
    {
        private StreamWriter? emitter;
        private readonly List<AssemblyDefinition> assemblies;
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
        private readonly Dictionary<string, int> structDefinitions;
        private MethodDefinition currentMethod;
        private readonly Dictionary<Code, Action<Instruction>> instructionHandlers;
        private readonly List<string> allCctors;
        private bool overflowCheck = false;
        private bool exportRuntimeOverflow = true;
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

        public Spratcher(List<AssemblyDefinition> assemblies, byte ptrWidth, byte nativeWord, string targetDouble, bool bundleCorelib = false, bool unicodeStrings = true)
        {
            this.assemblies = assemblies;
            this.ptrWidth = ptrWidth;
            this.nativeWord = nativeWord;
            this.bundleCorelib = bundleCorelib;
            this.unicodeStrings = unicodeStrings;
            this.targetDouble = targetDouble;
            instructionHandlers = BuildInstructionHandlers();
            declareLabels = [];
            calledCctors = [];
            allCctors = [];
            currentMethod = null!; // Is okay because it's not used until it IS set
            structDefinitions = [];
        }

        private StreamWriter Emitter => emitter ?? throw new InvalidOperationException("Emitter not initialized.");
        private LLVMObject[] LocalVars => localVars ?? throw new InvalidOperationException("Local variables not initialized.");
        private string[] LocalVarTypes => localVarTypes ?? throw new InvalidOperationException("Local variable types not initialized.");
        private string[] ArgTypes => argTypes ?? throw new InvalidOperationException("Argument types not initialized.");
        private Dictionary<Instruction, string> InstructionLabels => instructionLabels ?? throw new InvalidOperationException("Instruction labels not initialized.");
        private List<string> DeclareLabels => declareLabels ?? throw new InvalidOperationException("Declare labels not initialized.");
        private List<string> CalledCctors => calledCctors ?? throw new InvalidOperationException("CCTORs called not initialized.");
        private List<string> AllCctors => allCctors ?? throw new InvalidOperationException("All CCTORs called not initialized.");
        private Dictionary<string, int> StructDefinitions => structDefinitions ?? throw new InvalidOperationException("Struct Definitions are not initialized");
        private MethodDefinition CurrentMethod => currentMethod ?? throw new InvalidOperationException("Current Method not initialized.");
        private static Dictionary<string, string> TargetMatch => targetMatch ?? throw new InvalidOperationException("Target Match not initialized.");

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
            Emitter.WriteLine($"declare void @llvm.memset.p0.i{ptrWidth * 8}(ptr nocapture writeonly, i8, i{ptrWidth * 8}, i1 immarg)");
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

                    // Get all types in the assemblies
                    var namespaceGroups = assemblies
                        .SelectMany(a => a.Modules)
                        .SelectMany(m => m.GetAllTypes())
                        .GroupBy(t => t.IsNested ? t.DeclaringType.Namespace : t.Namespace);


                    // Precache all native calls
                    foreach (var namespaceGroup in namespaceGroups)
                    {
                        foreach (var type in namespaceGroup)
                        {
                            foreach (var method in type.Methods)
                            {
                                if (IsNativeCall(method))
                                {
                                    Mangler.AddNativeCall(method.FullName, GetNativeCallName(method)!);
                                    Console.WriteLine($"INFO: Added NativeCall:    \u001b[33m'{method.FullName}'\u001b[0m to \u001b[32m'{GetNativeCallName(method)!}'\u001b[0m");
                                }
                                if (IsPlugReference(method))
                                {
                                    Mangler.AddPlugReference(GetPlugReference(method)!, method);
                                    Console.WriteLine($"INFO: Added PlugReference: \u001b[33m'{GetPlugReference(method)!}'\u001b[0m to \u001b[32m'{method.FullName}'\u001b[0m");
                                }
                                if (IsExport(method))
                                {
                                    Mangler.AddExport(method, GetExportName(method)!);
                                    Console.WriteLine($"INFO: Added Export:        \u001b[33m'{method.FullName}'\u001b[0m as \u001b[32m'{GetExportName(method)!}'\u001b[0m");

                                    if (GetExportName(method) == "__runtime_overflow_occured")
                                    {
                                        exportRuntimeOverflow = false;
                                        Console.WriteLine($"INFO: Detected user-defined overflow handler, skipping export of runtime overflow handler");
                                    }
                                }
                            }
                        }
                    }

                    var customStructs = namespaceGroups
                        .SelectMany(group => group) 
                        .Where(t => t.IsValueType && !t.IsPrimitive && t.FullName != "System.Void");

                    foreach (var structure in customStructs)
                    {
                        string structName = Mangler.Mangle(structure);
                        int structSize = GetTypeByteSize(structure);
                        
                        Emitter.WriteLine($"%{structName} = type [{structSize} x i8]");
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
                                // Check if it is a field in a struct
                                if (type.IsValueType && !type.IsPrimitive && type.FullName != "System.Void")
                                    continue;

                                string fieldName = Mangler.Mangle(field);
                                string fieldType = GetVarType(field.FieldType);
                                bool isConstant = field.IsLiteral && field.HasConstant;
                                string constantValue = isConstant ? field.Constant!.ToString() ?? "0" : "0";
                                Emitter.WriteLine($"@{fieldName} = global {fieldType} {constantValue}, align {GetAlignmentForType(fieldType)}");
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
                    if (IsWindows32())
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
                Emitter.WriteLine($"    %V_{i} = alloca {localType}");
                LocalVars[i] = new($"%V_{i}", "ptr", false);
            }

            // Console.WriteLine($"Function: {mangledName}");

            // Compile method body
            foreach (var instruction in method.Body.Instructions)
            {
                if (InstructionLabels.TryGetValue(instruction, out string? label) && !string.IsNullOrEmpty(label))
                {
                    Emitter.WriteLine($"    br label %{label} ; SEPERATOR"); // god awful branch rules, let llvm optimize this out
                    Emitter.WriteLine($"{label}:");
                }
                Emitter.WriteLine($"; IL_{instruction.Offset:X8}: {instruction.OpCode.Code}");
                // Console.WriteLine($"IL_{instruction.Offset:X8}: {instruction.OpCode.Code}");

                CompileInstruction(instruction);
                // Console.WriteLine($"Stack: [{string.Join(", ", analyticalStack.Select(o => $"{o.Value} ({o.Type})"))}]\n");
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
            instructionLabels = [];
            
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
                Code.Ble        => true,
                Code.Ble_S      => true,
                Code.Ble_Un_S   => true,
                Code.Ble_Un     => true,
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
            try
            {
                var def = methodref.Resolve();
                return GetNativeCallName(def);
            }
            catch (Exception) // No
            {
                Console.WriteLine($"WARN: Unable to resolve '{methodref.FullName}'. Is it in an included assembly?");
                return null;
            }
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

        private static string? GetPlugReference(MethodDefinition method)
        {
            // Is it a plug?
            var attr = method.CustomAttributes.FirstOrDefault(a =>
                a.AttributeType.FullName == "IL2LLVM.Attributes.Plug");

            return attr is null
                ? null
                : (string)attr.ConstructorArguments[0].Value;
        }

        private static string? GetExportName(MethodDefinition method)
        {
            // Is it an export?
            var attr = method.CustomAttributes.FirstOrDefault(a =>
                a.AttributeType.FullName == "IL2LLVM.Attributes.Export");

            return attr is null
                ? null
                : (string)attr.ConstructorArguments[0].Value;
        }

        private static bool IsNativeCall(MethodDefinition method) => GetNativeCallName(method) != null;
        private static bool IsNativeCall(MethodReference method) => GetNativeCallName(method) != null;
        private bool IsWindows32() => targetDouble == "i686-windows";
        private static bool IsVoid(string type) => type == "void";
        private static bool IsEntryPoint(MethodDefinition method) => GetEntryPoint(method) != null;
        private static bool IsPlugReference(MethodDefinition method) => GetPlugReference(method) != null;
        private static bool IsExport(MethodDefinition method) => GetExportName(method) != null;

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
                [Code.Nop]              = _ => {},
                [Code.Break]            = _ => Emitter.WriteLine(Call.Formulate("void", "llvm.debugtrap", [])),
                [Code.Ldarg_0]          = _ => LDARG(0),
                [Code.Ldarg_1]          = _ => LDARG(1),
                [Code.Ldarg_2]          = _ => LDARG(2),
                [Code.Ldarg_3]          = _ => LDARG(3),
                [Code.Ldloc_0]          = _ => LDLOC(0),
                [Code.Ldloc_1]          = _ => LDLOC(1),
                [Code.Ldloc_2]          = _ => LDLOC(2),
                [Code.Ldloc_3]          = _ => LDLOC(3),
                [Code.Stloc_0]          = _ => STLOC(0),
                [Code.Stloc_1]          = _ => STLOC(1),
                [Code.Stloc_2]          = _ => STLOC(2),
                [Code.Stloc_3]          = _ => STLOC(3),
                [Code.Ldarg_S]          = instruction => LDARG((ushort)instruction.Operand),
                [Code.Ldarga_S]         = instruction => LDARGA((VariableDefinition)instruction.Operand),
                [Code.Ldloc_S]          = instruction => LDLOC((ushort)((VariableDefinition)instruction.Operand).Index),
                [Code.Ldloca_S]         = instruction => LDLOCA((VariableDefinition)instruction.Operand),
                [Code.Ldarg]            = instruction => LDARG((ushort)instruction.Operand),
                [Code.Ldarga]           = instruction => LDARGA((VariableDefinition)instruction.Operand),
                [Code.Ldloc]            = instruction => LDLOC((ushort)((VariableDefinition)instruction.Operand).Index),
                [Code.Ldloca]           = instruction => LDLOCA((VariableDefinition)instruction.Operand),
                [Code.Stloc]            = instruction => STLOC((ushort)((VariableDefinition)instruction.Operand).Index),
                [Code.Stloc_S]          = instruction => STLOC((ushort)((VariableDefinition)instruction.Operand).Index),
                [Code.Ldnull]           = _ => LDNULL(),
                [Code.Ldc_I4_M1]        = _ => LDC_I4(-1),
                [Code.Ldc_I4_0]         = _ => LDC_I4(0),
                [Code.Ldc_I4_1]         = _ => LDC_I4(1),
                [Code.Ldc_I4_2]         = _ => LDC_I4(2),
                [Code.Ldc_I4_3]         = _ => LDC_I4(3),
                [Code.Ldc_I4_4]         = _ => LDC_I4(4),
                [Code.Ldc_I4_5]         = _ => LDC_I4(5),
                [Code.Ldc_I4_6]         = _ => LDC_I4(6),
                [Code.Ldc_I4_7]         = _ => LDC_I4(7),
                [Code.Ldc_I4_8]         = _ => LDC_I4(8),
                [Code.Ldc_I4_S]         = instruction => LDC_I4_S((sbyte)instruction.Operand),
                [Code.Ldc_I4]           = instruction => LDC_I4((int)instruction.Operand),
                [Code.Ldc_I8]           = instruction => LDC_I8((long)instruction.Operand),
                [Code.Ldc_R4]           = instruction => LDC_R4((float)instruction.Operand),
                [Code.Ldc_R8]           = instruction => LDC_R8((double)instruction.Operand),
                [Code.Dup]              = _ => DUP(),
                [Code.Pop]              = _ => POP(),
                [Code.Call]             = instruction => CALL((MethodReference)instruction.Operand),
                [Code.Calli]            = instruction => CALLI((CallSite)instruction.Operand),
                [Code.Add]              = _ => ADD(),
                [Code.Sub]              = _ => SUB(),
                [Code.Or]               = _ => OR(),
                [Code.Stsfld]           = instruction => STSFLD((FieldDefinition)instruction.Operand),
                [Code.Ldsfld]           = instruction => LDSFLD((FieldDefinition)instruction.Operand),
                [Code.Ret]              = _ => RET(),
                [Code.Volatile]         = _ => VOLATILE(),
                [Code.Br_S]             = instruction => BR(instruction.Operand),
                [Code.Br]               = instruction => BR(instruction.Operand),
                [Code.Brfalse_S]        = instruction => BRFALSE(instruction.Operand),
                [Code.Brfalse]          = instruction => BRFALSE(instruction.Operand),
                [Code.Brtrue]           = instruction => BRTRUE(instruction.Operand),
                [Code.Brtrue_S]         = instruction => BRTRUE(instruction.Operand),
                [Code.Ble]              = instruction => BLE(instruction.Operand),
                [Code.Ble_S]            = instruction => BLE_UN(instruction.Operand),
                [Code.Ble_Un_S]         = instruction => BLE_UN(instruction.Operand),
                [Code.Ceq]              = _ => CEQ(),
                [Code.Clt]              = _ => CLT(),
                [Code.Cgt]              = _ => CGT(),
                [Code.Ldstr]            = instruction => LDSTR((string)instruction.Operand),
                [Code.Conv_U]           = _ => CONV_U(),
                [Code.Conv_I]           = _ => CONV_I(),
                [Code.Conv_U8]          = _ => CONV_U8(),
                [Code.Conv_I8]          = _ => CONV_I8(),
                [Code.Conv_U4]          = _ => CONV_U4(),
                [Code.Conv_I4]          = _ => CONV_I4(),
                [Code.Conv_U2]          = _ => CONV_U2(),
                [Code.Conv_I2]          = _ => CONV_I2(),
                [Code.Conv_U1]          = _ => CONV_U1(),
                [Code.Conv_I1]          = _ => CONV_I1(),
                [Code.Conv_R4]          = _ => CONV_R4(),
                [Code.Conv_R8]          = _ => CONV_R8(),
                [Code.Stind_I8]         = _ => STIND_I8(),
                [Code.Stind_I1]         = _ => STIND_I1(),
                [Code.Stind_I2]         = _ => STIND_I2(),
                [Code.Stind_I4]         = _ => STIND_I4(),
                [Code.Stind_I]          = _ => STIND_I(),
                [Code.Stind_R4]         = _ => STIND_R4(),
                [Code.Stind_R8]         = _ => STIND_R8(),
                [Code.Ldind_I8]         = _ => LDIND_I8(),
                [Code.Ldind_I1]         = _ => LDIND_I1(),
                [Code.Ldind_I2]         = _ => LDIND_I2(),
                [Code.Ldind_I4]         = _ => LDIND_I4(),
                [Code.Ldind_I]          = _ => LDIND_I(),
                [Code.Ldind_R4]         = _ => LDIND_R4(),
                [Code.Ldind_R8]         = _ => LDIND_R8(),
                [Code.Ldind_U1]         = _ => LDIND_U1(),
                [Code.Ldind_U2]         = _ => LDIND_U2(),
                [Code.Ldind_U4]         = _ => LDIND_U4(),
                [Code.Ldind_Ref]        = _ => LDIND_REF(),
                [Code.Stind_Ref]        = _ => STIND_REF(),
                [Code.Add_Ovf]          = _ => ADD_OVF(),
                [Code.Conv_Ovf_U1_Un]   = _ => CONV_OVF_U1_UN(),
                [Code.Conv_Ovf_I8_Un]   = _ => CONV_OVF_I1_UN(),
                [Code.Conv_Ovf_U1]      = _ => CONV_OVF_U1(),
                [Code.Initobj]          = instruction => INITOBJ((TypeReference)instruction.Operand),
                [Code.Stfld]            = instruction => STFLD((FieldReference)instruction.Operand),
                [Code.Ldfld]            = instruction => LDFLD((FieldReference)instruction.Operand),
                [Code.Ldflda]           = instruction => LDFLDA((FieldReference)instruction.Operand),
            };
        }

        private bool IsUnsigned(TypeReference type)
        {
            return type.FullName switch
            {
                "System.Byte"   => true,
                "System.UInt16" => true,
                "System.UInt32" => true,
                "System.UInt64" => true,
                _               => false
            };
        }

        private string GetVarType(TypeReference variableType)
        {
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
                _                => "%" + Mangler.Mangle(variableType)
            };
        }

        public  int GetPrimitiveByteSize(TypeReference type)
        {
            return type.FullName switch
            {
                "System.Boolean" or "System.Byte"    or "System.SByte"  => 1,
                "System.Int16"   or "System.UInt16"                     => 2,
                "System.Int32"   or "System.UInt32"  or "System.Single" => 4,
                "System.Int64"   or "System.UInt64"  or "System.Double" => 8,
                "System.IntPtr"  or "System.UIntPtr"                    => nativeWord,
                _ => -1 // Not a primitive
            };
        }

        public int GetTypeByteSize(TypeReference typeRef)
        {
            int primitiveSize = GetPrimitiveByteSize(typeRef);
            if (primitiveSize != -1) return primitiveSize;

            TypeDefinition typeDef = typeRef.Resolve();
            if (typeDef == null) return 0;

            if (typeDef.ClassSize > 0) return typeDef.ClassSize;

            int totalSize = 0;
            foreach (var field in typeDef.Fields)
            {
                if (field.IsStatic) continue;
                totalSize += GetTypeByteSize(field.FieldType);
            }
            return totalSize;
        }

        public int GetFieldByteOffset(FieldReference fieldRef)
        {
            FieldDefinition fieldDef = fieldRef.Resolve();
            if (fieldDef == null)
                throw new InvalidOperationException("Expected assembly structure to be present.");

            TypeDefinition parentStruct = fieldDef.DeclaringType;

            if (fieldDef.Offset != -1)
            {
                return fieldDef.Offset;
            }

            int currentOffset = 0;

            foreach (FieldDefinition f in parentStruct.Fields)
            {
                // Skip static
                if (f.IsStatic) continue;

                if (f.FullName == fieldDef.FullName)
                {
                    return currentOffset;
                }

                int fieldSize = GetTypeByteSize(f.FieldType);
                currentOffset += fieldSize;
            }

            throw new InvalidOperationException($"Field {fieldRef.Name} not found in parent struct.");
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

        static bool IsPrimative(string type)
        {
            if (type.StartsWith('i') || type == "float" || type == "double")
                return true;
            
            return false;
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
            if (!IsPrimative(LocalVarTypes[index]))
            {
                Push(new($"%V_{index}", "ptr", false));
                return; // Forward
            }

            string tempReg = TemporaryRegister();
            Emitter.WriteLine($"    {tempReg} = load {LocalVarTypes[index]}, ptr {LocalVars[index].Value}, align {GetAlignmentForType(LocalVarTypes[index])}");
            Push(new(tempReg, LocalVarTypes[index], false));
        }

        void STLOC(ushort index)
        {
            if (!IsPrimative(LocalVarTypes[index]))
            {
                string tmp = TemporaryRegister();
                Emitter.WriteLine($"    {tmp} = load {LocalVarTypes[index]}, ptr {Pop().Value}");
                Emitter.WriteLine($"    store {LocalVarTypes[index]} {tmp}, ptr %V_{index}");
                return;
            }

            LLVMObject value = Pop();
            value = ConvertValueToType(value, IsPrimative(LocalVarTypes[index]) ? LocalVarTypes[index] : "ptr");
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
                MethodReference toCctorCheck = method;
                MethodDefinition? possiblePlug = Mangler.GetPlugReference(method.FullName);
                if (possiblePlug != null)
                {
                    toCctorCheck = possiblePlug;
                }

                TypeDefinition? typeDef = null;
                try
                {
                    typeDef = toCctorCheck.DeclaringType.Resolve();
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

            var nativeConv = IsNativeCall(method) && IsWindows32() 
                ? MethodCallingConvention.StdCall 
                : MethodCallingConvention.Default;

            string tempReg = TemporaryRegister();

            Call formCall = new(
                returnType, 
                mangledName, 
                [.. formattedArgs], 
                tempReg, 
                nativeConv
            );

            Emitter.WriteLine(formCall.Formulate());

            if (!IsVoid(returnType))
            {
                Push(new(tempReg, returnType, false));
            }
        }

        void ADD()
        {
            LLVMObject b = Pop();
            LLVMObject a = Pop();

            Utility.GetBiggerType(a.Type, b.Type, out byte bigger);

            if (bigger == 1 && a.Type[0] == 'i') // a
            {
                Push(b);
                EmitIntegerConv(a.Type, int.Parse(a.Type[1..]), a.isUnsigned);
                Push(a);
                ADD(); // Try again
                return;
            }
            else if (bigger == 2 && a.Type[0] == 'i') // b
            {
                Push(b);
                Push(a);
                EmitIntegerConv(b.Type, int.Parse(b.Type[1..]), b.isUnsigned);
                ADD(); // Try again
                return;
            }

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

        void ADD_OVF()
        {

            if(!overflowCheck)
            {
                if (exportRuntimeOverflow)
                    DeclareLabels.Add("declare void @__runtime_overflow_occured()");

                DeclareLabels.Add("declare { i32, i1 } @llvm.sadd.with.overflow.i32(i32, i32)");
                DeclareLabels.Add("declare { i64, i1 } @llvm.sadd.with.overflow.i64(i64, i64)");
                DeclareLabels.Add("");
                DeclareLabels.Add("define void @__runtime_swilloverflow_32(i32 %a, i32 %b) {");
                DeclareLabels.Add("entry:");
                DeclareLabels.Add("    %res = call { i32, i1 } @llvm.sadd.with.overflow.i32(i32 %a, i32 %b)");
                DeclareLabels.Add("    %is_overflow = extractvalue { i32, i1 } %res, 1");
                DeclareLabels.Add("    br i1 %is_overflow, label %overflow_detected, label %no_overflow");
                DeclareLabels.Add("");
                DeclareLabels.Add("overflow_detected:");
                DeclareLabels.Add("    call void @__runtime_overflow_occured()");
                DeclareLabels.Add("    ret void");
                DeclareLabels.Add("");
                DeclareLabels.Add("no_overflow:");
                DeclareLabels.Add("    ret void");
                DeclareLabels.Add("}");
                DeclareLabels.Add("");
                DeclareLabels.Add("define void @__runtime_swilloverflow_64(i64 %a, i64 %b) {");
                DeclareLabels.Add("entry:");
                DeclareLabels.Add("    %res = call { i64, i1 } @llvm.sadd.with.overflow.i64(i64 %a, i64 %b)");
                DeclareLabels.Add("    %is_overflow = extractvalue { i64, i1 } %res, 1");
                DeclareLabels.Add("    br i1 %is_overflow, label %overflow_detected, label %no_overflow");
                DeclareLabels.Add("");
                DeclareLabels.Add("overflow_detected:");
                DeclareLabels.Add("    call void @__runtime_overflow_occured()");
                DeclareLabels.Add("    ret void");
                DeclareLabels.Add("");
                DeclareLabels.Add("no_overflow:");
                DeclareLabels.Add("    ret void");
                DeclareLabels.Add("}");

                overflowCheck = true;
            }
            
            LLVMObject b = Pop();
            LLVMObject a = Pop();

            Utility.GetBiggerType(a.Type, b.Type, out byte bigger);

            if (a.Type[0] == 'f' || a.Type[0] == 'd' || b.Type[0] == 'f' || b.Type[0] == 'd')
                throw new InvalidOpcodeException("ADD.OVF Cannot accept floats.");

            if (bigger == 1 && a.Type[0] == 'i') // a
            {
                Push(b);
                EmitIntegerConv(a.Type, int.Parse(a.Type[1..]), a.isUnsigned);
                Push(a);
                ADD_OVF(); // Try again
                return;
            }
            else if (bigger == 2 && a.Type[0] == 'i') // b
            {
                Push(b);
                Push(a);
                EmitIntegerConv(b.Type, int.Parse(b.Type[1..]), b.isUnsigned);
                ADD_OVF(); // Try again
                return;
            }

            string type = a.Type;
            string checkFunction = $"__runtime_swilloverflow_{(type == "i32" ? "32" : "64")}";

            Emitter.WriteLine(Call.Formulate("void", checkFunction, [$"{type} {a.Value}", $"{type} {b.Value}"]));

            // If that passes, then we are good to add
            Push(a);
            Push(b);
            ADD();
            return;
        }

        void SUB()
        {
            LLVMObject b = Pop();
            LLVMObject a = Pop();

            if (a.Type == b.Type)
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

        void OR()
        {
            LLVMObject b = Pop();
            LLVMObject a = Pop();

            if (a.Type == b.Type && a.Type.StartsWith("i"))
            {
                string tempReg = TemporaryRegister();
                Emitter.WriteLine($"    {tempReg} = or {a.Type} {a.Value}, {b.Value}");
                Push(new(tempReg, a.Type, false));
                return;
            }

            throw new InvalidOpcodeException($"OR operator requires matching integer types! Got {a.Type} and {b.Type}");
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
            if (analyticalStack.Count > 1) // Screwed up stack
            {
                Console.WriteLine("FATAL: Leftover stack values:");
                foreach (var value in analyticalStack)
                {
                    Console.WriteLine($"    - type {value.Type} | value {value.Value} | unsigned {value.isUnsigned}");
                }
                throw new Exception("Leftover stack values.");
            }

            if (analyticalStack.Count > 0 && !IsVoid(Peek().Type))
            {
                LLVMObject returnValue = Pop();
                Emitter.WriteLine(Return.Formulate(returnValue.Type, returnValue.Value));
            }
            else
            {
                Emitter.WriteLine(Return.Formulate("void"));
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

        void BLE(object operand)
        {
            if (!IsInstruction(operand))
                throw new InvalidOpcodeException($"Invalid Branch Instruction! typeof = {operand.GetType()}"); 

            if (InstructionLabels.TryGetValue((Instruction)operand, out string? label) && !string.IsNullOrEmpty(label))
            {
                LLVMObject b = Pop();
                LLVMObject a = Pop();

                string br = TemporaryBranch();
                string tmp = TemporaryRegister();

                Utility.GetBiggerType(a.Type, b.Type, out byte bigger);

                if (bigger == 1 && a.Type[0] == 'i') // a
                {
                    Push(b);
                    EmitIntegerConv(a.Type, int.Parse(a.Type[1..]), a.isUnsigned);
                    Push(a);
                    BLE(operand); // Try again
                    return;
                }
                else if (bigger == 2 && a.Type[0] == 'i') // b
                {
                    Push(b);
                    Push(a);
                    EmitIntegerConv(b.Type, int.Parse(b.Type[1..]), b.isUnsigned);
                    BLE(operand); // Try again
                    return;
                }

                if (IsFloat(a.Type) || IsFloat(b.Type))
                {
                    Emitter.WriteLine(FloatCompare.Formulate(LLVMComparison.UnorderedLessThanOrEqual, a.Type, a.Value, b.Value, tmp));
                }
                else
                {
                    Emitter.WriteLine(IntegerCompare.Formulate(LLVMComparison.LessThanOrEqual, a.Type, a.Value, b.Value, tmp, a.isUnsigned || b.isUnsigned));
                }

                Emitter.WriteLine($"    br i1 {tmp}, label %{label}, label %{br}");
                Emitter.WriteLine($"{br}:");

                return;
            }

            throw new InvalidOpcodeException($"Invalid Branch Instruction! typeof = {operand.GetType()}");
        }

        void BLE_UN(object operand)
        {
            if (!IsInstruction(operand))
                throw new InvalidOpcodeException($"Invalid Branch Instruction! typeof = {operand.GetType()}"); 

            if (InstructionLabels.TryGetValue((Instruction)operand, out string? label) && !string.IsNullOrEmpty(label))
            {
                LLVMObject b = Pop();
                LLVMObject a = Pop();

                string br = TemporaryBranch();
                string tmp = TemporaryRegister();

                Utility.GetBiggerType(a.Type, b.Type, out byte bigger);

                if (bigger == 1 && a.Type[0] == 'i') // a
                {
                    Push(b);
                    EmitIntegerConv(a.Type, int.Parse(a.Type[1..]), true);
                    Push(a);
                    BLE(operand); // Try again
                    return;
                }
                else if (bigger == 2 && a.Type[0] == 'i') // b
                {
                    Push(b);
                    Push(a);
                    EmitIntegerConv(b.Type, int.Parse(b.Type[1..]), true);
                    BLE(operand); // Try again
                    return;
                }

                if (IsFloat(a.Type) || IsFloat(b.Type))
                {
                    Emitter.WriteLine(FloatCompare.Formulate(LLVMComparison.UnorderedLessThanOrEqual, a.Type, a.Value, b.Value, tmp));
                }
                else
                {
                    Emitter.WriteLine(IntegerCompare.Formulate(LLVMComparison.LessThanOrEqual, a.Type, a.Value, b.Value, tmp, true));
                }

                return;
            }

            throw new InvalidOpcodeException($"Invalid Branch Instruction! typeof = {operand.GetType()}");
        }

        void CONV_OVF_U1()
        {
            LLVMObject value = Pop();
            if (value.Type == "i8" && value.isUnsigned) { Push(value); return; }
            
            // If int
            if (value.Type.StartsWith('i'))
            {
                Push(value);
                EmitIntegerConv("i8", 8, true);
                return;
            }
            else if (value.Type == "float" || value.Type == "double")
            {
                string tempReg = TemporaryRegister();
                Emitter.WriteLine($"    {tempReg} = fptoui {value.Type} {value.Value} to i8");
                Push(new(tempReg, "i8", true));
                return;
            }
            else if (value.Type == "ptr")
            {
                string tempReg = TemporaryRegister();
                Emitter.WriteLine($"    {tempReg} = ptrtoint ptr {value.Value} to i8");
                Push(new(tempReg, "i8", true));
                return;
            }

            throw new InvalidOpcodeException($"Cannot convert type {value.Type} to i8 for conv.ovf.u1");
        }

        void CONV_OVF_U1_UN()
        {
            LLVMObject value = Pop();
            if (value.Type == "i8" && value.isUnsigned) { Push(value); return; }
            
            // If int
            if (value.Type.StartsWith('i'))
            {
                Push(value);
                EmitIntegerConv("i8", 8, true);
                return;
            }
            else if (value.Type == "float" || value.Type == "double")
            {
                string tempReg = TemporaryRegister();
                Emitter.WriteLine($"    {tempReg} = fptoui {value.Type} {value.Value} to i8");
                Push(new(tempReg, "i8", true));
                return;
            }
            else if (value.Type == "ptr")
            {
                string tempReg = TemporaryRegister();
                Emitter.WriteLine($"    {tempReg} = ptrtoint ptr {value.Value} to i8");
                Push(new(tempReg, "i8", true));
                return;
            }

            throw new InvalidOpcodeException($"Cannot convert type {value.Type} to i8 for conv.ovf.u1.un");
        }

        void CONV_OVF_I1_UN()
        {
            LLVMObject value = Pop();
            if (value.Type == "i8" && !value.isUnsigned) { Push(value); return; }
            
            // If int
            if (value.Type.StartsWith('i'))
            {
                Push(value);
                EmitIntegerConv("i8", 8, false);
                return;
            }
            else if (value.Type == "float" || value.Type == "double")
            {
                string tempReg = TemporaryRegister();
                Emitter.WriteLine($"    {tempReg} = fptoui {value.Type} {value.Value} to i8");
                Push(new(tempReg, "i8", false));
                return;
            }
            else if (value.Type == "ptr")
            {
                string tempReg = TemporaryRegister();
                Emitter.WriteLine($"    {tempReg} = ptrtoint ptr {value.Value} to i8");
                Push(new(tempReg, "i8", false));
                return;
            }

            throw new InvalidOpcodeException($"Cannot convert type {value.Type} to i8 for conv.ovf.i1.un");
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

        void EmitPointerConv(string targetType)
        {
            LLVMObject value = Pop();
            if (value.Type == targetType) { Push(value); return; }
            string tempReg = TemporaryRegister();
            if (value.Type == "ptr")
            {
                Emitter.WriteLine($"    {tempReg} = bitcast ptr {value.Value} to {targetType}");
                Push(new(tempReg, targetType, false));
                return;
            }
            if (value.Type.StartsWith("i"))
            {
                Emitter.WriteLine($"    {tempReg} = inttoptr {value.Type} {value.Value} to {targetType}");
                Push(new(tempReg, targetType, false));
                return;
            }
            throw new InvalidOpcodeException($"Cannot convert type {value.Type} to pointer type {targetType}");
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

            if (value.Type != "i8")
            {
                // Conv
                Push(value);
                EmitIntegerConv("i8", 8, value.isUnsigned);
                value = Pop();

                if (value.Type != "i8")
                {
                    Console.WriteLine($"FATAL: Invalid value type for stind.i1 after conversion: {value.Type}");
                    Environment.Exit(-1);
                }
            }

            Emitter.WriteLine($"    store {(nextIsVolatile ? "volatile" : "")} i8 {value.Value}, ptr {address.Value}, align 1");
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
                // Conv
                Push(value);
                EmitIntegerConv("i16", 16, value.isUnsigned);
                value = Pop();

                if (value.Type != "i16")
                {
                    Console.WriteLine($"FATAL: Invalid value type for stind.i2 after conversion: {value.Type}");
                    Environment.Exit(-1);
                }
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
                // Conv
                Push(value);
                EmitIntegerConv("i32", 32, value.isUnsigned);
                value = Pop();

                if (value.Type != "i32")
                {
                    Console.WriteLine($"FATAL: Invalid value type for stind.i4 after conversion: {value.Type}");
                    Environment.Exit(-1);
                }
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
                // Conv
                Push(value);
                EmitIntegerConv("i64", 64, value.isUnsigned);
                value = Pop();

                if (value.Type != "i64")
                {
                    Console.WriteLine($"FATAL: Invalid value type for stind.i8 after conversion: {value.Type}");
                    Environment.Exit(-1);
                }
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
                Push(address);
                EmitPointerConv("ptr");
                address = Pop();
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
                Push(address);
                EmitPointerConv("ptr");
                address = Pop();
            }

            string tempReg = TemporaryRegister();
            Emitter.WriteLine($"    {tempReg} = load i8, ptr {address.Value}, align 1");
            Push(new(tempReg, "i8", false));
        }

        void LDIND_I2()
        {
            LLVMObject address = Pop();

            if (address.Type != "ptr")
            {
                Push(address);
                EmitPointerConv("ptr");
                address = Pop();
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
                Push(address);
                EmitPointerConv("ptr");
                address = Pop();
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
                Push(address);
                EmitPointerConv("ptr");
                address = Pop();
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
                Push(address);
                EmitPointerConv("ptr");
                address = Pop();
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
                Push(address);
                EmitPointerConv("ptr");
                address = Pop();
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
                Push(address);
                EmitPointerConv("ptr");
                address = Pop();
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
                Push(address);
                EmitPointerConv("ptr");
                address = Pop();
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

            Utility.GetBiggerType(a.Type, b.Type, out byte bigger);

            if (bigger == 1 && a.Type[0] == 'i') // a
            {
                Push(a);
                Push(b);
                EmitIntegerConv(a.Type, int.Parse(a.Type[1..]), a.isUnsigned);
                CEQ(); // Try again
                return;
            }
            else if (bigger == 2 && a.Type[0] == 'i') // b
            {
                
                Push(a);
                EmitIntegerConv(b.Type, int.Parse(b.Type[1..]), b.isUnsigned);
                Push(b);
                CEQ(); // Try again
                return;
            }
            
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

            Utility.GetBiggerType(a.Type, b.Type, out byte bigger);

            if (bigger == 1 && a.Type[0] == 'i') // a
            {
                Push(a);
                Push(b);
                EmitIntegerConv(a.Type, int.Parse(a.Type[1..]), a.isUnsigned);
                CLT(); // Try again
                return;
            }
            else if (bigger == 2 && a.Type[0] == 'i') // b
            {
                Push(a);
                EmitIntegerConv(b.Type, int.Parse(b.Type[1..]), b.isUnsigned);
                Push(b);
                CLT(); // Try again
                return;
            }
            
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

            Utility.GetBiggerType(a.Type, b.Type, out byte bigger);

            if (bigger == 1 && a.Type[0] == 'i') // a
            {
                Push(a);
                Push(b);
                EmitIntegerConv(a.Type, int.Parse(a.Type[1..]), a.isUnsigned);
                CGT(); // Try again
                return;
            }
            else if (bigger == 2 && a.Type[0] == 'i') // b
            {
                Push(a);
                EmitIntegerConv(b.Type, int.Parse(b.Type[1..]), b.isUnsigned);
                Push(b);
                CGT(); // Try again
                return;
            }
            
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

        void CALLI(CallSite site)
        {
            MethodCallingConvention conv = site.CallingConvention;

            string returnType = GetVarType(site.ReturnType);
            string[] paramTypes = [.. site.Parameters.Select(p => GetVarType(p.ParameterType))];

            LLVMObject funcPtr = Pop();

            LLVMObject[] args = [.. paramTypes.Select(_ => Pop()).Reverse()];

            string tempReg = TemporaryRegister();

            Call indirectCall = new(
                returnType, 
                funcPtr.Value, 
                [.. args.Select((a, i) => $"{a.Type} {a.Value}")], 
                tempReg, 
                conv, 
                true
            );

            Emitter.WriteLine(indirectCall.Formulate());

            if (!IsVoid(returnType))
                Push(new(tempReg, returnType, false));
        }

        void INITOBJ(TypeReference reference)
        {
            int size = GetTypeByteSize(reference);

            if (size == 0)
                throw new InvalidOpcodeException("INITOBJ Got zero bytes for size of object.");
            
            LLVMObject ptr = Pop();

            string mangledName = Mangler.Mangle(reference);

            Call call = new(
                "void",
                $"llvm.memset.p0.i{ptrWidth * 8}",
                [$"ptr {ptr.Value}", "i8 0", $"i{ptrWidth * 8} {size}", "i1 false"]
            );

            Emitter.WriteLine(call.Formulate());
        }

        void STFLD(FieldReference reference)
        {
            var field = reference.Resolve()
                ?? throw new InvalidOpcodeException("STFLD expected resolved field, but was left unresolved. Was the target assembly included?");

            string tmp = TemporaryRegister();

            LLVMObject toStore = Pop();
            LLVMObject ptr = Pop();

            int size = GetTypeByteSize(field.DeclaringType);

            if (size == 0)
                throw new InvalidOpcodeException("STFLD Got zero bytes for size of object.");

            GetElementPointer elementPointer = new(
                tmp,
                "i8",
                ptr.Value,
                ["i32 0", $"i32 {GetFieldByteOffset(reference)}"],
                true,
                size,
                false
            );

            Emitter.WriteLine(elementPointer.Formulate());
            Emitter.WriteLine($"    store {toStore.Type} {toStore.Value}, ptr {tmp}");
        }

        void LDFLD(FieldReference reference)
        {
            var field = reference.Resolve()
                ?? throw new InvalidOpcodeException("LDFLD expected resolved field, but was left unresolved. Was the target assembly included?");

            string tmp = TemporaryRegister();

            LLVMObject ptr = Pop();

            int size = GetTypeByteSize(field.DeclaringType);

            if (size == 0)
                throw new InvalidOpcodeException("LDFLD Got zero bytes for size of object.");

            GetElementPointer elementPointer = new(
                tmp,
                "i8",
                ptr.Value,
                ["i32 0", $"i32 {GetFieldByteOffset(reference)}"],
                true,
                size,
                false
            );

            string type = GetVarType(field.FieldType);

            Emitter.WriteLine(elementPointer.Formulate());

            if (!IsPrimative(type))
            {
                Push(new(tmp, "ptr", false));
                return;
            }

            string toLoad = TemporaryRegister();
            Emitter.WriteLine($"    {toLoad} = load {type}, ptr {tmp}");

            Push(new(toLoad, type, IsUnsigned(field.FieldType)));
        }

        void LDFLDA(FieldReference reference)
        {
            var field = reference.Resolve()
                ?? throw new InvalidOpcodeException("LDFLDA expected resolved field, but was left unresolved. Was the target assembly included?");

            string tmp = TemporaryRegister();

            LLVMObject ptr = Pop();

            int size = GetTypeByteSize(field.DeclaringType);

            if (size == 0)
                throw new InvalidOpcodeException("LDFLD Got zero bytes for size of object.");

            GetElementPointer elementPointer = new(
                tmp,
                "i8",
                ptr.Value,
                ["i32 0", $"i32 {GetFieldByteOffset(reference)}"],
                true,
                size,
                false
            );

            Emitter.WriteLine(elementPointer.Formulate());

            Push(new(tmp, "ptr", false));
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