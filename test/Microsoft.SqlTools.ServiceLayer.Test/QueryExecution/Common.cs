﻿//
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SqlTools.ServiceLayer.Connection;
using Microsoft.SqlTools.ServiceLayer.Connection.Contracts;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlTools.ServiceLayer.LanguageServices;
using Microsoft.SqlTools.ServiceLayer.QueryExecution;
using Microsoft.SqlTools.ServiceLayer.QueryExecution.Contracts;
using Microsoft.SqlTools.ServiceLayer.QueryExecution.DataStorage;
using Microsoft.SqlTools.ServiceLayer.SqlContext;
using Microsoft.SqlTools.ServiceLayer.Test.Utility;
using Microsoft.SqlTools.ServiceLayer.Workspace;
using Microsoft.SqlTools.ServiceLayer.Workspace.Contracts;
using Moq;
using Moq.Protected;

namespace Microsoft.SqlTools.ServiceLayer.Test.QueryExecution
{
    public class Common
    {
        public const SelectionData WholeDocument = null;
        
        public const string StandardQuery = "SELECT * FROM sys.objects";

        public const string InvalidQuery = "SELECT *** FROM sys.objects";

        public const string NoOpQuery = "-- No ops here, just us chickens.";

        public const string UdtQuery = "SELECT hierarchyid::Parse('/')";

        public const string OwnerUri = "testFile";

        public const int StandardRows = 5;

        public const int StandardColumns = 5;

        public static string TestServer { get; set; }

        public static string TestDatabase { get; set; }

        static Common()
        {
            TestServer = "sqltools11";
            TestDatabase = "master";
        }

        public static Dictionary<string, string>[] StandardTestData
        {
            get { return GetTestData(StandardRows, StandardColumns); }
        }

        public static Dictionary<string, string>[] GetTestData(int columns, int rows)
        {
            Dictionary<string, string>[] output = new Dictionary<string, string>[rows];
            for (int row = 0; row < rows; row++)
            {
                Dictionary<string, string> rowDictionary = new Dictionary<string, string>();
                for (int column = 0; column < columns; column++)
                {
                    rowDictionary.Add(string.Format("column{0}", column), string.Format("val{0}{1}", column, row));
                }
                output[row] = rowDictionary;
            }

            return output;
        }

        public static SelectionData GetSubSectionDocument() 
        {
            return new SelectionData(0, 0, 2, 2);
        }

        public static Batch GetBasicExecutedBatch()
        {
            Batch batch = new Batch(StandardQuery, 0, 0, 2, 2, GetFileStreamFactory());
            batch.Execute(CreateTestConnection(new[] {StandardTestData}, false), null, CancellationToken.None).Wait();
            return batch;
        }

        public static Query GetBasicExecutedQuery()
        {
            ConnectionInfo ci = CreateTestConnectionInfo(new[] {StandardTestData}, false);
            Query query = new Query(StandardQuery, ci, new QueryExecutionSettings(), GetFileStreamFactory());
            query.Execute();
            query.ExecutionTask.Wait();
            return query;
        }

        #region FileStreamWriteMocking 

        public static IFileStreamFactory GetFileStreamFactory(long? maxBytesToWrite = null)
        {
            var writer = new ServiceBufferFileStreamWriter(new InMemoryWrapper(), It.IsAny<string>(), maxBytesToWrite, 1024, 1024);
            var reader = new ServiceBufferFileStreamReader(new InMemoryWrapper(), It.IsAny<string>());

            Mock<IFileStreamFactory> mock = new Mock<IFileStreamFactory>();
            mock.Setup(fsf => fsf.GetReader(It.IsAny<string>()))
                .Returns(reader);
            mock.Setup(fsf => fsf.GetWriter(It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<int>(), It.IsAny<int>()))
                .Returns(writer);

            return mock.Object;
        }

        public class InMemoryWrapper : IFileStreamWrapper
        {
            private readonly byte[] storage = new byte[8192];
            private readonly MemoryStream memoryStream;
            private long? maxBytesToWrite;
            private bool readingOnly;

            public InMemoryWrapper()
            {
                memoryStream = new MemoryStream(storage);
            }

            public void Dispose()
            {
                // We'll dispose this via a special method
            }

            public void Init(string fileName, int bufferSize, FileAccess fAccess, long? maxBytes)
            {
                readingOnly = fAccess == FileAccess.Read;
                maxBytesToWrite = maxBytes;
            }

            public int ReadData(byte[] buffer, int bytes)
            {
                return ReadData(buffer, bytes, memoryStream.Position);
            }

            public int ReadData(byte[] buffer, int bytes, long fileOffset)
            {
                memoryStream.Seek(fileOffset, SeekOrigin.Begin);
                return memoryStream.Read(buffer, 0, bytes);
            }

            public int WriteData(byte[] buffer, int bytes)
            {
                if (readingOnly) { throw new InvalidOperationException(); }
                if (maxBytesToWrite.HasValue && bytes > maxBytesToWrite.Value - memoryStream.Length)
                {
                    Debug.Assert(maxBytesToWrite.Value - memoryStream.Length < int.MaxValue);
                    bytes = (int)(maxBytesToWrite.Value - memoryStream.Length);
                }

                memoryStream.Write(buffer, 0, bytes);
                memoryStream.Flush();
                if (maxBytesToWrite.HasValue && memoryStream.Length == maxBytesToWrite.Value)
                {
                    throw new TempStorageLimitException();
                }

                return bytes;
            }

