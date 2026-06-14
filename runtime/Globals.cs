using IL2LLVM.Attributes;
using IL2LLVM.Attributes.Promises;


namespace IL2LLVM.Runtime
{
    internal static class Globals
    {
        internal static volatile bool g_fEEStarted = false;
        internal static volatile uint g_fFastExitProcess = 0;
    }
}