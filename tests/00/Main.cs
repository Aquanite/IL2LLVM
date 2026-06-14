using IL2LLVM.Attributes;

namespace ILTest;

public static class Program
{
    [EntryPoint]
    public static int Main()
    {
        Native.ClearLCD();
        Native.HomeUp();
        Native.DrawStatusBar();
        for (byte i = 0; i < 3; i++)
        {
            Console.WriteLine("Hello, C#!");
        }

        while (Native.GetCSC() == 0) {}
        return 0;
    }
}

public static class Native
{
    [NativeCall("os_ClrLCD")]
    public static void ClearLCD() 
        => throw new NotImplementedException();

    [NativeCall("os_HomeUp")]
    public static void HomeUp() 
        => throw new NotImplementedException();

    [NativeCall("os_DrawStatusBar")]
    public static void DrawStatusBar() 
        => throw new NotImplementedException();
    
    [NativeCall("os_PutStrFull")]
    public static void PutStringFull(string str) 
        => throw new NotImplementedException();
    
    [NativeCall("os_GetCSC")]
    public static byte GetCSC() 
        => throw new NotImplementedException();

    [NativeCall("os_SetCursorPos")]
    public static byte SetCursorPos(byte x, byte y)
        => throw new NotImplementedException();
}

public static class Plugs
{
    static byte Row = 0;
    [Plug("System.Void System.Console::WriteLine(System.String)")]
    public static void CWriteLine(string value)
    {
        Native.SetCursorPos(Row, 0);
        Native.PutStringFull(value);
        Row++;
    }
}