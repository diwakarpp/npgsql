﻿#region License
// The PostgreSQL License
//
// Copyright (C) 2018 The Npgsql Development Team
//
// Permission to use, copy, modify, and distribute this software and its
// documentation for any purpose, without fee, and without a written
// agreement is hereby granted, provided that the above copyright notice
// and this paragraph and the following two paragraphs appear in all copies.
//
// IN NO EVENT SHALL THE NPGSQL DEVELOPMENT TEAM BE LIABLE TO ANY PARTY
// FOR DIRECT, INDIRECT, SPECIAL, INCIDENTAL, OR CONSEQUENTIAL DAMAGES,
// INCLUDING LOST PROFITS, ARISING OUT OF THE USE OF THIS SOFTWARE AND ITS
// DOCUMENTATION, EVEN IF THE NPGSQL DEVELOPMENT TEAM HAS BEEN ADVISED OF
// THE POSSIBILITY OF SUCH DAMAGE.
//
// THE NPGSQL DEVELOPMENT TEAM SPECIFICALLY DISCLAIMS ANY WARRANTIES,
// INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY
// AND FITNESS FOR A PARTICULAR PURPOSE. THE SOFTWARE PROVIDED HEREUNDER IS
// ON AN "AS IS" BASIS, AND THE NPGSQL DEVELOPMENT TEAM HAS NO OBLIGATIONS
// TO PROVIDE MAINTENANCE, SUPPORT, UPDATES, ENHANCEMENTS, OR MODIFICATIONS.
#endregion

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Runtime.InteropServices.ComTypes;
using System.Threading.Tasks;
using Npgsql.PostgresTypes;
using Npgsql.TypeMapping;

namespace Npgsql
{
    /// <summary>
    /// Base class for implementations which provide information about PostgreSQL and PostgreSQL-like databases
    /// (e.g. type definitions, capabilities...).
    /// </summary>
    public abstract class NpgsqlDatabaseInfo
    {
        #region Fields

        internal static ConcurrentDictionary<string, NpgsqlDatabaseInfo> Cache
            = new ConcurrentDictionary<string, NpgsqlDatabaseInfo>();

        static readonly List<INpgsqlDatabaseInfoFactory> Factories = new List<INpgsqlDatabaseInfoFactory>
        {
            new PostgresMinimalDatabaseInfoFactory(),
            new PostgresDatabaseInfoFactory()
        };

        #endregion Fields

        #region General database info

        /// <summary>
        /// The hostname of IP address of the database.
        /// </summary>
        public string Host { get; protected set; }
        /// <summary>
        /// The TCP port of the database.
        /// </summary>
        public int Port { get; protected set; }
        /// <summary>
        /// The database name.
        /// </summary>
        public string Name { get; protected set; }
        /// <summary>
        /// The version of the PostgreSQL database we're connected to, as reported in the "server_version" parameter.
        /// Exposed via <see cref="NpgsqlConnection.PostgreSqlVersion"/>.
        /// </summary>
        public Version Version { get; protected set; }

        #endregion General database info

        #region Supported capabilities and features

        /// <summary>
        /// Whether the backend supports range types.
        /// </summary>
        public virtual bool SupportsRangeTypes => Version >= new Version(9, 2, 0);
        /// <summary>
        /// Whether the backend supports enum types.
        /// </summary>
        public virtual bool SupportsEnumTypes => Version >= new Version(8, 3, 0);
        /// <summary>
        /// Whether the backend supports the CLOSE ALL statement.
        /// </summary>
        public virtual bool SupportsCloseAll => Version >= new Version(8, 3, 0);
        /// <summary>
        /// Whether the backend supports advisory locks.
        /// </summary>
        public virtual bool SupportsAdvisoryLocks => Version >= new Version(8, 2, 0);
        /// <summary>
        /// Whether the backend supports the DISCARD SEQUENCES statement.
        /// </summary>
        public virtual bool SupportsDiscardSequences => Version >= new Version(9, 4, 0);
        /// <summary>
        /// Whether the backend supports the UNLISTEN statement.
        /// </summary>
        public virtual bool SupportsUnlisten => Version >= new Version(6, 4, 0);  // overridden by PostgresDatabase
        /// <summary>
        /// Whether the backend supports the DISCARD TEMP statement.
        /// </summary>
        public virtual bool SupportsDiscardTemp => Version >= new Version(8, 3, 0);
        /// <summary>
        /// Whether the backend supports the DISCARD statement.
        /// </summary>
        public virtual bool SupportsDiscard => Version >= new Version(8, 3, 0);

