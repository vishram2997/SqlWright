using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;

namespace SqlWright
{
    /// <summary>
    /// Database-specific SQL generation used by the CRUD helpers (<c>Insert</c>, <c>Update</c>, <c>Get</c>, ...)
    /// and for the parameter prefix used by interpolated <see cref="Sql"/>.
    /// </summary>
    /// <remarks>
    /// The dialect is detected from the connection type. For other providers, or wrapped connections
    /// (e.g. profilers), call <see cref="SqlWrightSettings.RegisterDialect{TConnection}"/>.
    /// Subclass this to support another database.
    /// </remarks>
    public abstract class SqlDialect
    {
        /// <summary>Microsoft SQL Server and Azure SQL.</summary>
        public static SqlDialect SqlServer { get; } = new SqlServerDialect();

        /// <summary>PostgreSQL.</summary>
        public static SqlDialect PostgreSql { get; } = new PostgreSqlDialect();

        /// <summary>SQLite.</summary>
        public static SqlDialect Sqlite { get; } = new SqliteDialect();

        /// <summary>MySQL and MariaDB.</summary>
        public static SqlDialect MySql { get; } = new MySqlDialect();

        /// <summary>The prefix for named parameters in SQL text.</summary>
        public virtual string ParameterPrefix => "@";

        /// <summary>Quotes a single identifier (table, column or schema name), escaping as needed.</summary>
        public abstract string QuoteIdentifier(string name);

        /// <summary>Quotes a table name, optionally qualified with a schema.</summary>
        public virtual string QuoteTableName(string table, string? schema = null) =>
            string.IsNullOrEmpty(schema) ? QuoteIdentifier(table) : QuoteIdentifier(schema!) + "." + QuoteIdentifier(table);

        /// <summary>
        /// Builds an INSERT. When <paramref name="generatedKeyColumn"/> is set, the statement must also return the
        /// generated key as its single scalar result.
        /// </summary>
        /// <param name="quotedTable">The table, already quoted.</param>
        /// <param name="columns">Unquoted column names to insert.</param>
        /// <param name="parameterNames">Parameter names (no prefix), parallel to <paramref name="columns"/>.</param>
        /// <param name="generatedKeyColumn">Unquoted name of a database-generated key column, if any.</param>
        public virtual string BuildInsert(string quotedTable, IReadOnlyList<string> columns, IReadOnlyList<string> parameterNames, string? generatedKeyColumn)
        {
            var sb = new StringBuilder("INSERT INTO ").Append(quotedTable);
            if (columns.Count == 0)
            {
                sb.Append(' ').Append(EmptyInsertClause);
            }
            else
            {
                sb.Append(" (").Append(string.Join(", ", columns.Select(QuoteIdentifier))).Append(") VALUES (")
                  .Append(string.Join(", ", parameterNames.Select(p => ParameterPrefix + p))).Append(')');
            }

            if (generatedKeyColumn != null) sb.Append(ReturnGeneratedKey(QuoteIdentifier(generatedKeyColumn)));
            return sb.ToString();
        }

        /// <summary>The clause used to insert a row with no explicit column values.</summary>
        protected virtual string EmptyInsertClause => "DEFAULT VALUES";

        /// <summary>SQL appended to an INSERT so it returns the generated key.</summary>
        protected abstract string ReturnGeneratedKey(string quotedKeyColumn);

        /// <summary>
        /// The clause that limits a query to <paramref name="limit"/> rows after skipping <paramref name="offset"/>,
        /// appended after any ORDER BY. Both numbers are sent as parameters.
        /// </summary>
        /// <param name="limit">Maximum rows to return.</param>
        /// <param name="offset">Rows to skip.</param>
        /// <param name="hasOrderBy">Whether the query already has an ORDER BY (some databases require one).</param>
        public virtual Sql Paginate(int limit, int offset, bool hasOrderBy)
        {
            Sql clause = $" LIMIT {limit} OFFSET {offset}";
            return clause;
        }

        /// <summary>The escape character used by <see cref="EscapeLikePattern"/>.</summary>
        public virtual char LikeEscapeCharacter => '!';

