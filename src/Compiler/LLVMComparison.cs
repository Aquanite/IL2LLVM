namespace IL2LLVM.Compiler
{
    public enum LLVMComparison
    {
        True,
        False,
        Equal,
        NotEqual,
        GreaterThan,
        GreaterThanOrEqual,
        LessThan,
        LessThanOrEqual,
        OrderedEqual,
        OrderedNotEqual,
        OrderedGreaterThan,
        OrderedGreaterThanOrEqual,
        OrderedLessThan,
        OrderedLessThanOrEqual,
        Ordered,
        UnorderedEqual,
        UnorderedNotEqual,
        UnorderedGreaterThan,
        UnorderedGreaterThanOrEqual,
        UnorderedLessThan,
        UnorderedLessThanOrEqual,
        Unordered,
    }

    public static class LLVMComparisonExtension
    {
        public static string Flatten(this LLVMComparison cmp, bool isUnsigned = false)
        {
            return cmp switch // FUTURE: Frozen dictionary
            {
                // Integer
                LLVMComparison.Equal                        => "eq",
                LLVMComparison.NotEqual                     => "ne",
                LLVMComparison.GreaterThan                  => isUnsigned ? "ugt" : "sgt",
                LLVMComparison.GreaterThanOrEqual           => isUnsigned ? "uge" : "sge",
                LLVMComparison.LessThan                     => isUnsigned ? "ult" : "slt",
                LLVMComparison.LessThanOrEqual              => isUnsigned ? "ule" : "sle",

                // Floating-Point
                LLVMComparison.True                         => "true",
                LLVMComparison.False                        => "false",

                LLVMComparison.Ordered                      => "ord",
                LLVMComparison.OrderedEqual                 => "oeq",
                LLVMComparison.OrderedNotEqual              => "one",
                LLVMComparison.OrderedGreaterThan           => "ogt",
                LLVMComparison.OrderedGreaterThanOrEqual    => "oge",
                LLVMComparison.OrderedLessThan              => "olt",
                LLVMComparison.OrderedLessThanOrEqual       => "ole",

                LLVMComparison.Unordered                    => "uno",
                LLVMComparison.UnorderedEqual               => "ueq",
                LLVMComparison.UnorderedNotEqual            => "une",
                LLVMComparison.UnorderedGreaterThan         => "ugt",
                LLVMComparison.UnorderedGreaterThanOrEqual  => "uge",
                LLVMComparison.UnorderedLessThan            => "ult",
                LLVMComparison.UnorderedLessThanOrEqual     => "ule",

                _ => throw new IndexOutOfRangeException($"Unknown LLVM Compare instruction: {(int)cmp}")
            };
        }
    }
}