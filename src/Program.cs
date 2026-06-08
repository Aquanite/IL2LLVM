using System;
using System.IO;
using System.Reflection;
using IL2LLVM.Compiler;
using Mono.Cecil;

namespace IL2LLVM
{
    internal class Program
    {
        private static readonly string[] supportedTargets = [
            "i686-windows",
            "i686-linux",
            "i686-darwin",
            "i686-generic",
            "x86_64-windows",
            "x86_64-linux",
            "x86_64-darwin",
            "x86_64-generic",
            "aarch64-windows",
            "aarch64-linux",
            "aarch64-darwin",
            "aarch64-generic",
            "none-none" // Assume user sets clang target manually
        ];
        private static byte GetTargetWidth(string target) 
            => target.Split('-')[0] switch {
            "i686" => 4,
            "x86_64" => 8,
            "aarch64" => 8,
            _ => throw new InvalidDataException("Unknown Target Width: " + target)
        };
        static void Main(string[] args)
        {
            string inputFile = null!;
            string outputFile = null!;

            byte ptrWidth = (byte)IntPtr.Size;
            byte nativeWord = ptrWidth;
            bool setPtrWidth = false;
            bool setNativeWord = false;
            bool bundleCorelib = false;
            bool useUnicode = false;

            string targetDouble =
                OperatingSystem.IsWindows() ? "x86_64-windows" :
                OperatingSystem.IsLinux() ? "x86_64-linux" :
                OperatingSystem.IsMacOS() ? "aarch64-darwin" :
                "unknown";
            

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-h" || args[i] == "--help")
                {
                    PrintUsage();
                    return;
                }
                else if (args[i] == "--version" || args[i] == "-v")
                {
                    PrintVersion();
                    return;
                }
                else if (args[i] == "--targets")
                {
                    Console.WriteLine("==== Targets ====");
                    foreach (var target in supportedTargets)
                        Console.WriteLine($" - {target}");

                    return;
                }
                else if (args[i] == "--bundle-corelib")
                {
                    bundleCorelib = true;
                }
                else if (args[i] == "--ptr-width")
                {
                    if (i + 1 < args.Length && byte.TryParse(args[i + 1], out byte width) && width != 0)
                    {
                        ptrWidth = width;
                        setPtrWidth = true;
                        i++;
                    }
                    else
                    {
                        Console.WriteLine("FATAL: Option '--ptr-width' requires a valid pointer width.");
                        return;
                    }
                }
                else if (args[i] == "--native-word")
                {
                    if (i + 1 < args.Length && byte.TryParse(args[i + 1], out byte width) && width != 0)
                    {
                        nativeWord = width;
                        setNativeWord = true;
                        i++;
                    }
                    else
                    {
                        Console.WriteLine("FATAL: Option '--native-word' requires a valid native width.");
                        return;
                    }
                }
                else if (args[i] == "--target")
                {
                    if (i + 1 < args.Length)
                    {
                        targetDouble = args[i + 1];
                        if (!supportedTargets.Contains(targetDouble))
                        {
                            Console.WriteLine("FATAL: Option '--target' requires a valid target double. (--targets to get current targets)");
                            return;
                        }
                        i++;
                    }
                    else
                    {
                        Console.WriteLine("FATAL: Option '--target' requires a valid target double. (--targets to get current targets)");
                        return;
                    }
                }
                else if (args[i] == "--unicode")
                {
                    useUnicode = true;
                }
                else if (args[i] == "-o")
                {
                    if (i + 1 < args.Length)
                    {
                        outputFile = args[i + 1];
                        i++;
                    }
                    else
                    {
                        Console.WriteLine("FATAL: Option '-o' requires an output file path.");
                        return;
                    }
                }
                else if (!args[i].StartsWith('-'))
                {
                    inputFile ??= args[i];
                }
                else
                {
                    Console.WriteLine($"FATAL: Unknown argument '{args[i]}'");
                    PrintUsage();
                    return;
                }
            }

            if (string.IsNullOrEmpty(inputFile))
            {
                Console.WriteLine("FATAL: No input file specified.");
                PrintUsage();
                return;
            }

            if (string.IsNullOrEmpty(outputFile))
            {
                outputFile = $"{inputFile}.ll";
            }

            try
            {
                if (!setPtrWidth)
                    ptrWidth = GetTargetWidth(targetDouble);
                    
                Console.WriteLine($"Compiling {inputFile} to {outputFile}...");
                ModuleDefinition module = ModuleDefinition.ReadModule(inputFile);

                int width = GetModuleBitness(module);
                Console.WriteLine("INFO: Module width: " + (width == -1 ? "AnyCPU" : (width == -2 ? "Unknown" : width * 8)));
                Console.WriteLine("INFO: Target: " + targetDouble);

                if (width != -1 && width != -2 && width != ptrWidth)
                    Console.WriteLine($"WARN: Module architecture is {width * 8}-bit while selected Pointer Width is {ptrWidth * 8}-bit.");

                if (!setNativeWord)
                    nativeWord = ptrWidth;

                var compiler = new Spratcher(module, ptrWidth, nativeWord, targetDouble, bundleCorelib, useUnicode);
                compiler.Run(outputFile);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FATAL: Failed to process assembly: {ex.Message}");
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage: IL2LLVM <input_file> [-o <output_file>]");
            Console.WriteLine("Options:");
            Console.WriteLine("  -h, --help      Show this help message");
            Console.WriteLine("  -v, --version   Show version information");
            Console.WriteLine("  -o <file>       Specify output file (default: <input_file>.ll)");
            Console.WriteLine("  --ptr-width <n> Set pointer width (4 or 8, default: platform pointer width)");
            Console.WriteLine("  --target <t>    Set target double (e.g. x86_64-linux, aarch64-darwin, etc.)");
            Console.WriteLine("  --unicode       Use Unicode (UTF-16) for string literals (default: false)");
            Console.WriteLine("  --bundle-corelib Bundle the core library into the output (default: false)");
            Console.WriteLine("  --targets       List supported target doubles");
        }

        private static void PrintVersion()
        {
            Console.WriteLine($"IL2LLVM version 0.1.0 (built on {DateTime.UtcNow:yyyy-MM-dd})");
            Console.WriteLine($"Running on .NET {Environment.Version}");
            Console.WriteLine($"Copyright (c) 2026 Aquanite, LLC and contributors. All rights reserved.");
            Console.WriteLine("Source code available at https://github.com/Aquanite/IL2LLVM");
            Console.WriteLine("Licensed under the MIT License.");
        }

        private static int GetModuleBitness(ModuleDefinition module)
        {
            if (module.Architecture == TargetArchitecture.AMD64 || module.Architecture == TargetArchitecture.IA64)
                return 8;
                
            if (module.Architecture == TargetArchitecture.I386)
            {
                if ((module.Attributes & ModuleAttributes.Required32Bit) != 0)
                    return 4;
                    
                return -1;
            }
            
            return -2;
        }
    }
}
