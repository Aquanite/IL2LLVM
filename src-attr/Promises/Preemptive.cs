namespace IL2LLVM.Attributes.Promises;

[AttributeUsage(AttributeTargets.Method)]
public class Preemptive() : Attribute {} // Promises the GC this function should ONLY be used in Preemptive mode.