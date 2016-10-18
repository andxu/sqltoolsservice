﻿// 
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SqlTools.ServiceLayer.QueryExecution.Contracts;
using Microsoft.SqlTools.ServiceLayer.QueryExecution.DataStorage;
using Microsoft.SqlTools.ServiceLayer.Utility;

namespace Microsoft.SqlTools.ServiceLayer.QueryExecution
{
    public class ResultSet : IDisposable
    {
        #region Constants

        private const int DefaultMaxCharsToStore = 65535; // 64 KB - QE default

        // xml is a special case so number of chars to store is usually greater than for other long types
        private const int DefaultMaxXmlCharsToStore = 2097152; // 2 MB - QE default

        // Column names of 'for xml' and 'for json' queries
        private const string NameOfForXMLColumn = "XML_F52E2B61-18A1-11d1-B105-00805F49916B";
        private const string NameOfForJSONColumn = "JSON_F52E2B61-18A1-11d1-B105-00805F49916B";


        #endregion

        #region Member Variables

        /// <summary>
        /// For IDisposable pattern, whether or not object has been disposed
        /// </summary>
        private bool disposed;

        /// <summary>
        /// The factory to use to get reading/writing handlers
        /// </summary>
        private readonly IFileStreamFactory fileStreamFactory;

        /// <summary>
        /// File stream reader that will be reused to make rapid-fire retrieval of result subsets
        /// quick and low perf impact.
        /// </summary>
        private IFileStreamReader fileStreamReader;

        /// <summary>
        /// Whether or not the result set has been read in from the database
        /// </summary>
        private bool hasBeenRead;

        /// <summary>
        /// Whether resultSet is a 'for xml' or 'for json' result
        /// </summary>
        private bool isSingleColumnXmlJsonResultSet;

        /// <summary>
        /// The name of the temporary file we're using to output these results in
        /// </summary>
        private readonly string outputFileName;

        /// <summary>
        /// The total number of bytes that had been read before this result set
        /// </summary>
        private readonly long priorBytes;

        #endregion

        /// <summary>
        /// Creates a new result set and initializes its state
        /// </summary>
        /// <param name="reader">The reader from executing a query</param>
        /// <param name="factory">Factory for creating a reader/writer</param>
        /// <param name="bytes">
        /// Number of bytes read before this resultSet. For first resultset this should be 0
        /// </param>
        public ResultSet(DbDataReader reader, IFileStreamFactory factory, long bytes)
        {
            // Sanity check to make sure we got a reader
            Validate.IsNotNull(nameof(reader), SR.QueryServiceResultSetReaderNull);

            DataReader = new StorageDataReader(reader);

            // Initialize the storage
            outputFileName = factory.CreateFile();
            FileOffsets = new LongList<long>();
            priorBytes = bytes;

            // Store the factory
            fileStreamFactory = factory;
            hasBeenRead = false;
        }

        #region Properties

        /// <summary>
        /// The columns for this result set
        /// </summary>
        public DbColumnWrapper[] Columns { get; private set; }

        /// <summary>
        /// The reader to use for this resultset
        /// </summary>
        private StorageDataReader DataReader { get; set; }

        /// <summary>
        /// A list of offsets into the buffer file that correspond to where rows start
        /// </summary>
        private LongList<long> FileOffsets { get; set; }

        /// <summary>
        /// Maximum number of characters to store for a field
        /// </summary>
        public int MaxCharsToStore { get { return DefaultMaxCharsToStore; } }

        /// <summary>
        /// Maximum number of characters to store for an XML field
        /// </summary>
        public int MaxXmlCharsToStore { get { return DefaultMaxXmlCharsToStore; } }

        /// <summary>
        /// The number of rows for this result set
        /// </summary>
        public long RowCount { get; private set; }

