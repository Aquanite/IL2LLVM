namespace IL2LLVM.Attributes.Promises;

[AttributeUsage(AttributeTargets.Method)]
public class SafeGC() : Attribute {} // Promises the GC that no matter the state/mode, this function is safe to run.