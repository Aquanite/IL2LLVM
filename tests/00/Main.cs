using IL2LLVM.Attributes;

namespace ILTest;

public class Program
{
    [EntryPoint]
    public static int Main()
    {
        for (int i = 0; i < 5; i++)
        {
            Native.PrintF("Hello, world!\n");       
        }
        return 0;
    }
}

public static unsafe class Native
{
    [NativeCall("printf")]
    public static int PrintF(string str) 
        => throw new NotImplementedException();
}