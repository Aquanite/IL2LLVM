namespace IL2LLVM.Attributes.Promises;

[AttributeUsage(AttributeTargets.Method)]
public class NoContract() : Attribute {} // Basically a NO-OP in the runtime, but good to have for identification reasons.