        /// <summary>
        /// Escapes LIKE wildcards in <paramref name="value"/> so it matches literally. Use with
        /// <c>LIKE @p ESCAPE '!'</c> (see <see cref="LikeEscapeCharacter"/>).
        /// </summary>
        public virtual string EscapeLikePattern(string value)
        {
            var e = LikeEscapeCharacter.ToString();
            return value.Replace(e, e + e).Replace("%", e + "%").Replace("_", e + "_");
        }

        /// <summary>Whether the database supports FULL OUTER JOIN.</summary>
        public virtual bool SupportsFullOuterJoin => true;

        private sealed class SqlServerDialect : SqlDialect
        {
            public override string QuoteIdentifier(string name) => "[" + name.Replace("]", "]]") + "]";

            public override Sql Paginate(int limit, int offset, bool hasOrderBy)
            {
                // OFFSET/FETCH is only valid after an ORDER BY. "ORDER BY (SELECT NULL)" would avoid a sort but is
                // rejected with SELECT DISTINCT; ordering by the first selected column works for every query shape.
                var clause = hasOrderBy ? new Sql() : Sql.Raw(" ORDER BY 1");
                clause.Append($" OFFSET {offset} ROWS FETCH NEXT {limit} ROWS ONLY");
                return clause;
            }

            // '[' starts a character class in SQL Server's LIKE.
            public override string EscapeLikePattern(string value) => base.EscapeLikePattern(value).Replace("[", "![");

            // SCOPE_IDENTITY rather than OUTPUT INSERTED, because OUTPUT fails on tables with triggers.
            protected override string ReturnGeneratedKey(string quotedKeyColumn) => "; SELECT CAST(SCOPE_IDENTITY() AS bigint)";
        }

        private sealed class PostgreSqlDialect : SqlDialect
        {
            public override string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

            protected override string ReturnGeneratedKey(string quotedKeyColumn) => " RETURNING " + quotedKeyColumn;
        }

        private sealed class SqliteDialect : SqlDialect
        {
            public override string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

            protected override string ReturnGeneratedKey(string quotedKeyColumn) => "; SELECT last_insert_rowid()";
        }

        private sealed class MySqlDialect : SqlDialect
        {
            public override string QuoteIdentifier(string name) => "`" + name.Replace("`", "``") + "`";

            protected override string EmptyInsertClause => "() VALUES ()";

            public override bool SupportsFullOuterJoin => false;

            protected override string ReturnGeneratedKey(string quotedKeyColumn) => "; SELECT LAST_INSERT_ID()";
        }

        // ---- Resolution ----

        private static readonly ConcurrentDictionary<Type, SqlDialect> Registered = new ConcurrentDictionary<Type, SqlDialect>();

        private static readonly Dictionary<string, SqlDialect> KnownConnectionTypes = new Dictionary<string, SqlDialect>(StringComparer.OrdinalIgnoreCase)
        {
            ["SqlConnection"] = SqlServer,       // System.Data.SqlClient, Microsoft.Data.SqlClient
            ["NpgsqlConnection"] = PostgreSql,
            ["SqliteConnection"] = Sqlite,       // Microsoft.Data.Sqlite, System.Data.SQLite
            ["MySqlConnection"] = MySql,         // MySql.Data, MySqlConnector
        };

        internal static void Register(Type connectionType, SqlDialect dialect) => Registered[connectionType] = dialect;

        internal static SqlDialect? Find(IDbConnection connection)
        {
            var type = connection.GetType();
            if (Registered.TryGetValue(type, out var dialect)) return dialect;
            if (KnownConnectionTypes.TryGetValue(type.Name, out dialect)) return dialect;
            return SqlWrightSettings.DefaultDialect;
        }

        /// <summary>
        /// The dialect for a connection: a registered one, a recognised provider, or <see cref="SqlWrightSettings.DefaultDialect"/>.
        /// Throws if none applies.
        /// </summary>
        public static SqlDialect For(IDbConnection connection) =>
            Find(connection) ?? throw new SqlWrightException(
                $"SqlWright doesn't know which SQL dialect to use for {connection.GetType().FullName}. " +
                $"Register one at startup, e.g. SqlWrightSettings.RegisterDialect<{connection.GetType().Name}>(SqlDialect.SqlServer), " +
                "or set SqlWrightSettings.DefaultDialect.");
    }
}
