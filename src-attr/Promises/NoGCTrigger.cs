namespace IL2LLVM.Attributes.Promises;

[AttributeUsage(AttributeTargets.Method)]
public class NoGCTrigger() : Attribute {} // Promises the compiler that this method will not trigger anything garbage collection related or call anything that will.