using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using SqlWright.Internal;

namespace SqlWright
{
    /// <remarks>
    /// CRUD helpers. Table and column names come from <c>[Table]</c> / <c>[Column]</c> attributes, or from type and
    /// member names via <see cref="SqlWrightSettings.NamingStyle"/>. The key is the member marked <c>[Key]</c>, or
    /// one named <c>Id</c> / <c>{TypeName}Id</c>. SQL is generated per <see cref="SqlDialect"/> and cached.
    /// </remarks>
    public static partial class SqlWrightExtensions
    {
        /// <summary>
        /// Loads one entity by key, or returns <c>default</c> if it doesn't exist.
        /// </summary>
        /// <param name="connection">The connection.</param>
        /// <param name="id">The key value, or for composite keys an object with a member per key (e.g. <c>new { OrderId, LineNo }</c>).</param>
        /// <param name="transaction">The transaction to enlist in, if any.</param>
        /// <param name="commandTimeout">Timeout in seconds.</param>
        public static T? Get<T>(this IDbConnection connection, object id, IDbTransaction? transaction = null, int? commandTimeout = null)
        {
            var (info, sql) = SelectById<T>(connection);
            return QueryRow<T?>(connection, new CommandSpec(sql, info.KeyParameters(id, "Get"), transaction, commandTimeout, CommandType.Text), RowMode.SingleOrDefault);
        }

        /// <summary>Loads every row of the entity's table.</summary>
        public static IEnumerable<T> GetAll<T>(this IDbConnection connection, IDbTransaction? transaction = null, int? commandTimeout = null)
        {
            var sql = EntityInfo.Get(typeof(T)).For(SqlDialect.For(connection)).SelectAll;
            return QueryImpl<T>(connection, new CommandSpec(sql, null, transaction, commandTimeout, CommandType.Text), buffered: true);
        }

        /// <summary>
        /// Inserts an entity and returns the number of rows inserted. A database-generated key is written back to the entity.
        /// </summary>
        public static int Insert<T>(this IDbConnection connection, T entity, IDbTransaction? transaction = null, int? commandTimeout = null)
        {
            var (info, statements, target) = Prepare(connection, entity);
            var spec = new CommandSpec(statements.Insert, info.EntityParameters(target, info.InsertColumns), transaction, commandTimeout, CommandType.Text);

            if (info.GeneratedKey == null) return ExecuteImpl(connection, spec);

            var key = ExecuteScalarImpl<object>(connection, spec);
            info.GeneratedKey.SetValue(target, key);
            return 1;
        }

        /// <summary>Inserts each entity in turn, writing back generated keys. Returns the number of rows inserted.</summary>
        public static int InsertAll<T>(this IDbConnection connection, IEnumerable<T> entities, IDbTransaction? transaction = null, int? commandTimeout = null)
        {
            if (entities is null) throw new ArgumentNullException(nameof(entities));
            var wasClosed = OpenIfClosed(connection);
            try
            {
                var total = 0;
                foreach (var entity in entities) total += connection.Insert(entity, transaction, commandTimeout);
                return total;
            }
            finally
            {
                if (wasClosed) connection.Close();
            }
        }

        /// <summary>Updates every non-key column of an entity, matched by key. Returns <c>true</c> if a row was updated.</summary>
        public static bool Update<T>(this IDbConnection connection, T entity, IDbTransaction? transaction = null, int? commandTimeout = null)
        {
            var (info, statements, target) = Prepare(connection, entity);
            var sql = UpdateSql(info, statements);
            return ExecuteImpl(connection, new CommandSpec(sql, info.EntityParameters(target, info.Columns), transaction, commandTimeout, CommandType.Text)) > 0;
        }

        /// <summary>Deletes an entity by its key. Returns <c>true</c> if a row was deleted.</summary>
        public static bool Delete<T>(this IDbConnection connection, T entity, IDbTransaction? transaction = null, int? commandTimeout = null)
        {
            var (info, statements, target) = Prepare(connection, entity);
            info.RequireKey("Delete");
            return ExecuteImpl(connection, new CommandSpec(statements.Delete!, info.EntityParameters(target, info.Keys), transaction, commandTimeout, CommandType.Text)) > 0;
        }

        /// <summary>Deletes the row with the given key. Returns <c>true</c> if a row was deleted.</summary>
        /// <param name="connection">The connection.</param>
        /// <param name="id">The key value, or for composite keys an object with a member per key.</param>
        /// <param name="transaction">The transaction to enlist in, if any.</param>
        /// <param name="commandTimeout">Timeout in seconds.</param>
        public static bool DeleteById<T>(this IDbConnection connection, object id, IDbTransaction? transaction = null, int? commandTimeout = null)
        {
            var info = EntityInfo.Get(typeof(T));
            var parameters = info.KeyParameters(id, "DeleteById");
            var sql = info.For(SqlDialect.For(connection)).Delete!;
            return ExecuteImpl(connection, new CommandSpec(sql, parameters, transaction, commandTimeout, CommandType.Text)) > 0;
        }

        // ---- Async ----

        /// <summary>Asynchronously loads one entity by key, or returns <c>default</c> if it doesn't exist.</summary>
        public static Task<T?> GetAsync<T>(this IDbConnection connection, object id, IDbTransaction? transaction = null, int? commandTimeout = null,
            CancellationToken cancellationToken = default)
        {
            var (info, sql) = SelectById<T>(connection);
            return QueryRowAsync<T?>(connection,
                new CommandSpec(sql, info.KeyParameters(id, "Get"), transaction, commandTimeout, CommandType.Text, cancellationToken), RowMode.SingleOrDefault);
        }