            public void Flush()
            {
                if (readingOnly) { throw new InvalidOperationException(); }
            }

            public void Close()
            {
                memoryStream.Dispose();
            }
        }

        #endregion

        #region DbConnection Mocking

        public static DbCommand CreateTestCommand(Dictionary<string, string>[][] data, bool throwOnRead)
        {
            var commandMock = new Mock<DbCommand> { CallBase = true };
            var commandMockSetup = commandMock.Protected()
                .Setup<DbDataReader>("ExecuteDbDataReader", It.IsAny<CommandBehavior>());

            // Setup the expected behavior
            if (throwOnRead)
            {
                var mockException = new Mock<DbException>();
                mockException.SetupGet(dbe => dbe.Message).Returns("Message");
                commandMockSetup.Throws(mockException.Object);
            }
            else
            {
                commandMockSetup.Returns(new TestDbDataReader(data));
            }
                

            return commandMock.Object;
        }

        public static DbConnection CreateTestConnection(Dictionary<string, string>[][] data, bool throwOnRead)
        {
            var connectionMock = new Mock<DbConnection> { CallBase = true };
            connectionMock.Protected()
                .Setup<DbCommand>("CreateDbCommand")
                .Returns(CreateTestCommand(data, throwOnRead));
            connectionMock.Setup(dbc => dbc.Open())
                .Callback(() => connectionMock.SetupGet(dbc => dbc.State).Returns(ConnectionState.Open));
            connectionMock.Setup(dbc => dbc.Close())
                .Callback(() => connectionMock.SetupGet(dbc => dbc.State).Returns(ConnectionState.Closed));

            return connectionMock.Object;
        }

        public static ISqlConnectionFactory CreateMockFactory(Dictionary<string, string>[][] data, bool throwOnRead)
        {
            var mockFactory = new Mock<ISqlConnectionFactory>();
            mockFactory.Setup(factory => factory.CreateSqlConnection(It.IsAny<string>()))
                .Returns(CreateTestConnection(data, throwOnRead));

            return mockFactory.Object;
        }

        public static ConnectionInfo CreateTestConnectionInfo(Dictionary<string, string>[][] data, bool throwOnRead)
        {
            // Create connection info
            ConnectionDetails connDetails = new ConnectionDetails
            {
                UserName = "sa",
                Password = "Yukon900",
                DatabaseName = Common.TestDatabase,
                ServerName = Common.TestServer
            };

            return new ConnectionInfo(CreateMockFactory(data, throwOnRead), OwnerUri, connDetails);
        }

        #endregion

        #region Service Mocking
        
        public static void GetAutoCompleteTestObjects(
            out TextDocumentPosition textDocument,
            out ScriptFile scriptFile,
            out ConnectionInfo connInfo
        )
        {
            textDocument = new TextDocumentPosition
            {
                TextDocument = new TextDocumentIdentifier {Uri = OwnerUri},
                Position = new Position
                {
                    Line = 0,
                    Character = 0
                }
            };

            connInfo = Common.CreateTestConnectionInfo(null, false);

            LanguageService.Instance.ScriptParseInfoMap.Add(textDocument.TextDocument.Uri,  new ScriptParseInfo());

            scriptFile = new ScriptFile {ClientFilePath = textDocument.TextDocument.Uri};

        }

        public static ServerConnection GetServerConnection(ConnectionInfo connection)
        {
            string connectionString = ConnectionService.BuildConnectionString(connection.ConnectionDetails);
            var sqlConnection = new SqlConnection(connectionString);
            return new ServerConnection(sqlConnection);
        }
        
        public static ConnectionDetails GetTestConnectionDetails()
        {
            return new ConnectionDetails
            {
                DatabaseName = "123",
                Password = "456",
                ServerName = "789",
                UserName = "012"
            };
        }

        public static async Task<QueryExecutionService> GetPrimedExecutionService(ISqlConnectionFactory factory, bool isConnected, WorkspaceService<SqlToolsSettings> workspaceService)
        {
            var connectionService = new ConnectionService(factory);
            if (isConnected)
            {
                await connectionService.Connect(new ConnectParams
                {
                    Connection = GetTestConnectionDetails(),
                    OwnerUri = OwnerUri
                });
            }
            return new QueryExecutionService(connectionService, workspaceService) {BufferFileStreamFactory = GetFileStreamFactory()};
        }

        public static WorkspaceService<SqlToolsSettings> GetPrimedWorkspaceService()
        {
            // Set up file for returning the query
            var fileMock = new Mock<ScriptFile>();
            fileMock.SetupGet(file => file.Contents).Returns(StandardQuery);
           
            // Set up workspace mock
            var workspaceService = new Mock<WorkspaceService<SqlToolsSettings>>();
            workspaceService.Setup(service => service.Workspace.GetFile(It.IsAny<string>()))
                .Returns(fileMock.Object);

            return workspaceService.Object;
        }

        #endregion
        
    }
}
