using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using SqlWright.Internal;
#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
#endif

namespace SqlWright
{
    public static partial class SqlWrightExtensions
    {
        /// <summary>
        /// Asynchronously executes a command and returns the number of rows affected.
        /// If <paramref name="param"/> is a sequence of objects, the command runs once per item and the counts are summed.
        /// </summary>
        public static Task<int> ExecuteAsync(this IDbConnection connection, string sql, object? param = null,
            IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null,
            CancellationToken cancellationToken = default)
            => ExecuteAsyncImpl(connection, new CommandSpec(sql, param, transaction, commandTimeout, commandType, cancellationToken));

        /// <summary>Asynchronously returns the first column of the first row.</summary>
        public static Task<object?> ExecuteScalarAsync(this IDbConnection connection, string sql, object? param = null,
            IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null,
            CancellationToken cancellationToken = default)
            => ExecuteScalarAsyncImpl<object>(connection, new CommandSpec(sql, param, transaction, commandTimeout, commandType, cancellationToken));

        /// <summary>Asynchronously returns the first column of the first row, converted to <typeparamref name="T"/>.</summary>
        public static Task<T?> ExecuteScalarAsync<T>(this IDbConnection connection, string sql, object? param = null,
            IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null,
            CancellationToken cancellationToken = default)
            => ExecuteScalarAsyncImpl<T>(connection, new CommandSpec(sql, param, transaction, commandTimeout, commandType, cancellationToken));

        /// <summary>Asynchronously runs a query and maps every row to <typeparamref name="T"/> (buffered).</summary>
        public static Task<IEnumerable<T>> QueryAsync<T>(this IDbConnection connection, string sql, object? param = null,
            IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null,
            CancellationToken cancellationToken = default)
            => QueryAsyncImpl<T>(connection, new CommandSpec(sql, param, transaction, commandTimeout, commandType, cancellationToken));

        /// <summary>Asynchronously runs a query and returns each row as a <c>dynamic</c> object (buffered).</summary>
        public static async Task<IEnumerable<dynamic>> QueryAsync(this IDbConnection connection, string sql, object? param = null,
            IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null,
            CancellationToken cancellationToken = default)
            => await connection.QueryAsync<object>(sql, param, transaction, commandTimeout, commandType, cancellationToken).ConfigureAwait(false);

        /// <summary>Asynchronously returns the first row. Throws if there are no rows.</summary>
        public static Task<T> QueryFirstAsync<T>(this IDbConnection connection, string sql, object? param = null,
            IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null,
            CancellationToken cancellationToken = default)
            => QueryRowAsync<T>(connection, new CommandSpec(sql, param, transaction, commandTimeout, commandType, cancellationToken), RowMode.First);

        /// <summary>Asynchronously returns the first row, or <c>default</c> if there are no rows.</summary>
        public static Task<T?> QueryFirstOrDefaultAsync<T>(this IDbConnection connection, string sql, object? param = null,
            IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null,
            CancellationToken cancellationToken = default)
            => QueryRowAsync<T?>(connection, new CommandSpec(sql, param, transaction, commandTimeout, commandType, cancellationToken), RowMode.FirstOrDefault);

        /// <summary>Asynchronously returns the only row. Throws if there are zero rows or more than one.</summary>
        public static Task<T> QuerySingleAsync<T>(this IDbConnection connection, string sql, object? param = null,
            IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null,
            CancellationToken cancellationToken = default)
            => QueryRowAsync<T>(connection, new CommandSpec(sql, param, transaction, commandTimeout, commandType, cancellationToken), RowMode.Single);

        /// <summary>Asynchronously returns the only row, or <c>default</c> if there are no rows. Throws if there is more than one.</summary>
        public static Task<T?> QuerySingleOrDefaultAsync<T>(this IDbConnection connection, string sql, object? param = null,
            IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null,
            CancellationToken cancellationToken = default)
            => QueryRowAsync<T?>(connection, new CommandSpec(sql, param, transaction, commandTimeout, commandType, cancellationToken), RowMode.SingleOrDefault);

        /// <summary>
        /// Asynchronously executes a command that returns several result sets. Dispose the returned reader when done.
        /// </summary>
        public static Task<MultiResultReader> QueryMultipleAsync(this IDbConnection connection, string sql, object? param = null,
            IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null,
            CancellationToken cancellationToken = default)
            => QueryMultipleAsyncImpl(connection, new CommandSpec(sql, param, transaction, commandTimeout, commandType, cancellationToken));

#if NET8_0_OR_GREATER
        /// <summary>
        /// Streams rows as they are read. The reader (and the connection, if SqlWright opened it)
        /// stays open until enumeration completes or is abandoned.
        /// </summary>
        public static IAsyncEnumerable<T> QueryUnbufferedAsync<T>(this IDbConnection connection, string sql, object? param = null,
            IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null,
            CancellationToken cancellationToken = default)
            => QueryUnbufferedAsyncImpl<T>(connection, new CommandSpec(sql, param, transaction, commandTimeout, commandType), cancellationToken);

