using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using SqlWright.Internal;

namespace SqlWright
{
    /// <remarks>
    /// Overloads taking <see cref="Sql"/>: pass an interpolated string and every <c>{value}</c> becomes a parameter.
    /// <code>
    /// var user = connection.QuerySingle&lt;User&gt;($"SELECT * FROM Users WHERE Id = {id}");
    /// </code>
    /// </remarks>
    public static partial class SqlWrightExtensions
    {
        /// <summary>Executes interpolated SQL and returns the number of rows affected.</summary>
        public static int Execute(this IDbConnection connection, Sql sql, IDbTransaction? transaction = null, int? commandTimeout = null)
            => ExecuteImpl(connection, Spec(connection, sql, transaction, commandTimeout));

        /// <summary>Executes interpolated SQL and returns the first column of the first row, converted to <typeparamref name="T"/>.</summary>
        public static T? ExecuteScalar<T>(this IDbConnection connection, Sql sql, IDbTransaction? transaction = null, int? commandTimeout = null)
            => ExecuteScalarImpl<T>(connection, Spec(connection, sql, transaction, commandTimeout));

        /// <summary>Runs interpolated SQL and maps each row to <typeparamref name="T"/>.</summary>
        public static IEnumerable<T> Query<T>(this IDbConnection connection, Sql sql, IDbTransaction? transaction = null, bool buffered = true, int? commandTimeout = null)
            => QueryImpl<T>(connection, Spec(connection, sql, transaction, commandTimeout), buffered);

        /// <summary>Runs interpolated SQL and returns each row as a <c>dynamic</c> object.</summary>
        public static IEnumerable<dynamic> Query(this IDbConnection connection, Sql sql, IDbTransaction? transaction = null, bool buffered = true, int? commandTimeout = null)
            => QueryImpl<object>(connection, Spec(connection, sql, transaction, commandTimeout), buffered);

        /// <summary>Runs interpolated SQL and returns the first row. Throws if there are no rows.</summary>
        public static T QueryFirst<T>(this IDbConnection connection, Sql sql, IDbTransaction? transaction = null, int? commandTimeout = null)
            => QueryRow<T>(connection, Spec(connection, sql, transaction, commandTimeout), RowMode.First);

        /// <summary>Runs interpolated SQL and returns the first row, or <c>default</c>.</summary>
        public static T? QueryFirstOrDefault<T>(this IDbConnection connection, Sql sql, IDbTransaction? transaction = null, int? commandTimeout = null)
            => QueryRow<T?>(connection, Spec(connection, sql, transaction, commandTimeout), RowMode.FirstOrDefault);

        /// <summary>Runs interpolated SQL and returns the only row. Throws unless there is exactly one.</summary>
        public static T QuerySingle<T>(this IDbConnection connection, Sql sql, IDbTransaction? transaction = null, int? commandTimeout = null)
            => QueryRow<T>(connection, Spec(connection, sql, transaction, commandTimeout), RowMode.Single);

        /// <summary>Runs interpolated SQL and returns the only row, or <c>default</c>. Throws if there is more than one.</summary>
        public static T? QuerySingleOrDefault<T>(this IDbConnection connection, Sql sql, IDbTransaction? transaction = null, int? commandTimeout = null)
            => QueryRow<T?>(connection, Spec(connection, sql, transaction, commandTimeout), RowMode.SingleOrDefault);

        /// <summary>Runs interpolated SQL that returns several result sets. Dispose the returned reader when done.</summary>
        public static MultiResultReader QueryMultiple(this IDbConnection connection, Sql sql, IDbTransaction? transaction = null, int? commandTimeout = null)
            => QueryMultipleImpl(connection, Spec(connection, sql, transaction, commandTimeout));

        /// <summary>Asynchronously executes interpolated SQL and returns the number of rows affected.</summary>
        public static Task<int> ExecuteAsync(this IDbConnection connection, Sql sql, IDbTransaction? transaction = null, int? commandTimeout = null,
            CancellationToken cancellationToken = default)
            => ExecuteAsyncImpl(connection, Spec(connection, sql, transaction, commandTimeout, cancellationToken));

        /// <summary>Asynchronously executes interpolated SQL and returns the first column of the first row.</summary>
        public static Task<T?> ExecuteScalarAsync<T>(this IDbConnection connection, Sql sql, IDbTransaction? transaction = null, int? commandTimeout = null,
            CancellationToken cancellationToken = default)
            => ExecuteScalarAsyncImpl<T>(connection, Spec(connection, sql, transaction, commandTimeout, cancellationToken));

