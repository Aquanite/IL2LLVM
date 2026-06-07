namespace IL2LLVM.Attributes;

[AttributeUsage(AttributeTargets.Method)]
public class Plug(string qualifiedName) : Attribute
{
    public string QualifiedName { get; } = qualifiedName;
}