namespace IL2LLVM.Attributes;

[AttributeUsage(AttributeTargets.Method)]
public class NativeCall : Attribute
{
    public string Name { get; }

    public NativeCall(string name)
    {
        Name = name;
    }
}