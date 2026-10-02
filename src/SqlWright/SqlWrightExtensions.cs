using System.Collections;
using System.Collections.Generic;
using System.Data;
using SqlWright.Internal;

namespace SqlWright
{
    /// <summary>
    /// SqlWright's entry point: extension methods on <see cref="IDbConnection"/>.
    /// </summary>
    /// <remarks>
    /// Parameters can be an anonymous object, a POCO, a dictionary or a <see cref="SqlParameters"/>.
    /// If the connection is closed, it is opened for the duration of the call and closed again afterwards.
    /// </remarks>
    public static partial class SqlWrightExtensions
    {
        /// <summary>
        /// Executes a command and returns the number of rows affected.
        /// If <paramref name="param"/> is a sequence of objects, the command runs once per item and the counts are summed.
        /// </summary>
        public static int Execute(this IDbConnection connection, string sql, object? param = null,
            IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
            => ExecuteImpl(connection, new CommandSpec(sql, param, transaction, commandTimeout, commandType));

        /// <summary>Executes a command and returns the first column of the first row.</summary>
        public static object? ExecuteScalar(this IDbConnection connection, string sql, object? param = null,
            IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
            => ExecuteScalarImpl<object>(connection, new CommandSpec(sql, param, transaction, commandTimeout, commandType));

        /// <summary>Executes a command and returns the first column of the first row, converted to <typeparamref name="T"/>.</summary>
        public static T? ExecuteScalar<T>(this IDbConnection connection, string sql, object? param = null,
            IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
            => ExecuteScalarImpl<T>(connection, new CommandSpec(sql, param, transaction, commandTimeout, commandType));

        /// <summary>
        /// Runs a query and maps each row to <typeparamref name="T"/>.
        /// </summary>
        /// <param name="connection">The connection to run on. Opened and closed automatically if it is closed.</param>
        /// <param name="sql">The SQL text or stored procedure name.</param>
        /// <param name="param">Parameters: an anonymous object, POCO, dictionary or <see cref="SqlParameters"/>.</param>
        /// <param name="transaction">The transaction to enlist in, if any.</param>
        /// <param name="commandTimeout">Timeout in seconds; falls back to <see cref="SqlWrightSettings.CommandTimeout"/>.</param>
        /// <param name="commandType">Text (default) or StoredProcedure.</param>
        /// <param name="buffered">
        /// When <c>true</c> (the default) all rows are read before returning. When <c>false</c>, rows are streamed
        /// as you enumerate, and the reader (and connection, if SqlWright opened it) stays open until enumeration ends.
        /// </param>
        public static IEnumerable<T> Query<T>(this IDbConnection connection, string sql, object? param = null,
            IDbTransaction? transaction = null, bool buffered = true, int? commandTimeout = null, CommandType? commandType = null)
            => QueryImpl<T>(connection, new CommandSpec(sql, param, transaction, commandTimeout, commandType), buffered);

        /// <summary>
        /// Runs a query and returns each row as a <c>dynamic</c> object (an <see cref="System.Dynamic.ExpandoObject"/>,
        /// which can also be used as <c>IDictionary&lt;string, object?&gt;</c>).
        /// </summary>
        public static IEnumerable<dynamic> Query(this IDbConnection connection, string sql, object? param = null,
            IDbTransaction? transaction = null, bool buffered = true, int? commandTimeout = null, CommandType? commandType = null)
            => connection.Query<object>(sql, param, transaction, buffered, commandTimeout, commandType);

        /// <summary>Returns the first row. Throws if there are no rows.</summary>
        public static T QueryFirst<T>(this IDbConnection connection, string sql, object? param = null,
            IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
            => QueryRow<T>(connection, new CommandSpec(sql, param, transaction, commandTimeout, commandType), RowMode.First);

        /// <summary>Returns the first row, or <c>default</c> if there are no rows.</summary>
        public static T? QueryFirstOrDefault<T>(this IDbConnection connection, string sql, object? param = null,
            IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
            => QueryRow<T>(connection, new CommandSpec(sql, param, transaction, commandTimeout, commandType), RowMode.FirstOrDefault);

        /// <summary>Returns the only row. Throws if there are zero rows or more than one.</summary>
        public static T QuerySingle<T>(this IDbConnection connection, string sql, object? param = null,
            IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
            => QueryRow<T>(connection, new CommandSpec(sql, param, transaction, commandTimeout, commandType), RowMode.Single);

        /// <summary>Returns the only row, or <c>default</c> if there are no rows. Throws if there is more than one.</summary>
        public static T? QuerySingleOrDefault<T>(this IDbConnection connection, string sql, object? param = null,
            IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
            => QueryRow<T>(connection, new CommandSpec(sql, param, transaction, commandTimeout, commandType), RowMode.SingleOrDefault);

        /// <summary>
        /// Executes a command that returns several result sets, read in order with <see cref="MultiResultReader"/>.
        /// Dispose the returned reader when done.
        /// </summary>
        public static MultiResultReader QueryMultiple(this IDbConnection connection, string sql, object? param = null,
            IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
            => QueryMultipleImpl(connection, new CommandSpec(sql, param, transaction, commandTimeout, commandType));

        internal static int ExecuteImpl(IDbConnection connection, CommandSpec spec)
        {
            var wasClosed = OpenIfClosed(connection);
            try
            {
                if (!ParameterReader.IsMultiExec(spec.Param))
                {
                    using var cmd = spec.Create(connection);
                    return cmd.ExecuteNonQuery();
                }

                var total = 0;
                foreach (var item in (IEnumerable)spec.Param!)
                {
                    using var cmd = spec.Create(connection, item);
                    total += cmd.ExecuteNonQuery();
                }
                return total;
            }
            finally
            {
                if (wasClosed) connection.Close();
            }
        }

        internal static T? ExecuteScalarImpl<T>(IDbConnection connection, CommandSpec spec)
        {
            var wasClosed = OpenIfClosed(connection);
            try
            {
                using var cmd = spec.Create(connection);
                return ValueConverter.To<T>(cmd.ExecuteScalar());
            }
            finally
            {
                if (wasClosed) connection.Close();
            }
        }

        internal static IEnumerable<T> QueryImpl<T>(IDbConnection connection, CommandSpec spec, bool buffered)
        {
            var rows = QueryIterator<T>(connection, spec);
            return buffered ? new List<T>(rows) : rows;
        }

        internal static MultiResultReader QueryMultipleImpl(IDbConnection connection, CommandSpec spec)
        {
            var wasClosed = OpenIfClosed(connection);
            IDbCommand? cmd = null;
            try
            {
                cmd = spec.Create(connection);
                var reader = cmd.ExecuteReader(wasClosed ? CommandBehavior.CloseConnection : CommandBehavior.Default);
                return new MultiResultReader(cmd, reader);
            }
            catch
            {
                cmd?.Dispose();
                if (wasClosed) connection.Close();
                throw;
            }
        }

        private static IEnumerable<T> QueryIterator<T>(IDbConnection connection, CommandSpec spec)
        {
            var wasClosed = connection.State == ConnectionState.Closed;
            using var cmd = spec.Create(connection);
            IDataReader? reader = null;
            try
            {
                if (wasClosed) connection.Open();
                reader = cmd.ExecuteReader(wasClosed
                    ? CommandBehavior.SingleResult | CommandBehavior.CloseConnection
                    : CommandBehavior.SingleResult);
                wasClosed = false; // the reader now owns closing the connection

                if (reader.FieldCount == 0) yield break;
                var map = RowMapper.Get<T>(reader);
                while (reader.Read()) yield return map(reader);
            }
            finally
            {
                reader?.Dispose();
                if (wasClosed) connection.Close();
            }
        }

        internal static T QueryRow<T>(IDbConnection connection, CommandSpec spec, RowMode mode)
        {
            var wasClosed = OpenIfClosed(connection);
            try
            {
                using var cmd = spec.Create(connection);
                using var reader = cmd.ExecuteReader(RowReader.Behavior(mode));
                return RowReader.ReadRow<T>(reader, mode);
            }
            finally
            {
                if (wasClosed) connection.Close();
            }
        }

        internal static bool OpenIfClosed(IDbConnection connection)
        {
            if (connection.State != ConnectionState.Closed) return false;
            connection.Open();
            return true;
        }
    }
}
