using IL2LLVM.Attributes;

namespace ILTest2
{
    public static class Numbers
    {
        public static int GetNumber()
        {
            return 123;
        }
    }

    public static class Native
    {
        [NativeCall("printf")]
        public static void PrintF(string printf) 
            => throw new NotImplementedException();
    }
}