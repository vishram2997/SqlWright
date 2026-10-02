using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace SqlWright.Internal
{
    /// <summary>
    /// Everything needed to build a command; captured once per public API call.
    /// </summary>
    internal readonly struct CommandSpec
    {
        public CommandSpec(string sql, object? param, IDbTransaction? transaction, int? commandTimeout,
            CommandType? commandType, CancellationToken cancellationToken = default)
        {
            Sql = sql ?? throw new ArgumentNullException(nameof(sql));
            Param = param;
            Transaction = transaction;
            CommandTimeout = commandTimeout;
            CommandType = commandType;
            CancellationToken = cancellationToken;
        }

        public string Sql { get; }
        public object? Param { get; }
        public IDbTransaction? Transaction { get; }
        public int? CommandTimeout { get; }
        public CommandType? CommandType { get; }
        public CancellationToken CancellationToken { get; }

        public IDbCommand Create(IDbConnection connection) => Create(connection, Param);

        public IDbCommand Create(IDbConnection connection, object? param)
        {
            var cmd = connection.CreateCommand();
            try
            {
                if (Transaction != null) cmd.Transaction = Transaction;
                var timeout = CommandTimeout ?? SqlWrightSettings.CommandTimeout;
                if (timeout.HasValue) cmd.CommandTimeout = timeout.Value;
                if (CommandType.HasValue) cmd.CommandType = CommandType.Value;

                var sql = Sql;
                var isText = (CommandType ?? System.Data.CommandType.Text) == System.Data.CommandType.Text;
                var specs = param is SqlParameters bag ? bag.Specs : ParameterReader.Read(param);
                foreach (var spec in specs)
                    AddParameter(cmd, spec, isText, ref sql);

                cmd.CommandText = sql;
                return cmd;
            }
            catch
            {
                cmd.Dispose();
                throw;
            }
        }

        public DbCommand CreateAsync(DbConnection connection) => (DbCommand)Create(connection, Param);

        private static void AddParameter(IDbCommand cmd, ParameterSpec spec, bool isText, ref string sql)
        {
            if (isText && spec.Direction == ParameterDirection.Input && IsList(spec.Value))
            {
                ExpandList(cmd, spec, ref sql);
                return;
            }

            var p = cmd.CreateParameter();
            p.ParameterName = spec.Name;
            p.Direction = spec.Direction;
            p.Value = ToDbValue(spec.Value);

            if (spec.DbType.HasValue)
                p.DbType = spec.DbType.Value;
            else if (spec.Value is null && spec.DeclaredType != null && TryGetDbType(spec.DeclaredType, out var inferred))
                p.DbType = inferred;

            if (spec.Size.HasValue) p.Size = spec.Size.Value;
            if (spec.Precision.HasValue) p.Precision = spec.Precision.Value;
            if (spec.Scale.HasValue) p.Scale = spec.Scale.Value;

            cmd.Parameters.Add(p);
            spec.Attached = p;
        }

        private static bool IsList(object? value) =>
            value is IEnumerable && !(value is string) && !(value is byte[]) && !(value is char[]);

        /// <summary>
        /// Rewrites <c>IN @ids</c> (or <c>IN (@ids)</c>) to <c>IN (@ids_1, @ids_2, ...)</c> and adds one parameter per item.
        /// An empty list becomes a subquery that matches nothing, so the SQL stays valid.
        /// </summary>
        private static void ExpandList(IDbCommand cmd, ParameterSpec spec, ref string sql)
        {
            var names = new List<string>();
            var n = 0;
            foreach (var item in (IEnumerable)spec.Value!)
            {
                // The underscore keeps "p1" + "1" from colliding with a separate parameter "p11".
                var name = spec.Name + "_" + (++n).ToString(System.Globalization.CultureInfo.InvariantCulture);
                names.Add(name);
                var p = cmd.CreateParameter();
                p.ParameterName = name;
                p.Value = ToDbValue(item);
                if (spec.DbType.HasValue) p.DbType = spec.DbType.Value;
                if (spec.Size.HasValue) p.Size = spec.Size.Value;
                cmd.Parameters.Add(p);
            }

            var pattern = @"(\(\s*)?([@:?$])" + Regex.Escape(spec.Name) + @"(?![\p{L}\p{N}_])(\s*\))?";
            sql = Regex.Replace(sql, pattern, m =>
            {
                var prefix = m.Groups[2].Value;
                var list = names.Count == 0
                    ? "(SELECT NULL WHERE 1 = 0)"
                    : "(" + string.Join(", ", names.Select(x => prefix + x)) + ")";

                var open = m.Groups[1];
                var close = m.Groups[3];
                if (open.Success && close.Success) return list;
                return (open.Success ? open.Value : "") + list + (close.Success ? close.Value : "");
            }, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static object ToDbValue(object? value)
        {
            if (value is null) return DBNull.Value;
            var type = value.GetType();
            if (type.IsEnum) return Convert.ChangeType(value, Enum.GetUnderlyingType(type), System.Globalization.CultureInfo.InvariantCulture)!;
            return value;
        }

        private static readonly Dictionary<Type, DbType> DbTypes = new Dictionary<Type, DbType>
        {
            [typeof(string)] = DbType.String,
            [typeof(byte[])] = DbType.Binary,
            [typeof(bool)] = DbType.Boolean,
            [typeof(byte)] = DbType.Byte,
            [typeof(short)] = DbType.Int16,
            [typeof(int)] = DbType.Int32,
            [typeof(long)] = DbType.Int64,
            [typeof(float)] = DbType.Single,
            [typeof(double)] = DbType.Double,
            [typeof(decimal)] = DbType.Decimal,
            [typeof(DateTime)] = DbType.DateTime,
            [typeof(DateTimeOffset)] = DbType.DateTimeOffset,
            [typeof(TimeSpan)] = DbType.Time,
            [typeof(Guid)] = DbType.Guid,
        };

        /// <summary>
        /// Used only for null values, where the provider can't infer a type from the value itself.
        /// </summary>
        private static bool TryGetDbType(Type type, out DbType dbType)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type.IsEnum) type = Enum.GetUnderlyingType(type);
            return DbTypes.TryGetValue(type, out dbType);
        }
    }
}
