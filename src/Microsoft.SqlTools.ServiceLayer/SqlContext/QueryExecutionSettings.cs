// 
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
namespace Microsoft.SqlTools.ServiceLayer.SqlContext
{
    /// <summary>
    /// Collection of settings related to the execution of queries
    /// </summary>
    public class QueryExecutionSettings
    {
        #region Constants

        /// <summary>
        /// Default value for batch separator (de facto standard as per SSMS)
        /// </summary>
        private const string DefaultBatchSeparator = "GO";

        /// <summary>
        /// Default value for maximum number of bytes to store in the temporary file
        /// </summary>
        private const long DefaultMaxTemporaryBytes = 2L*1024*1024*1024;

        #endregion

        #region Member Variables

        private string batchSeparator;

        private long? maxTemporaryBytes;

        #endregion

        #region Properties

        /// <summary>
        /// The configured batch separator, will use a default if a value was not configured
        /// </summary>
        public string BatchSeparator
        {
            get { return batchSeparator ?? DefaultBatchSeparator; }
            set { batchSeparator = value; }
        }

        /// <summary>
        /// The configured maximum number of bytes to store in the temp files, will use default if
        /// a value was not configured.
        /// </summary>
        public long MaxTempBytes
        {
            get { return maxTemporaryBytes ?? DefaultMaxTemporaryBytes; }
            set { maxTemporaryBytes = value; }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Update the current settings with the new settings
        /// </summary>
        /// <param name="newSettings">The new settings</param>
        public void Update(QueryExecutionSettings newSettings)
        {
            BatchSeparator = newSettings.BatchSeparator;
        }

        #endregion
    }
}
