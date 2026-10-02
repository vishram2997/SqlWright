using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace SqlWright.Internal
{
    internal enum RowMode
    {
        First,
        FirstOrDefault,
        Single,
        SingleOrDefault,
    }

    /// <summary>
    /// Shared result-set reading logic for the extension methods and <see cref="MultiResultReader"/>.
    /// </summary>
    internal static class RowReader
    {
        public static CommandBehavior Behavior(RowMode mode) =>
            mode == RowMode.First || mode == RowMode.FirstOrDefault
                ? CommandBehavior.SingleResult | CommandBehavior.SingleRow
                : CommandBehavior.SingleResult;

        public static List<T> ReadAll<T>(IDataReader reader)
        {
            var list = new List<T>();
            if (reader.FieldCount == 0) return list;
            var map = RowMapper.Get<T>(reader);
            while (reader.Read()) list.Add(map(reader));
            return list;
        }

        public static async Task<List<T>> ReadAllAsync<T>(DbDataReader reader, CancellationToken ct)
        {
            var list = new List<T>();
            if (reader.FieldCount == 0) return list;
            var map = RowMapper.Get<T>(reader);
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) list.Add(map(reader));
            return list;
        }

        public static T ReadRow<T>(IDataReader reader, RowMode mode)
        {
            if (reader.FieldCount > 0 && reader.Read())
            {
                var result = RowMapper.Get<T>(reader)(reader);
                if (IsSingle(mode) && reader.Read()) throw MoreThanOneRow<T>(mode);
                return result;
            }
            if (mode == RowMode.First || mode == RowMode.Single) throw NoRows<T>(mode);
            return default!;
        }

        public static async Task<T> ReadRowAsync<T>(DbDataReader reader, RowMode mode, CancellationToken ct)
        {
            if (reader.FieldCount > 0 && await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var result = RowMapper.Get<T>(reader)(reader);
                if (IsSingle(mode) && await reader.ReadAsync(ct).ConfigureAwait(false)) throw MoreThanOneRow<T>(mode);
                return result;
            }
            if (mode == RowMode.First || mode == RowMode.Single) throw NoRows<T>(mode);
            return default!;
        }

        private static bool IsSingle(RowMode mode) => mode == RowMode.Single || mode == RowMode.SingleOrDefault;

        private static Exception NoRows<T>(RowMode mode) => new SqlWrightException(
            $"{Describe<T>(mode)} expected {(mode == RowMode.Single ? "exactly one row" : "at least one row")}, but the query returned none. " +
            $"Use {(mode == RowMode.Single ? "SingleOrDefault" : "FirstOrDefault")} if no rows is a valid outcome.");

        private static Exception MoreThanOneRow<T>(RowMode mode) => new SqlWrightException(
            $"{Describe<T>(mode)} expected {(mode == RowMode.Single ? "exactly one row" : "at most one row")}, but the query returned more than one. " +
            "Use First/FirstOrDefault if you only need the first row, or tighten the WHERE clause.");

        private static string Describe<T>(RowMode mode) => $"{mode}<{Diagnostics.TypeName(typeof(T))}>";
    }
}
