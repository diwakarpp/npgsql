// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Microsoft.Data.Entity.Metadata;
using Microsoft.Data.Entity.Relational;
using Microsoft.Data.Entity.Relational.Migrations.Operations;
using Npgsql.EntityFramework7.Migrations;
using Microsoft.Data.Entity.Utilities;

namespace Npgsql.EntityFramework7
{
    public class NpgsqlDataStoreCreator : RelationalDataStoreCreator, INpgsqlDataStoreCreator
    {
        private readonly INpgsqlEFConnection _connection;
        private readonly INpgsqlModelDiffer _modelDiffer;
        private readonly INpgsqlMigrationSqlGenerator _sqlGenerator;
        private readonly SqlStatementExecutor _statementExecutor;

        public NpgsqlDataStoreCreator(
            [NotNull] INpgsqlEFConnection connection,
            [NotNull] INpgsqlModelDiffer modelDiffer,
            [NotNull] INpgsqlMigrationSqlGenerator sqlGenerator,
            [NotNull] SqlStatementExecutor statementExecutor)
        {
            Check.NotNull(connection, nameof(connection));
            Check.NotNull(modelDiffer, nameof(modelDiffer));
            Check.NotNull(sqlGenerator, nameof(sqlGenerator));
            Check.NotNull(statementExecutor, nameof(statementExecutor));

            _connection = connection;
            _modelDiffer = modelDiffer;
            _sqlGenerator = sqlGenerator;
            _statementExecutor = statementExecutor;
        }

        public override void Create()
        {
            Console.WriteLine("CREATE " + );
            using (var masterConnection = _connection.CreateMasterConnection())
            {
                _statementExecutor.ExecuteNonQuery(masterConnection, null, CreateCreateOperations());
                ClearPool();
            }
        }

        public override async Task CreateAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            using (var masterConnection = _connection.CreateMasterConnection())
            {
                await _statementExecutor
                    .ExecuteNonQueryAsync(masterConnection, null, CreateCreateOperations(), cancellationToken)
                    .WithCurrentCulture();
                ClearPool();
            }
        }

        public override void CreateTables(IModel model)
        {
            Check.NotNull(model, nameof(model));

            _statementExecutor.ExecuteNonQuery(_connection, _connection.DbTransaction, CreateSchemaCommands(model));
        }

        public override async Task CreateTablesAsync(IModel model, CancellationToken cancellationToken = default(CancellationToken))
        {
            Check.NotNull(model, nameof(model));

            await _statementExecutor
                .ExecuteNonQueryAsync(_connection, _connection.DbTransaction, CreateSchemaCommands(model), cancellationToken)
                .WithCurrentCulture();
        }

        public override bool HasTables()
            => (int)_statementExecutor.ExecuteScalar(_connection, _connection.DbTransaction, CreateHasTablesCommand()) != 0;

        public override async Task<bool> HasTablesAsync(CancellationToken cancellationToken = default(CancellationToken))
            => (int)(await _statementExecutor
                .ExecuteScalarAsync(_connection, _connection.DbTransaction, CreateHasTablesCommand(), cancellationToken)
                .WithCurrentCulture()) != 0;

        private IEnumerable<SqlBatch> CreateSchemaCommands(IModel model)
            => _sqlGenerator.Generate(_modelDiffer.GetDifferences(null, model), model);

        private string CreateHasTablesCommand()
            => @"
                 SELECT CASE WHEN COUNT(*) = 0 THEN 0 ELSE 1 END
                 FROM information_schema.tables
                 WHERE table_type = 'BASE TABLE' AND table_schema NOT IN ('pg_catalog', 'information_schema')
               ";

        private IEnumerable<SqlBatch> CreateCreateOperations()
            => _sqlGenerator.Generate(new[] { new CreateDatabaseOperation { Name = _connection.DbConnection.Database } });

        public override bool Exists()
        {
            try
            {
                _connection.Open();
                _connection.Close();
                return true;
            }
            catch (NpgsqlException e)
            {
                if (IsDoesNotExist(e))
                {
                    return false;
                }

                throw;
            }
        }

        public override async Task<bool> ExistsAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                await _connection.OpenAsync(cancellationToken).WithCurrentCulture();
                _connection.Close();
                return true;
            }
            catch (NpgsqlException e)
            {
                if (IsDoesNotExist(e))
                {
                    return false;
                }

                throw;
            }
        }

        // Login failed is thrown when database does not exist (See Issue #776)
        private static bool IsDoesNotExist(NpgsqlException exception) => exception.Code == "3D000";

        public override void Delete()
        {
            ClearAllPools();

            using (var masterConnection = _connection.CreateMasterConnection())
            {
                _statementExecutor.ExecuteNonQuery(masterConnection, null, CreateDropCommands());
            }
        }

        public override async Task DeleteAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            ClearAllPools();

            using (var masterConnection = _connection.CreateMasterConnection())
            {
                await _statementExecutor
                    .ExecuteNonQueryAsync(masterConnection, null, CreateDropCommands(), cancellationToken)
                    .WithCurrentCulture();
            }
        }

        private IEnumerable<SqlBatch> CreateDropCommands()
        {
            var operations = new MigrationOperation[]
                {
                    // TODO Check DbConnection.Database always gives us what we want
                    // Issue #775
                    new DropDatabaseOperation { Name = _connection.DbConnection.Database }
                };

            var masterCommands = _sqlGenerator.Generate(operations);
            return masterCommands;
        }

        // Clear connection pools in case there are active connections that are pooled
        private static void ClearAllPools() => NpgsqlConnection.ClearAllPools();

        // Clear connection pool for the database connection since after the 'create database' call, a previously
        // invalid connection may now be valid.
        private void ClearPool() => NpgsqlConnection.ClearPool((NpgsqlConnection)_connection.DbConnection);
    }
}
