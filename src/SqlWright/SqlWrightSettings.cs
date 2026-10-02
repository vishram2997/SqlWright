using System;
using System.Data;
using SqlWright.Internal;

namespace SqlWright
{
    /// <summary>
    /// Global configuration for SqlWright. Set these once at startup.
    /// </summary>
    public static class SqlWrightSettings
    {
        private static bool _matchNamesWithUnderscores;
        private static bool _strictMapping;
        private static NamingStyle _namingStyle;
        private static Func<Type, string>? _tableNameResolver;

        /// <summary>
        /// When <c>true</c>, a column such as <c>first_name</c> maps to a member named <c>FirstName</c>.
        /// Defaults to <c>false</c>.
        /// </summary>
        public static bool MatchNamesWithUnderscores
        {
            get => _matchNamesWithUnderscores;
            set
            {
                if (_matchNamesWithUnderscores == value) return;
                _matchNamesWithUnderscores = value;
                RowMapper.ClearCache();
            }
        }

        /// <summary>
        /// When <c>true</c>, a result column that matches no property or field throws a <see cref="MappingException"/>
        /// instead of being silently ignored. Useful in development and tests to catch typos and schema drift.
        /// Defaults to <c>false</c>.
        /// </summary>
        public static bool StrictMapping
        {
            get => _strictMapping;
            set
            {
                if (_strictMapping == value) return;
                _strictMapping = value;
                RowMapper.ClearCache();
            }
        }

        /// <summary>
        /// How the CRUD helpers derive table and column names from type and member names when there is no
        /// <c>[Table]</c> or <c>[Column]</c> attribute. Defaults to <see cref="NamingStyle.AsIs"/>.
        /// </summary>
        public static NamingStyle NamingStyle
        {
            get => _namingStyle;
            set
            {
                _namingStyle = value;
                EntityInfo.ClearCache();
            }
        }

        /// <summary>
        /// Overrides how the CRUD helpers name tables for types without a <c>[Table]</c> attribute,
        /// e.g. <c>type =&gt; type.Name + "s"</c>. The result is used as-is (not passed through <see cref="NamingStyle"/>).
        /// </summary>
        public static Func<Type, string>? TableNameResolver
        {
            get => _tableNameResolver;
            set
            {
                _tableNameResolver = value;
                EntityInfo.ClearCache();
            }
        }

        /// <summary>
        /// The dialect used for connection types SqlWright doesn't recognise and that haven't been registered.
        /// <c>null</c> (the default) means such connections can't use the CRUD helpers.
        /// </summary>
        public static SqlDialect? DefaultDialect { get; set; }

        /// <summary>
        /// Default command timeout, in seconds, applied when a call does not specify one.
        /// <c>null</c> leaves the provider default in place.
        /// </summary>
        public static int? CommandTimeout { get; set; }

        /// <summary>
        /// Tells SqlWright which dialect to use for a connection type, e.g. a provider it doesn't know
        /// or a wrapper such as a profiling connection.
        /// </summary>
        public static void RegisterDialect<TConnection>(SqlDialect dialect) where TConnection : IDbConnection =>
            SqlDialect.Register(typeof(TConnection), dialect ?? throw new ArgumentNullException(nameof(dialect)));

        /// <summary>
        /// Clears all cached row mappers, parameter readers and entity metadata.
        /// </summary>
        public static void ClearCache()
        {
            RowMapper.ClearCache();
            ParameterReader.ClearCache();
            EntityInfo.ClearCache();
        }
    }

    /// <summary>How CRUD helpers turn .NET names into database names.</summary>
    public enum NamingStyle
    {
        /// <summary>Use type and member names unchanged: <c>FirstName</c>.</summary>
        AsIs,

        /// <summary>Convert to snake_case: <c>FirstName</c> becomes <c>first_name</c>.</summary>
        SnakeCase,
    }
}
