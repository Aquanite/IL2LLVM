namespace IL2LLVM.Attributes.Promises;

[AttributeUsage(AttributeTargets.Method)]
public class StackOverflowTolerant() : Attribute {} // Promise the compiler this method cannot physically stack overflow.