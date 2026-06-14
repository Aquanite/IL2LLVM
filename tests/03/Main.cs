using IL2LLVM.Attributes;

unsafe class Program
{
    static int LoopTimes = 5;
    [EntryPoint]
    static int Main()
    {
        TestStruct a = new();
        a.useless.a = LoopTimes;
        a.useless.b = a.useless.a;

        for (int i = a.useless.b; i > 0; i--)
        {
            Console.WriteLine("Hello from C#!");
        }

        return 0;
    }
}
public struct Useless
{
    public int a;
    public int b;
    public int c;
}
public struct TestStruct
{
    public Useless useless;
    public int loop;
}

public static class Plugs
{
    [Plug("System.Void System.Console::WriteLine(System.String)")]
    public static void CWriteLine(string value)
    {
        Native.PrintF(value);
        Native.PrintF("\n");
    }
}

public static class Native
{
    [NativeCall("printf")]
    public static void PrintF(string str) 
        => throw new NotImplementedException();
}