        /// <summary>Asynchronously loads every row of the entity's table.</summary>
        public static Task<IEnumerable<T>> GetAllAsync<T>(this IDbConnection connection, IDbTransaction? transaction = null, int? commandTimeout = null,
            CancellationToken cancellationToken = default)
        {
            var sql = EntityInfo.Get(typeof(T)).For(SqlDialect.For(connection)).SelectAll;
            return QueryAsyncImpl<T>(connection, new CommandSpec(sql, null, transaction, commandTimeout, CommandType.Text, cancellationToken));
        }

        /// <summary>Asynchronously inserts an entity. A database-generated key is written back to the entity.</summary>
        public static async Task<int> InsertAsync<T>(this IDbConnection connection, T entity, IDbTransaction? transaction = null, int? commandTimeout = null,
            CancellationToken cancellationToken = default)
        {
            var (info, statements, target) = Prepare(connection, entity);
            var spec = new CommandSpec(statements.Insert, info.EntityParameters(target, info.InsertColumns), transaction, commandTimeout, CommandType.Text, cancellationToken);

            if (info.GeneratedKey == null) return await ExecuteAsyncImpl(connection, spec).ConfigureAwait(false);

            var key = await ExecuteScalarAsyncImpl<object>(connection, spec).ConfigureAwait(false);
            info.GeneratedKey.SetValue(target, key);
            return 1;
        }

        /// <summary>Asynchronously inserts each entity in turn, writing back generated keys.</summary>
        public static async Task<int> InsertAllAsync<T>(this IDbConnection connection, IEnumerable<T> entities, IDbTransaction? transaction = null,
            int? commandTimeout = null, CancellationToken cancellationToken = default)
        {
            if (entities is null) throw new ArgumentNullException(nameof(entities));
            var db = AsDbConnection(connection);
            var wasClosed = await OpenIfClosedAsync(db, cancellationToken).ConfigureAwait(false);
            try
            {
                var total = 0;
                foreach (var entity in entities)
                    total += await connection.InsertAsync(entity, transaction, commandTimeout, cancellationToken).ConfigureAwait(false);
                return total;
            }
            finally
            {
                if (wasClosed) db.Close();
            }
        }

        /// <summary>Asynchronously updates every non-key column of an entity. Returns <c>true</c> if a row was updated.</summary>
        public static async Task<bool> UpdateAsync<T>(this IDbConnection connection, T entity, IDbTransaction? transaction = null, int? commandTimeout = null,
            CancellationToken cancellationToken = default)
        {
            var (info, statements, target) = Prepare(connection, entity);
            var sql = UpdateSql(info, statements);
            var spec = new CommandSpec(sql, info.EntityParameters(target, info.Columns), transaction, commandTimeout, CommandType.Text, cancellationToken);
            return await ExecuteAsyncImpl(connection, spec).ConfigureAwait(false) > 0;
        }

        /// <summary>Asynchronously deletes an entity by its key. Returns <c>true</c> if a row was deleted.</summary>
        public static async Task<bool> DeleteAsync<T>(this IDbConnection connection, T entity, IDbTransaction? transaction = null, int? commandTimeout = null,
            CancellationToken cancellationToken = default)
        {
            var (info, statements, target) = Prepare(connection, entity);
            info.RequireKey("Delete");
            var spec = new CommandSpec(statements.Delete!, info.EntityParameters(target, info.Keys), transaction, commandTimeout, CommandType.Text, cancellationToken);
            return await ExecuteAsyncImpl(connection, spec).ConfigureAwait(false) > 0;
        }

        /// <summary>Asynchronously deletes the row with the given key. Returns <c>true</c> if a row was deleted.</summary>
        public static async Task<bool> DeleteByIdAsync<T>(this IDbConnection connection, object id, IDbTransaction? transaction = null, int? commandTimeout = null,
            CancellationToken cancellationToken = default)
        {
            var info = EntityInfo.Get(typeof(T));
            var parameters = info.KeyParameters(id, "DeleteById");
            var sql = info.For(SqlDialect.For(connection)).Delete!;
            return await ExecuteAsyncImpl(connection, new CommandSpec(sql, parameters, transaction, commandTimeout, CommandType.Text, cancellationToken)).ConfigureAwait(false) > 0;
        }

        // ---- Helpers ----

        private static (EntityInfo Info, string Sql) SelectById<T>(IDbConnection connection)
        {
            var info = EntityInfo.Get(typeof(T));
            info.RequireKey("Get");
            return (info, info.For(SqlDialect.For(connection)).SelectById!);
        }

        private static (EntityInfo Info, EntityStatements Statements, object Target) Prepare<T>(IDbConnection connection, T entity)
        {
            if (entity is null) throw new ArgumentNullException(nameof(entity));

            // Use the declared type unless it says nothing useful (object, an interface, an abstract base).
            var type = typeof(T);
            if (type == typeof(object) || type.IsInterface || type.IsAbstract) type = entity.GetType();

            var info = EntityInfo.Get(type);
            return (info, info.For(SqlDialect.For(connection)), entity);
        }

        private static string UpdateSql(EntityInfo info, EntityStatements statements)
        {
            info.RequireKey("Update");
            return statements.Update ?? throw new SqlWrightException(
                $"Update<{Diagnostics.TypeName(info.Type)}> has nothing to update: every column is a key or [Computed].");
        }
    }
}
