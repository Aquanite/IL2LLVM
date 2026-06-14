namespace IL2LLVM.Attributes.Promises;

[AttributeUsage(AttributeTargets.Method)]
public class NoThrow() : Attribute {} // Promise the compiler this method will never throw.