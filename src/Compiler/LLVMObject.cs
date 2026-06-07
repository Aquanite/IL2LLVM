namespace IL2LLVM.Compiler
{
    public record LLVMObject
    {
        public string Value {
            get {
                if (field == "0" && Type == "ptr")
                {
                    return "null";
                }
                return field;
            }
            set;
        }
        public string Type {get; set;}
        public bool isUnsigned {get; set;}

        public LLVMObject(string value, string type, bool isunsigned)
        {
            Value = value;
            Type = type;
            isUnsigned = isunsigned;
        }
    }
}