        /// <summary>
        /// Reports whether the backend uses the newer integer timestamp representation.
        /// </summary>
        public virtual bool HasIntegerDateTimes { get; protected set; } = true;

        /// <summary>
        /// Whether the database supports transactions.
        /// </summary>
        public virtual bool SupportsTransactions { get; protected set; } = true;

        #endregion Supported capabilities and features

        #region Types

        internal IReadOnlyList<PostgresBaseType>      BaseTypes      { get; private set; }
        internal IReadOnlyList<PostgresArrayType>     ArrayTypes     { get; private set; }
        internal IReadOnlyList<PostgresRangeType>     RangeTypes     { get; private set; }
        internal IReadOnlyList<PostgresEnumType>      EnumTypes      { get; private set; }
        internal IReadOnlyList<PostgresCompositeType> CompositeTypes { get; private set; }
        internal IReadOnlyList<PostgresDomainType>    DomainTypes    { get; private set; }

        /// <summary>
        /// Indexes backend types by their type OID.
        /// </summary>
        internal Dictionary<uint, PostgresType> ByOID { get; } = new Dictionary<uint, PostgresType>();

        /// <summary>
        /// Indexes backend types by their PostgreSQL name, including namespace (e.g. pg_catalog.int4).
        /// Only used for enums and composites.
        /// </summary>
        internal Dictionary<string, PostgresType> ByFullName { get; } = new Dictionary<string, PostgresType>();

        /// <summary>
        /// Indexes backend types by their PostgreSQL name, not including namespace.
        /// If more than one type exists with the same name (i.e. in different namespaces) this
        /// table will contain an entry with a null value.
        /// Only used for enums and composites.
        /// </summary>
        internal Dictionary<string, PostgresType> ByName { get; } = new Dictionary<string, PostgresType>();

        internal void ProcessTypes()
        {
            var baseTypes      = new List<PostgresBaseType>();
            var arrayTypes     = new List<PostgresArrayType>();
            var rangeTypes     = new List<PostgresRangeType>();
            var enumTypes      = new List<PostgresEnumType>();
            var compositeTypes = new List<PostgresCompositeType>();
            var domainTypes    = new List<PostgresDomainType>();

            foreach (var type in GetTypes())
            {
                ByOID[type.OID] = type;
                ByFullName[type.FullName] = type;
                // If more than one type exists with the same partial name, we place a null value.
                // This allows us to detect this case later and force the user to use full names only.
                ByName[type.Name] = ByName.ContainsKey(type.Name)
                    ? null
                    : type;

                switch (type)
                {
                case PostgresBaseType baseType:
                    baseTypes.Add(baseType);
                    continue;
                case PostgresArrayType arrayType:
                    arrayTypes.Add(arrayType);
                    continue;
                case PostgresRangeType rangeType:
                    rangeTypes.Add(rangeType);
                    continue;
                case PostgresEnumType enumType:
                    enumTypes.Add(enumType);
                    continue;
                case PostgresCompositeType compositeType:
                    compositeTypes.Add(compositeType);
                    continue;
                case PostgresDomainType domainType:
                    domainTypes.Add(domainType);
                    continue;
                default:
                    throw new ArgumentOutOfRangeException();
                }
            }

            BaseTypes      = baseTypes;
            ArrayTypes     = arrayTypes;
            RangeTypes     = rangeTypes;
            EnumTypes      = enumTypes;
            CompositeTypes = compositeTypes;
            DomainTypes    = domainTypes;
        }

