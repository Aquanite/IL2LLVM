using System.Reflection.Metadata;

namespace IL2LLVM.Formulae
{
    public class GetElementPointer( 
        string toSet, 
        string innerType,
        string innerPtr,
        string[] indexers,
        bool isArray = false,
        int itemCount = 0,
        bool isInbound = true
    ) : Formula
    {
        private readonly bool _isInbound = isInbound;
        private readonly string _toSet = toSet;
        private readonly string _innerType = innerType;
        private readonly string _innerPtr = innerPtr;
        private readonly string[] _indexers = indexers; 
        private readonly bool _isArray = isArray;
        private readonly int _itemCount = itemCount;

        public override string Formulate()
        {
            return $"    {_toSet} = getelementptr {(_isInbound ? "inbounds" : "")} {(_isArray ? $"[{_itemCount} x {_innerType}]" : _innerType)}, ptr {_innerPtr}, {string.Join(", ", _indexers)}";
        }

        public static string Formulate(
            string toSet, 
            string innerType,
            string innerPtr,
            string[] indexers,
            bool isArray = false,
            int itemCount = 0,
            bool isInbound = true
        )
        {
            return $"    {toSet} = getelementptr {(isInbound ? "inbounds" : "")} {(isArray ? $"[{itemCount} x {innerType}]" : innerType)}, ptr {innerPtr}, {string.Join(", ", indexers)}";
        }
    }
}