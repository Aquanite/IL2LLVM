using IL2LLVM.Attributes;

namespace IL2LLVM.Runtime
{
    internal static class Comutilnative
    {
        [QCall]
        internal static void Environment_Exit(int exitcode)
        {
            Ceemain.SetLatchedExitCode(exitcode);
        }
    }
}