        /// <summary>
        /// Provides all PostgreSQL types detected in this database.
        /// </summary>
        /// <returns></returns>
        protected abstract IEnumerable<PostgresType> GetTypes();

        /// <summary>
        /// Adapts the type mappings for this database.
        /// </summary>
        /// <param name="mappings">The mappings that are about the be bound.</param>
        protected internal virtual void AdaptTypeMappings(IDictionary<string, NpgsqlTypeMapping> mappings) { }

        #endregion Types

        #region Misc

        /// <summary>
        /// Parses a PostgreSQL server version (e.g. 10.1, 9.6.3) and returns a CLR Version.
        /// </summary>
        protected static Version ParseServerVersion(string value)
        {
            var versionString = value.Trim();
            for (var idx = 0; idx != versionString.Length; ++idx)
            {
                var c = value[idx];
                if (!char.IsDigit(c) && c != '.')
                {
                    versionString = versionString.Substring(0, idx);
                    break;
                }
            }
            if (!versionString.Contains("."))
                versionString += ".0";
            return new Version(versionString);
        }

        #endregion Misc

        #region Factory management

        /// <summary>
        /// Registers a new database info factory, which is used to load information about databases.
        /// </summary>
        public static void RegisterFactory(INpgsqlDatabaseInfoFactory factory)
        {
            if (factory == null)
                throw new ArgumentNullException(nameof(factory));
            Factories.Insert(0, factory);
        }

        internal static async Task<NpgsqlDatabaseInfo> Load(NpgsqlConnection conn, NpgsqlTimeout timeout, bool async)
        {
            foreach (var factory in Factories)
            {
                var dbInfo = await factory.Load(conn, timeout, async);
                if (dbInfo != null)
                {
                    dbInfo.ProcessTypes();
                    return dbInfo;
                }
            }

            // Should never be here
            throw new NpgsqlException("No DatabaseInfoFactory could be found for this connection");
        }

        // For tests
        internal static void ResetFactories()
        {
            Factories.Clear();
            Factories.Add(new PostgresMinimalDatabaseInfoFactory());
            Factories.Add(new PostgresDatabaseInfoFactory());
        }

        #endregion Factory management

        #region GAC plugin support
        /// <summary>
        /// Checks if the assembly is loaded from the global assembly cache and loads globally installed database info factories if this is the case.
        /// </summary>
        static NpgsqlDatabaseInfo()
        {
            if (typeof(NpgsqlDatabaseInfo).Assembly.GlobalAssemblyCache)
            {
                LoadRegisteredFactories();
            }
        }

        /// <summary>
        /// Reads metadata about globally registered database info factories and tries to load and register those types.
        /// </summary>
        /// <remarks>
        /// This is only intended for the GAC use case. Applications like Microsoft Power BI have support
        /// for Npgsql by loading it from the GAC. But there is no way to register custom database info factories
        /// in that scenario. To overcome this problem, custom database info factories may be installed and 
        /// registered globally and this method processes that registry accordingly by trying to load and register each type.
        /// </remarks>
        internal static void LoadRegisteredFactories()
        {
            foreach (var factoryMetadata in NpgsqlGlobalConfiguration.Current.DatabaseInfoFactories)
            {
                TryRegisterFactoryByAssemblyQualifiedName(factoryMetadata.TypeName);
            }
        }

        static bool TryRegisterFactoryByAssemblyQualifiedName(string s)
        {
            if (!string.IsNullOrWhiteSpace(s))
            {
                try
                {
                    var factoryType = Type.GetType(s);
                    if (typeof(INpgsqlDatabaseInfoFactory).IsAssignableFrom(factoryType))
                    {
                        if (Activator.CreateInstance(factoryType) is INpgsqlDatabaseInfoFactory factory)
                        {
                            NpgsqlDatabaseInfo.RegisterFactory(factory);
                            return true;
                        }
                    }
                }
                catch { }
            }
            return false;
        }
        #endregion
    }
}
