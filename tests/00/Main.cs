using IL2LLVM.Attributes;
using ILTest2;

namespace ILTest;
public static class Program
{
    [EntryPoint]
    public unsafe static int Main()
    {
        nint addr = 0x12345678;
        delegate* unmanaged[Stdcall]<string, void> printfPtr = (delegate* unmanaged[Stdcall]<string, void>)addr;
        printfPtr("Hello, World!\n");

        return 0;
    }

    public static class Plugs
    {
        [Plug("System.Void System.Console::WriteLine(System.String)")]
        public static void CWriteLine(string value)
        {
            Native.PrintF(value);
        }
    }
}