using IL2LLVM.Attributes;

namespace IL2LLVM.Runtime
{
    internal static unsafe class Native
    {
        [NativeCall("_InterlockedIncrement")]
        internal static long InterlockedIncrement(long* atomicptr)
            => throw new NotImplementedException(); 
    }
}