namespace IL2LLVM.Attributes;

[AttributeUsage(AttributeTargets.Method)]
public class Export(string name) : Attribute
{
    public string Name { get; } = name;
}