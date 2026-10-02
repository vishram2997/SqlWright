using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using SqlWright.Internal;

namespace SqlWright
{
    /// <summary>
    /// Reads the result sets of a multi-statement command, one call per result set, in order.
    /// Returned by <c>QueryMultiple</c> and <c>QueryMultipleAsync</c>.
    /// </summary>
    /// <example>
    /// <code>
    /// using var multi = connection.QueryMultiple("SELECT * FROM Orders WHERE Id = @id; SELECT * FROM OrderLines WHERE OrderId = @id", new { id });
    /// var order = multi.ReadSingle&lt;Order&gt;();
    /// var lines = multi.Read&lt;OrderLine&gt;();
    /// </code>
    /// </example>
    public sealed class MultiResultReader : IDisposable
    {
        private readonly IDbCommand _command;
        private readonly IDataReader _reader;
        private bool _disposed;

        internal MultiResultReader(IDbCommand command, IDataReader reader)
        {
            _command = command;
            _reader = reader;
        }

        /// <summary>True once every result set has been read.</summary>
        public bool IsConsumed { get; private set; }

        /// <summary>Reads the current result set as a list of <typeparamref name="T"/> and advances to the next one.</summary>
        public IEnumerable<T> Read<T>()
        {
            EnsureReadable();
            var rows = RowReader.ReadAll<T>(_reader);
            Advance();
            return rows;
        }

        /// <summary>Reads the current result set as <c>dynamic</c> rows and advances to the next one.</summary>
        public IEnumerable<dynamic> Read() => Read<object>();

        /// <summary>Returns the first row of the current result set and advances. Throws if it is empty.</summary>
        public T ReadFirst<T>() => ReadRow<T>(RowMode.First);

        /// <summary>Returns the first row of the current result set, or <c>default</c>, and advances.</summary>
        public T? ReadFirstOrDefault<T>() => ReadRow<T?>(RowMode.FirstOrDefault);

        /// <summary>Returns the only row of the current result set and advances. Throws unless there is exactly one row.</summary>
        public T ReadSingle<T>() => ReadRow<T>(RowMode.Single);

        /// <summary>Returns the only row of the current result set, or <c>default</c>, and advances. Throws if there is more than one.</summary>
        public T? ReadSingleOrDefault<T>() => ReadRow<T?>(RowMode.SingleOrDefault);

        /// <summary>Asynchronously reads the current result set and advances to the next one.</summary>
        public async Task<IEnumerable<T>> ReadAsync<T>(CancellationToken cancellationToken = default)
        {
            EnsureReadable();
            var reader = AsyncReader();
            var rows = await RowReader.ReadAllAsync<T>(reader, cancellationToken).ConfigureAwait(false);
            await AdvanceAsync(reader, cancellationToken).ConfigureAwait(false);
            return rows;
        }

        /// <summary>Asynchronously returns the first row of the current result set and advances. Throws if it is empty.</summary>
        public Task<T> ReadFirstAsync<T>(CancellationToken cancellationToken = default) =>
            ReadRowAsync<T>(RowMode.First, cancellationToken);

        /// <summary>Asynchronously returns the first row of the current result set, or <c>default</c>, and advances.</summary>
        public Task<T?> ReadFirstOrDefaultAsync<T>(CancellationToken cancellationToken = default) =>
            ReadRowAsync<T?>(RowMode.FirstOrDefault, cancellationToken);

        /// <summary>Asynchronously returns the only row of the current result set and advances.</summary>
        public Task<T> ReadSingleAsync<T>(CancellationToken cancellationToken = default) =>
            ReadRowAsync<T>(RowMode.Single, cancellationToken);

        /// <summary>Asynchronously returns the only row of the current result set, or <c>default</c>, and advances.</summary>
        public Task<T?> ReadSingleOrDefaultAsync<T>(CancellationToken cancellationToken = default) =>
            ReadRowAsync<T?>(RowMode.SingleOrDefault, cancellationToken);

        /// <summary>Closes the reader and command, and the connection if SqlWright opened it.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _reader.Dispose();
            _command.Dispose();
        }

        private T ReadRow<T>(RowMode mode)
        {
            EnsureReadable();
            var row = RowReader.ReadRow<T>(_reader, mode);
            Advance();
            return row;
        }

        private async Task<T> ReadRowAsync<T>(RowMode mode, CancellationToken cancellationToken)
        {
            EnsureReadable();
            var reader = AsyncReader();
            var row = await RowReader.ReadRowAsync<T>(reader, mode, cancellationToken).ConfigureAwait(false);
            await AdvanceAsync(reader, cancellationToken).ConfigureAwait(false);
            return row;
        }

        private void Advance()
        {
            if (!_reader.NextResult()) IsConsumed = true;
        }

        private async Task AdvanceAsync(DbDataReader reader, CancellationToken cancellationToken)
        {
            if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false)) IsConsumed = true;
        }

        private void EnsureReadable()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MultiResultReader));
            if (IsConsumed) throw new InvalidOperationException("All result sets have already been read.");
        }

        private DbDataReader AsyncReader() =>
            _reader as DbDataReader
            ?? throw new InvalidOperationException($"Async reads require a {nameof(DbDataReader)}.");
    }
}