        internal static async IAsyncEnumerable<T> QueryUnbufferedAsyncImpl<T>(IDbConnection connection, CommandSpec spec,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var db = AsDbConnection(connection);
            var wasClosed = await OpenIfClosedAsync(db, cancellationToken).ConfigureAwait(false);
            try
            {
                await using var cmd = spec.CreateAsync(db);
                await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SingleResult, cancellationToken).ConfigureAwait(false);
                if (reader.FieldCount == 0) yield break;

                var map = RowMapper.Get<T>(reader);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    yield return map(reader);
            }
            finally
            {
                if (wasClosed) await db.CloseAsync().ConfigureAwait(false);
            }
        }
#endif

        internal static async Task<int> ExecuteAsyncImpl(IDbConnection connection, CommandSpec spec)
        {
            var db = AsDbConnection(connection);
            var ct = spec.CancellationToken;
            var wasClosed = await OpenIfClosedAsync(db, ct).ConfigureAwait(false);
            try
            {
                if (!ParameterReader.IsMultiExec(spec.Param))
                {
                    using var cmd = spec.CreateAsync(db);
                    return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                var total = 0;
                foreach (var item in (IEnumerable)spec.Param!)
                {
                    using var cmd = (DbCommand)spec.Create(db, item);
                    total += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                return total;
            }
            finally
            {
                if (wasClosed) db.Close();
            }
        }

        internal static async Task<T?> ExecuteScalarAsyncImpl<T>(IDbConnection connection, CommandSpec spec)
        {
            var db = AsDbConnection(connection);
            var ct = spec.CancellationToken;
            var wasClosed = await OpenIfClosedAsync(db, ct).ConfigureAwait(false);
            try
            {
                using var cmd = spec.CreateAsync(db);
                return ValueConverter.To<T>(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            }
            finally
            {
                if (wasClosed) db.Close();
            }
        }

        internal static async Task<IEnumerable<T>> QueryAsyncImpl<T>(IDbConnection connection, CommandSpec spec)
        {
            var db = AsDbConnection(connection);
            var ct = spec.CancellationToken;
            var wasClosed = await OpenIfClosedAsync(db, ct).ConfigureAwait(false);
            try
            {
                using var cmd = spec.CreateAsync(db);
                using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SingleResult, ct).ConfigureAwait(false);
                return await RowReader.ReadAllAsync<T>(reader, ct).ConfigureAwait(false);
            }
            finally
            {
                if (wasClosed) db.Close();
            }
        }

        internal static async Task<MultiResultReader> QueryMultipleAsyncImpl(IDbConnection connection, CommandSpec spec)
        {
            var db = AsDbConnection(connection);
            var ct = spec.CancellationToken;
            var wasClosed = await OpenIfClosedAsync(db, ct).ConfigureAwait(false);
            DbCommand? cmd = null;
            try
            {
                cmd = spec.CreateAsync(db);
                var reader = await cmd.ExecuteReaderAsync(
                    wasClosed ? CommandBehavior.CloseConnection : CommandBehavior.Default, ct).ConfigureAwait(false);
                return new MultiResultReader(cmd, reader);
            }
            catch
            {
                cmd?.Dispose();
                if (wasClosed) db.Close();
                throw;
            }
        }

        internal static async Task<T> QueryRowAsync<T>(IDbConnection connection, CommandSpec spec, RowMode mode)
        {
            var db = AsDbConnection(connection);
            var ct = spec.CancellationToken;
            var wasClosed = await OpenIfClosedAsync(db, ct).ConfigureAwait(false);
            try
            {
                using var cmd = spec.CreateAsync(db);
                using var reader = await cmd.ExecuteReaderAsync(RowReader.Behavior(mode), ct).ConfigureAwait(false);
                return await RowReader.ReadRowAsync<T>(reader, mode, ct).ConfigureAwait(false);
            }
            finally
            {
                if (wasClosed) db.Close();
            }
        }

        internal static async Task<bool> OpenIfClosedAsync(DbConnection connection, CancellationToken cancellationToken)
        {
            if (connection.State != ConnectionState.Closed) return false;
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        internal static DbConnection AsDbConnection(IDbConnection connection) =>
            connection as DbConnection
            ?? throw new SqlWrightException(
                $"Async methods require a connection deriving from {nameof(DbConnection)}; got {connection.GetType().FullName}. " +
                "Use the synchronous methods with this connection type.");
    }
}
