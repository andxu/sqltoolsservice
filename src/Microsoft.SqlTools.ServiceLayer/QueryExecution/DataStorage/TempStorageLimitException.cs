using System;

namespace Microsoft.SqlTools.ServiceLayer.QueryExecution.DataStorage
{
    public sealed class TempStorageLimitException : Exception
    {
        public TempStorageLimitException() { }

        public TempStorageLimitException(string message) : base(message) { }

        public TempStorageLimitException(string message, Exception innerException)
            : base(message, innerException) { }
    }
}
