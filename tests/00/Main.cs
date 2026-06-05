using System;
using IL2LLVM.Attributes;
public unsafe class Program
{
    private static readonly int STD_OUT = -11;
    public static int Main()
    {
        void* handle = GetHandle(STD_OUT);
        WriteConsole(handle, "Hello from C# 🙂\n", 16, null, null);

        return 0;
    }

    [NativeCall("WriteConsoleW")]
    public static int WriteConsole(void* handle, string lpcwstr, ulong charsToWrite, ulong* charsWritten, void* reserved) 
        => throw new NotImplementedException();

    [NativeCall("GetStdHandle")]
    public static void* GetHandle(int stdHandle)
        => throw new NotImplementedException();
}
