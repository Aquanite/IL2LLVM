# IL2LLVM
IL2LLVM is a C# to LLVM IR compiler. It takes a .NET assembly as input and produces an LLVM IR file as output, which can then be compiled to native code using the LLVM toolchain.

## Usage
To use IL2LLVM, run the following command in the terminal:
```
IL2LLVM [options] <input assembly> -o <output file>
```

### Options
- `--ptr-width <bytes>`: Specify the pointer width (e.g., 4 or 8). Default is the pointer width of the host platform.
- `--bundle-corelib`: Bundle the .NET Core library into the output. This will increase the output size but allows the generated code to run without requiring a separate .NET runtime.
- `--unicode`: Use Unicode encoding for string literals in the generated LLVM IR. By default, IL2LLVM uses ASCII encoding.
- `-h`, `--help`: Display help information.
- `-v`, `--version`: Display version information.

## License
This project is licensed under the MIT License. See the [LICENSE](LICENSE) file for details

# Contributing
Contributions to IL2LLVM are welcome! If you have an idea for a new feature or have found a bug, please open an issue or submit a pull request. See the [CONTRIBUTORS](CONTRIBUTORS.md) file for a list of contributors to the project.

# Acknowledgements
- The Mono.Cecil library is used for reading and manipulating .NET assemblies.
- The LLVM project provides the tools and libraries for generating and compiling LLVM IR.

# Contact
For questions or support, please open an issue on the GitHub repository or contact the maintainer at [@Aquanite, LLC](https://github.com/Aquanite).
<br>
Email:
- [support@aquanite.org](mailto:support@aquanite.org)