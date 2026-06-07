namespace IL2LLVM.ILException
{
    [Serializable]
    internal class InvalidOpcodeException : Exception
    {
        public InvalidOpcodeException()
        {
        }

        public InvalidOpcodeException(string? message) : base(message)
        {
        }

        public InvalidOpcodeException(string? message, Exception? innerException) : base(message, innerException)
        {
        }
    }
}