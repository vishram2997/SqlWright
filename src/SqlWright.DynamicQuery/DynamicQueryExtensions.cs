using System.Data;
using System.Data.Common;
using SqlWright.DynamicQuery.Internal;

namespace SqlWright.DynamicQuery;

/// <summary>Runs <see cref="QueryRequest"/>s against a connection.</summary>
public static class DynamicQueryExtensions
{
    /// <summary>
    /// Validates <paramref name="request"/> against <paramref name="schema"/> and builds SQL for <paramref name="dialect"/>
    /// without running it. Throws <see cref="DynamicQueryException"/> if the request is invalid.
    /// </summary>
    public static TranslatedQuery Translate(this QuerySchema schema, QueryRequest request, SqlDialect dialect, DynamicQueryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(dialect);
        return QueryTranslator.Translate(schema, request, dialect, options);
    }

    /// <summary>
    /// Runs a client's query request. Throws <see cref="DynamicQueryException"/> (a client error) if the request
    /// names anything outside <paramref name="schema"/>, has bad values, or exceeds the schema's limits.
    /// </summary>
    /// <param name="connection">The connection; its dialect is detected as for SqlWright's CRUD helpers.</param>
    /// <param name="request">The client's request.</param>
    /// <param name="schema">What clients are allowed to query.</param>
    /// <param name="options">Per-request row filters, e.g. for tenant isolation.</param>
    /// <param name="transaction">The transaction to enlist in, if any.</param>
    public static QueryResponse QueryDynamic(this IDbConnection connection, QueryRequest request, QuerySchema schema,
        DynamicQueryOptions? options = null, IDbTransaction? transaction = null)
    {
        var query = schema.Translate(request, SqlDialect.For(connection), options);
        var timeout = schema.Limits.CommandTimeout;

        var wasClosed = connection.State == ConnectionState.Closed;
        if (wasClosed) connection.Open();
        try
        {
            var rows = connection.Query(query.Data, transaction, commandTimeout: timeout);
            long? total = query.Count is null ? null : connection.ExecuteScalar<long>(query.Count, transaction, timeout);
            return query.ToResponse(rows, total);
        }
        finally
        {
            if (wasClosed) connection.Close();
        }
    }

    /// <summary>Asynchronously runs a client's query request. See <see cref="QueryDynamic"/>.</summary>
    public static async Task<QueryResponse> QueryDynamicAsync(this IDbConnection connection, QueryRequest request, QuerySchema schema,
        DynamicQueryOptions? options = null, IDbTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        var query = schema.Translate(request, SqlDialect.For(connection), options);
        var timeout = schema.Limits.CommandTimeout;

        var wasClosed = connection.State == ConnectionState.Closed;
        if (wasClosed)
        {
            if (connection is DbConnection db) await db.OpenAsync(cancellationToken).ConfigureAwait(false);
            else connection.Open();
        }
        try
        {
            var rows = await connection.QueryAsync(query.Data, transaction, timeout, cancellationToken).ConfigureAwait(false);
            long? total = query.Count is null
                ? null
                : await connection.ExecuteScalarAsync<long>(query.Count, transaction, timeout, cancellationToken).ConfigureAwait(false);
            return query.ToResponse(rows, total);
        }
        finally
        {
            if (wasClosed) connection.Close();
        }
    }
}
