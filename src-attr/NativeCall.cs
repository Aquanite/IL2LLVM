namespace IL2LLVM.Attributes;

[AttributeUsage(AttributeTargets.Method)]
public class NativeCall(string name) : Attribute
{
    public string Name { get; } = name;
}