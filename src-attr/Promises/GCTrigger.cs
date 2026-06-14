namespace IL2LLVM.Attributes.Promises;

[AttributeUsage(AttributeTargets.Method)]
public class GCTrigger() : Attribute {} // Promises the compiler that this method may trigger anything garbage collection related or call anything that may.