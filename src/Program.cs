using System;
using System.IO;
using System.Reflection;
using IL2LLVM.Compiler;
using Mono.Cecil;

namespace IL2LLVM
{
    internal class Program
    {
        static void Main(string[] args)
        {
            string inputFile = null!;
            string outputFile = null!;

            byte ptrWidth = (byte)IntPtr.Size;
            bool bundleCorelib = false;
            bool useUnicode = false;

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
                else if (args[i] == "--bundle-corelib")
                {
                    bundleCorelib = true;
                }
                else if (args[i] == "--ptr-width")
                {
                    if (i + 1 < args.Length && byte.TryParse(args[i + 1], out byte width))
                    {
                        ptrWidth = width;
                        i++;
                    }
                    else
                    {
                        Console.WriteLine("FATAL: Option '--ptr-width' requires a valid pointer width.");
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
                Console.WriteLine($"Compiling {inputFile} to {outputFile}...");
                ModuleDefinition module = ModuleDefinition.ReadModule(inputFile);
                var compiler = new Spratcher(module, ptrWidth, bundleCorelib, useUnicode);
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
        }

        private static void PrintVersion()
        {
            Console.WriteLine($"IL2LLVM version 0.1.0 (built on {DateTime.UtcNow:yyyy-MM-dd})");
            Console.WriteLine($"Running on .NET {Environment.Version}");
            Console.WriteLine($"Copyright (c) 2026 Aquanite, LLC and contributors. All rights reserved.");
            Console.WriteLine("Source code available at https://github.com/Aquanite/IL2LLVM");
            Console.WriteLine("Licensed under the MIT License.");
        }
    }
}
