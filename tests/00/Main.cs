using IL2LLVM.Attributes;

namespace ILTest;

public class Program
{
    [EntryPoint]
    public static int Main()
    {
        Test.Write("Hello from C# 🙂\n", 16);
        return 0;
    }
}

public static unsafe class Test
{
    private static readonly int STD_OUT = -11;

    public static void Write(string str, ulong length)
    {
        void* handle = Native.GetHandle(STD_OUT);
        Native.WriteConsole(handle, str, length, null, null);
    }
}

public static unsafe class Native
{
    [NativeCall("WriteConsoleW")]
    public static int WriteConsole(void* handle, string lpcwstr, ulong charsToWrite, ulong* charsWritten, void* reserved) 
        => throw new NotImplementedException();

    [NativeCall("GetStdHandle")]
    public static void* GetHandle(int stdHandle)
        => throw new NotImplementedException();
}