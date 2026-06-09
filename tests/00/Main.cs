using IL2LLVM.Attributes;
using ILTest2;

namespace ILTest;
public static class Program
{
    [EntryPoint]
    public static int Main()
    {
        int value = Numbers.GetNumber();
        if (value == 123)
        {
            Console.WriteLine("The same!");
        }

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