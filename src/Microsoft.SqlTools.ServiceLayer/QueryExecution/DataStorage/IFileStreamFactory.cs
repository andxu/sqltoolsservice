//
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//

namespace Microsoft.SqlTools.ServiceLayer.QueryExecution.DataStorage
{
    /// <summary>
    /// Interface for a factory that creates filesystem readers/writers
    /// </summary>
    public interface IFileStreamFactory
    {
        long MaxBytesToStore { get; set; }

        string CreateFile();

        IFileStreamReader GetReader(string fileName);

        IFileStreamWriter GetWriter(string fileName, long priorWrittenBytes, int maxCharsToStore, int maxXmlCharsToStore);

        void DisposeFile(string fileName);

    }
}