        /// <summary>
        /// The rows of this result set
        /// </summary>
        public IEnumerable<string[]> Rows
        {
            get
            {
                return FileOffsets.Select(
                    offset => fileStreamReader.ReadRow(offset, Columns).Select(cell => cell.DisplayValue).ToArray());
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Generates a subset of the rows from the result set
        /// </summary>
        /// <param name="startRow">The starting row of the results</param>
        /// <param name="rowCount">How many rows to retrieve</param>
        /// <returns>A subset of results</returns>
        public Task<ResultSetSubset> GetSubset(int startRow, int rowCount)
        {
            // Sanity check to make sure that the results have been read beforehand
            if (!hasBeenRead || fileStreamReader == null)
            {
                throw new InvalidOperationException(SR.QueryServiceResultSetNotRead);
            }

            // Sanity check to make sure that the row and the row count are within bounds
            if (startRow < 0 || startRow >= RowCount)
            {
                throw new ArgumentOutOfRangeException(nameof(startRow), SR.QueryServiceResultSetStartRowOutOfRange);
            }
            if (rowCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(rowCount), SR.QueryServiceResultSetRowCountOutOfRange);
            }

            return Task.Factory.StartNew(() =>
            {

                string[][] rows;
                // If result set is 'for xml' or 'for json',
                // Concatenate all the rows together into one row
                if (isSingleColumnXmlJsonResultSet)
                {
                    // Iterate over all the rows and process them into a list of string builders
                    IEnumerable<StringBuilder> sbRows = FileOffsets.Select(rowOffset => fileStreamReader.ReadRow(rowOffset, Columns)
                        .Select(cell => cell.DisplayValue).Aggregate(new StringBuilder(), (sb, value) => sb.Append(value)));
                    rows = new[] { new[] { string.Join(string.Empty, sbRows) } };

                }
                else
                {
                    // Figure out which rows we need to read back
                    IEnumerable<long> rowOffsets = FileOffsets.Skip(startRow).Take(rowCount);

                    // Iterate over the rows we need and process them into output
                    rows = rowOffsets.Select(rowOffset =>
                        fileStreamReader.ReadRow(rowOffset, Columns).Select(cell => cell.DisplayValue).ToArray())
                        .ToArray();

                }

                // Retrieve the subset of the results as per the request
                return new ResultSetSubset
                {
                    Rows = rows,
                    RowCount = rows.Length
                };
            });
        }

        /// <summary>
        /// Reads from the reader until there are no more results to read
        /// </summary>
        /// <param name="cancellationToken">Cancellation token for cancelling the query</param>
        public async Task<long> ReadResultToEnd(CancellationToken cancellationToken)
        {
            // Mark that result has been read
            hasBeenRead = true;
            fileStreamReader = fileStreamFactory.GetReader(outputFileName);

            // Open a writer for the file
            long currentFileOffset = 0;
            using (IFileStreamWriter fileWriter = fileStreamFactory.GetWriter(outputFileName, priorBytes, MaxCharsToStore, MaxXmlCharsToStore))
            {
                // If we can initialize the columns using the column schema, use that
                if (!DataReader.DbDataReader.CanGetColumnSchema())
                {
                    throw new InvalidOperationException(SR.QueryServiceResultSetNoColumnSchema);
                }
                Columns = DataReader.Columns;

                while (await DataReader.ReadAsync(cancellationToken))
                {
                    RowCount++;
                    FileOffsets.Add(currentFileOffset);
                    currentFileOffset += fileWriter.WriteRow(DataReader);
                }
            }
            // Check if resultset is 'for xml/json'. If it is, set isJson/isXml value in column metadata
            SingleColumnXmlJsonResultSet();

            return priorBytes + currentFileOffset;
        }

        #endregion

        #region IDisposable Implementation

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposed)
            {
                return;
            }

            if (disposing)
            {
                fileStreamReader?.Dispose();
                fileStreamFactory.DisposeFile(outputFileName);
            }

            disposed = true;
        }

        #endregion

        #region Private Helper Methods

        /// <summary>
        /// If the result set represented by this class corresponds to a single XML
        /// column that contains results of "for xml" query, set isXml = true 
        /// If the result set represented by this class corresponds to a single JSON
        /// column that contains results of "for json" query, set isJson = true
        /// </summary>
        private void SingleColumnXmlJsonResultSet() {

            if (Columns?.Length == 1 && RowCount != 0)
            {   
                if (Columns[0].ColumnName.Equals(NameOfForXMLColumn, StringComparison.Ordinal))
                {
                    Columns[0].IsXml = true;
                    isSingleColumnXmlJsonResultSet = true;
                    RowCount = 1;
                }
                else if (Columns[0].ColumnName.Equals(NameOfForJSONColumn, StringComparison.Ordinal))
                {
                    Columns[0].IsJson = true;
                    isSingleColumnXmlJsonResultSet = true;
                    RowCount = 1;
                }                
            }
        }

        #endregion
    }
}
