using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Dynamic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace SqlWright.Internal
{
    /// <summary>
    /// Builds and caches compiled delegates that turn the current row of a reader into a <c>T</c>.
    /// Mappers are cached per target type and result-set shape (column names).
    /// </summary>
    internal static class RowMapper
    {
        private static readonly ConcurrentDictionary<(Type Type, string Columns), Delegate> Cache =
            new ConcurrentDictionary<(Type, string), Delegate>();

        private static readonly MethodInfo GetValueMethod = typeof(IDataRecord).GetMethod(nameof(IDataRecord.GetValue))!;
        private static readonly MethodInfo ConvertMethod = typeof(ValueConverter).GetMethod(nameof(ValueConverter.To))!;

        public static void ClearCache() => Cache.Clear();

        public static Func<IDataReader, T> Get<T>(IDataReader reader)
        {
            var type = typeof(T);

            if (type == typeof(object))
                return (Func<IDataReader, T>)(object)(Func<IDataReader, object>)MapDynamic;

            if (ValueConverter.IsScalar(type))
                return ScalarMapper<T>.Instance;

            var key = (type, Signature(reader));
            if (Cache.TryGetValue(key, out var cached))
                return (Func<IDataReader, T>)cached;

            var mapper = Build<T>(reader);
            Cache.TryAdd(key, mapper);
            return mapper;
        }

        public static object MapDynamic(IDataReader reader)
        {
            IDictionary<string, object?> row = new ExpandoObject();
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            return row;
        }

        private static class ScalarMapper<T>
        {
            public static readonly Func<IDataReader, T> Instance = r =>
            {
                try
                {
                    return ValueConverter.To<T>(r.GetValue(0));
                }
                catch (Exception ex)
                {
                    var step = new MappingStep(0, r.GetName(0), "the result value", typeof(T));
                    throw Diagnostics.ExplainMappingFailure(typeof(T), new[] { step }, r, ex);
                }
            };
        }

        private static string Signature(IDataReader reader)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < reader.FieldCount; i++)
                sb.Append(reader.GetName(i)).Append('\u0001');
            return sb.ToString();
        }

        private static Func<IDataReader, T> Build<T>(IDataReader reader)
        {
            var type = typeof(T);
            var typeName = Diagnostics.TypeName(type);
            var columns = new string[reader.FieldCount];
            for (var i = 0; i < columns.Length; i++) columns[i] = reader.GetName(i);

            var members = TypeMembers.Get(type);
            var steps = new List<MappingStep>();
            var r = Expression.Parameter(typeof(IDataReader), "r");
            Expression Read(int ordinal, Type target, string targetName)
            {
                steps.Add(new MappingStep(ordinal, columns[ordinal], targetName, target));
                return Expression.Call(ConvertMethod.MakeGenericMethod(target),
                    Expression.Call(r, GetValueMethod, Expression.Constant(ordinal)));
            }

            var usedColumns = new HashSet<int>();
            NewExpression create;

            var defaultCtor = type.GetConstructor(Type.EmptyTypes);
            if (type.IsValueType || defaultCtor != null)
            {
                create = defaultCtor != null ? Expression.New(defaultCtor) : Expression.New(type);
            }
            else
            {
                create = BuildConstructorCall(type, columns, members, usedColumns, Read);
            }

            var bindings = new List<MemberBinding>();
            var boundMembers = new HashSet<MemberInfo>();
            for (var i = 0; i < columns.Length; i++)
            {
                if (usedColumns.Contains(i)) continue;
                var member = FindMember(members, columns[i], m => m.CanWrite && !boundMembers.Contains(m.Member));
                if (member == null) continue;

                boundMembers.Add(member.Member);
                usedColumns.Add(i);
                bindings.Add(Expression.Bind(member.Member, Read(i, member.Type, $"{typeName}.{member.Member.Name}")));
            }

            CheckUnmappedColumns(type, columns, members, usedColumns);

            Expression body = bindings.Count > 0 ? Expression.MemberInit(create, bindings) : (Expression)create;
            var compiled = Expression.Lambda<Func<IDataReader, T>>(body, r).Compile();
            var plan = steps.ToArray();

            return row =>
            {
                try
                {
                    return compiled(row);
                }
                catch (Exception ex) when (!(ex is MappingException))
                {
                    throw Diagnostics.ExplainMappingFailure(type, plan, row, ex);
                }
            };
        }

        private static void CheckUnmappedColumns(Type type, string[] columns, MemberMap[] members, HashSet<int> usedColumns)
        {
            if (columns.Length == 0) return;

            var memberNames = members.Where(m => m.CanWrite).Select(m => m.ColumnName).ToList();
            var typeName = Diagnostics.TypeName(type);

            if (usedColumns.Count == 0)
            {
                var hints = string.Concat(columns.Select(c => Diagnostics.ColumnHint(c, memberNames)).Where(h => h.Length > 0).Take(3));
                throw new MappingException(
                    $"None of the columns ({string.Join(", ", columns.Select(c => $"'{c}'"))}) match a settable property or field of {typeName}." +
                    (hints.Length > 0 ? hints : $" Its settable members are: {string.Join(", ", memberNames)}."),
                    type);
            }

            if (!SqlWrightSettings.StrictMapping) return;

            for (var i = 0; i < columns.Length; i++)
            {
                if (usedColumns.Contains(i)) continue;
                throw new MappingException(
                    $"Column '{columns[i]}' does not match any settable property or field of {typeName}." +
                    Diagnostics.ColumnHint(columns[i], memberNames) +
                    " (Reported because SqlWrightSettings.StrictMapping is on.)",
                    type, columns[i], i);
            }
        }

        /// <summary>
        /// Picks the public constructor with the most parameters where every parameter matches a column.
        /// This is what makes positional records and immutable classes work.
        /// </summary>
        private static NewExpression BuildConstructorCall(
            Type type, string[] columns, MemberMap[] members, HashSet<int> usedColumns, Func<int, Type, string, Expression> read)
        {
            var ctors = type.GetConstructors().Where(c => c.GetParameters().Length > 0)
                .OrderByDescending(c => c.GetParameters().Length).ToList();
            var problems = new StringBuilder();

            foreach (var ctor in ctors)
            {
                var parameters = ctor.GetParameters();
                var ordinals = new int[parameters.Length];
                var missing = new List<string>();
                for (var p = 0; p < parameters.Length; p++)
                {
                    var name = ParameterColumnName(parameters[p], members);
                    ordinals[p] = Array.FindIndex(columns, c => TypeMembers.NamesMatch(c, name));
                    if (ordinals[p] < 0)
                    {
                        var suggestion = Diagnostics.Suggest(name, columns, out _);
                        missing.Add(suggestion != null ? $"'{name}' (closest column: '{suggestion}')" : $"'{name}'");
                    }
                }

                if (missing.Count > 0)
                {
                    problems.Append($"\n  ({string.Join(", ", parameters.Select(p => p.Name))}): no column for {string.Join(", ", missing)}");
                    continue;
                }

                var args = new Expression[parameters.Length];
                for (var p = 0; p < parameters.Length; p++)
                {
                    args[p] = read(ordinals[p], parameters[p].ParameterType, $"constructor parameter '{parameters[p].Name}'");
                    usedColumns.Add(ordinals[p]);
                }
                return Expression.New(ctor, args);
            }

            var typeName = Diagnostics.TypeName(type);
            throw new MappingException(
                $"Cannot create {typeName}: it has no public parameterless constructor, and no public constructor has a column " +
                $"for every parameter. Columns: {string.Join(", ", columns.Select(c => $"'{c}'"))}." +
                (problems.Length > 0 ? "\nConstructors tried:" + problems : $"\n{typeName} has no public constructors."),
                type);
        }

        private static string ParameterColumnName(ParameterInfo parameter, MemberMap[] members)
        {
            var explicitName = TypeMembers.ColumnName(parameter);
            if (explicitName != null) return explicitName;

            // Records put [property: Column("x")] on the generated property, so look there too.
            var member = members.FirstOrDefault(m => string.Equals(m.Member.Name, parameter.Name, StringComparison.OrdinalIgnoreCase));
            return member?.ColumnName ?? parameter.Name!;
        }

        private static MemberMap? FindMember(MemberMap[] members, string column, Func<MemberMap, bool> filter)
        {
            foreach (var m in members)
                if (filter(m) && string.Equals(m.ColumnName, column, StringComparison.OrdinalIgnoreCase))
                    return m;

            if (!SqlWrightSettings.MatchNamesWithUnderscores) return null;

            foreach (var m in members)
                if (filter(m) && TypeMembers.NamesMatch(column, m.ColumnName))
                    return m;

            return null;
        }
    }
}
