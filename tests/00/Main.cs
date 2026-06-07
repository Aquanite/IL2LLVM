using IL2LLVM.Attributes;

namespace ILTest;

public class Program
{
    [EntryPoint]
    public static int Main()
    {
        for (int i = 0; 5 > i; i++)
        {
            Test.Write("Hello, world!\n", 14);       
        }
        return 0;
    }
}

public static unsafe class Test
{
    private static readonly int STD_OUT = -11;

    public static void Write(string str, nint length)
    {
        void* handle = Native.GetHandle(STD_OUT);
        Native.WriteConsole(handle, str, length, null, null);
    }
}

public static unsafe class Native
{
    [NativeCall("WriteConsoleW")]
    public static int WriteConsole(void* handle, string lpcwstr, nint charsToWrite, nint* charsWritten, void* reserved) 
        => throw new NotImplementedException();

    [NativeCall("GetStdHandle")]
    public static void* GetHandle(int stdHandle)
        => throw new NotImplementedException();
}