        /// <summary>Asynchronously runs interpolated SQL and maps each row to <typeparamref name="T"/>.</summary>
        public static Task<IEnumerable<T>> QueryAsync<T>(this IDbConnection connection, Sql sql, IDbTransaction? transaction = null, int? commandTimeout = null,
            CancellationToken cancellationToken = default)
            => QueryAsyncImpl<T>(connection, Spec(connection, sql, transaction, commandTimeout, cancellationToken));

        /// <summary>Asynchronously runs interpolated SQL and returns each row as a <c>dynamic</c> object.</summary>
        public static async Task<IEnumerable<dynamic>> QueryAsync(this IDbConnection connection, Sql sql, IDbTransaction? transaction = null, int? commandTimeout = null,
            CancellationToken cancellationToken = default)
            => await QueryAsyncImpl<object>(connection, Spec(connection, sql, transaction, commandTimeout, cancellationToken)).ConfigureAwait(false);

        /// <summary>Asynchronously runs interpolated SQL and returns the first row. Throws if there are no rows.</summary>
        public static Task<T> QueryFirstAsync<T>(this IDbConnection connection, Sql sql, IDbTransaction? transaction = null, int? commandTimeout = null,
            CancellationToken cancellationToken = default)
            => QueryRowAsync<T>(connection, Spec(connection, sql, transaction, commandTimeout, cancellationToken), RowMode.First);

        /// <summary>Asynchronously runs interpolated SQL and returns the first row, or <c>default</c>.</summary>
        public static Task<T?> QueryFirstOrDefaultAsync<T>(this IDbConnection connection, Sql sql, IDbTransaction? transaction = null, int? commandTimeout = null,
            CancellationToken cancellationToken = default)
            => QueryRowAsync<T?>(connection, Spec(connection, sql, transaction, commandTimeout, cancellationToken), RowMode.FirstOrDefault);

        /// <summary>Asynchronously runs interpolated SQL and returns the only row. Throws unless there is exactly one.</summary>
        public static Task<T> QuerySingleAsync<T>(this IDbConnection connection, Sql sql, IDbTransaction? transaction = null, int? commandTimeout = null,
            CancellationToken cancellationToken = default)
            => QueryRowAsync<T>(connection, Spec(connection, sql, transaction, commandTimeout, cancellationToken), RowMode.Single);

        /// <summary>Asynchronously runs interpolated SQL and returns the only row, or <c>default</c>.</summary>
        public static Task<T?> QuerySingleOrDefaultAsync<T>(this IDbConnection connection, Sql sql, IDbTransaction? transaction = null, int? commandTimeout = null,
            CancellationToken cancellationToken = default)
            => QueryRowAsync<T?>(connection, Spec(connection, sql, transaction, commandTimeout, cancellationToken), RowMode.SingleOrDefault);

        /// <summary>Asynchronously runs interpolated SQL that returns several result sets.</summary>
        public static Task<MultiResultReader> QueryMultipleAsync(this IDbConnection connection, Sql sql, IDbTransaction? transaction = null, int? commandTimeout = null,
            CancellationToken cancellationToken = default)
            => QueryMultipleAsyncImpl(connection, Spec(connection, sql, transaction, commandTimeout, cancellationToken));

#if NET8_0_OR_GREATER
        /// <summary>Streams the rows of interpolated SQL as they are read.</summary>
        public static IAsyncEnumerable<T> QueryUnbufferedAsync<T>(this IDbConnection connection, Sql sql, IDbTransaction? transaction = null, int? commandTimeout = null,
            CancellationToken cancellationToken = default)
            => QueryUnbufferedAsyncImpl<T>(connection, Spec(connection, sql, transaction, commandTimeout), cancellationToken);
#endif

        private static CommandSpec Spec(IDbConnection connection, Sql sql, IDbTransaction? transaction, int? commandTimeout,
            CancellationToken cancellationToken = default)
        {
            if (sql is null) throw new System.ArgumentNullException(nameof(sql));
            var prefix = SqlDialect.Find(connection)?.ParameterPrefix ?? "@";
            var text = sql.Render(prefix, out var parameters);
            return new CommandSpec(text, parameters, transaction, commandTimeout, CommandType.Text, cancellationToken);
        }
    }
}
