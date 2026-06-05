namespace IL2LLVM.Attributes;

[AttributeUsage(AttributeTargets.Method)]
public class EntryPoint : Attribute
{
    public string? RenameTo { get; }
    public EntryPoint() { } // [EntryPoint]
    public EntryPoint(string renameTo) => RenameTo = renameTo; // [EntryPoint("whatever")]
}