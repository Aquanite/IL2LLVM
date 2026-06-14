using IL2LLVM.Attributes;
using IL2LLVM.Attributes.Promises;


namespace IL2LLVM.Runtime
{
    internal static class EEPolicy
    {
        [NoContract]
        internal static void HandleExitProcess(Ceemain.ShutdownCompleteAction sca)
        {
            
        }
    }
}