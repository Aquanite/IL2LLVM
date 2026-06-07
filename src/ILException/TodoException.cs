namespace IL2LLVM.ILException
{
    [Serializable]
    internal class TodoException : Exception
    {
        public TodoException()
        {
        }

        public TodoException(string? message) : base(message)
        {
        }

        public TodoException(string? message, Exception? innerException) : base(message, innerException)
        {
        }
    }
}