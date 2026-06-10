# IL2LLVM
IL2LLVM is a C# to LLVM IR compiler. It takes a .NET assembly as input and produces an LLVM IR file as output, which can then be compiled to native code using the LLVM toolchain.

## Usage
To use IL2LLVM, run the following command in the terminal:
```
IL2LLVM [options] <input assembly> -o <output file>
```

### Options
- `-o <file>`: Specify the output file path (default: `out.ll`).
- `--target <triple>`: Set the target platform triple (e.g., `x86_64-linux`, `aarch64-darwin`). If omitted, it defaults to an auto-detected triple based on the host OS.
- `--targets`: List all supported target platform triples and exit.
- `--ptr-width <bytes>`: Specify the pointer width in bytes (`4` or `8`). Default is inferred from the target platform or host platform.
- `--native-word <bytes>`: Specify the native machine word size in bytes (`4` or `8`). Default matches the pointer width.
- `--bundle-corelib`: Bundle the .NET Core library into the output to run without requiring a separate .NET runtime.
- `--unicode`: Use Unicode (UTF-16) encoding for string literals in the generated LLVM IR (default: ASCII).
- `-h`, `--help`: Display help and usage information.
- `-v`, `--version`: Display version and runtime platform information.

## License
This project is licensed under the MIT License. See the [LICENSE](LICENSE) file for details

# Contributing
Contributions to IL2LLVM are welcome! If you have an idea for a new feature or have found a bug, please open an issue or submit a pull request. See the [CONTRIBUTORS](CONTRIBUTORS.md) file for a list of contributors to the project.

# Acknowledgements
- The Mono.Cecil library is used for reading and manipulating .NET assemblies.
- The LLVM project provides the tools and libraries for compiling LLVM IR.

# Contact
For questions or support, please open an issue on the GitHub repository or contact the maintainer at [@Aquanite, LLC](https://github.com/Aquanite).
<br>
Email:
- [support@aquanite.org](mailto:support@aquanite.org)
