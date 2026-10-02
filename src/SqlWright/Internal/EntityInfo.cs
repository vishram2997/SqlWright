using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace SqlWright.Internal
{
    internal sealed class EntityColumn
    {
        public EntityColumn(MemberMap map, string column, Func<object, object?> getter)
        {
            Member = map.Member;
            Type = map.Type;
            Column = column;
            Alias = map.ColumnName;
            Getter = getter;
        }

        public MemberInfo Member { get; }
        public Type Type { get; }

        /// <summary>The database column name.</summary>
        public string Column { get; }

        /// <summary>The name the row mapper matches this member by; SELECTs alias the column to it when they differ.</summary>
        public string Alias { get; }

        public string ParameterName => Member.Name;
        public Func<object, object?> Getter { get; }
        public bool IsKey { get; set; }
        public bool IsGenerated { get; set; }
        public bool IsComputed { get; set; }

        public void SetValue(object entity, object? value)
        {
            var converted = value is null || value is DBNull ? null : ValueConverter.ChangeType(value, Type);
            if (Member is PropertyInfo property) property.SetValue(entity, converted);
            else ((FieldInfo)Member).SetValue(entity, converted);
        }
    }

    internal sealed class EntityStatements
    {
        public string SelectAll { get; set; } = "";
        public string? SelectById { get; set; }
        public string Insert { get; set; } = "";
        public string? Update { get; set; }
        public string? Delete { get; set; }
    }

    /// <summary>
    /// Table, column and key metadata for an entity type, plus its generated CRUD SQL per dialect.
    /// </summary>
    internal sealed class EntityInfo
    {
        private static readonly ConcurrentDictionary<Type, EntityInfo> Cache = new ConcurrentDictionary<Type, EntityInfo>();
        private readonly ConcurrentDictionary<SqlDialect, EntityStatements> _statements = new ConcurrentDictionary<SqlDialect, EntityStatements>();

        private EntityInfo(Type type)
        {
            Type = type;
            (Table, Schema) = ResolveTable(type);

            var columns = new List<EntityColumn>();
            foreach (var map in TypeMembers.Get(type).Where(m => m.CanRead && m.CanWrite))
            {
                var column = TypeMembers.ColumnName(map.Member) ?? ApplyNaming(map.Member.Name);
                columns.Add(new EntityColumn(map, column, CompileGetter(type, map.Member))
                {
                    IsKey = HasAttribute(map.Member, "KeyAttribute"),
                    IsComputed = HasAttribute(map.Member, "ComputedAttribute"),
                });
            }
            Columns = columns.ToArray();

            if (!Columns.Any(c => c.IsKey))
            {
                var conventional = Columns.FirstOrDefault(c => string.Equals(c.Member.Name, "Id", StringComparison.OrdinalIgnoreCase))
                    ?? Columns.FirstOrDefault(c => string.Equals(c.Member.Name, type.Name + "Id", StringComparison.OrdinalIgnoreCase));
                if (conventional != null) conventional.IsKey = true;
            }
            Keys = Columns.Where(c => c.IsKey).ToArray();

            foreach (var column in Columns)
            {
                var generated = ExplicitGenerated(column.Member);
                if (column.IsKey)
                    column.IsGenerated = generated ?? (Keys.Length == 1 && IsInteger(column.Type));
                else if (generated == true)
                    column.IsComputed = true;
            }

            var generatedKeys = Keys.Where(k => k.IsGenerated).ToArray();
            if (generatedKeys.Length > 1)
                throw new SqlWrightException(
                    $"{Diagnostics.TypeName(type)} has more than one database-generated key ({string.Join(", ", generatedKeys.Select(k => k.Member.Name))}). " +
                    "At most one key can be generated; set [Key(DatabaseGenerated = false)] on the others.");
            GeneratedKey = generatedKeys.FirstOrDefault();
        }

        public Type Type { get; }
        public string Table { get; }
        public string? Schema { get; }
        public EntityColumn[] Columns { get; }
        public EntityColumn[] Keys { get; }
        public EntityColumn? GeneratedKey { get; }

        public IEnumerable<EntityColumn> InsertColumns => Columns.Where(c => !c.IsComputed && !c.IsGenerated);
        public IEnumerable<EntityColumn> UpdateColumns => Columns.Where(c => !c.IsComputed && !c.IsKey);

        public static EntityInfo Get(Type type) => Cache.GetOrAdd(type, t => new EntityInfo(t));

        public static void ClearCache() => Cache.Clear();

        public EntityStatements For(SqlDialect dialect) => _statements.GetOrAdd(dialect, Build);

        public SqlParameters EntityParameters(object entity, IEnumerable<EntityColumn> columns)
        {
            var parameters = new SqlParameters();
            foreach (var column in columns)
                parameters.AddSpec(new ParameterSpec(column.ParameterName, column.Getter(entity), column.Type));
            return parameters;
        }

        /// <summary>
        /// Key parameters from an id: a plain value for single keys, or an object (anonymous, or the entity itself)
        /// with a property per key member.
        /// </summary>
        public SqlParameters KeyParameters(object id, string operation)
        {
            if (id is null) throw new ArgumentNullException(nameof(id));
            RequireKey(operation);

            var parameters = new SqlParameters();
            if (Keys.Length == 1 && ValueConverter.IsScalar(id.GetType()))
            {
                parameters.AddSpec(new ParameterSpec(Keys[0].ParameterName, id, Keys[0].Type));
                return parameters;
            }

            if (Type.IsInstanceOfType(id)) return EntityParameters(id, Keys);

            var supplied = ParameterReader.Read(id).ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var key in Keys)
            {
                if (!supplied.TryGetValue(key.ParameterName, out var spec))
                    throw new SqlWrightException(
                        $"{operation}<{Diagnostics.TypeName(Type)}> needs a value for key '{key.ParameterName}', but the id object " +
                        $"({Diagnostics.TypeName(id.GetType())}) has no such member. " +
                        (Keys.Length > 1
                            ? $"Pass an object with all key members, e.g. new {{ {string.Join(", ", Keys.Select(k => k.ParameterName + " = ..."))} }}."
                            : $"Pass the key value itself, or an object with a '{key.ParameterName}' member."));
                parameters.AddSpec(new ParameterSpec(key.ParameterName, spec.Value, key.Type));
            }
            return parameters;
        }

        public void RequireKey(string operation)
        {
            if (Keys.Length > 0) return;
            throw new SqlWrightException(
                $"{operation}<{Diagnostics.TypeName(Type)}> needs a key, but {Diagnostics.TypeName(Type)} has none. " +
                $"Mark the key with [Key], or name it 'Id' or '{Type.Name}Id'.");
        }

        private EntityStatements Build(SqlDialect dialect)
        {
            var table = dialect.QuoteTableName(Table, Schema);
            string Assign(EntityColumn c) => dialect.QuoteIdentifier(c.Column) + " = " + dialect.ParameterPrefix + c.ParameterName;
            var where = Keys.Length == 0 ? null : " WHERE " + string.Join(" AND ", Keys.Select(Assign));

            var select = new StringBuilder("SELECT ");
            select.Append(string.Join(", ", Columns.Select(c =>
                c.Column == c.Alias
                    ? dialect.QuoteIdentifier(c.Column)
                    : dialect.QuoteIdentifier(c.Column) + " AS " + dialect.QuoteIdentifier(c.Alias))));
            select.Append(" FROM ").Append(table);
            var selectAll = select.ToString();

            var insertColumns = InsertColumns.ToList();
            var updateColumns = UpdateColumns.ToList();

            return new EntityStatements
            {
                SelectAll = selectAll,
                SelectById = where == null ? null : selectAll + where,
                Insert = dialect.BuildInsert(table,
                    insertColumns.Select(c => c.Column).ToList(),
                    insertColumns.Select(c => c.ParameterName).ToList(),
                    GeneratedKey?.Column),
                Update = where == null || updateColumns.Count == 0
                    ? null
                    : "UPDATE " + table + " SET " + string.Join(", ", updateColumns.Select(Assign)) + where,
                Delete = where == null ? null : "DELETE FROM " + table + where,
            };
        }

        private static (string Table, string? Schema) ResolveTable(Type type)
        {
            foreach (var attr in type.GetCustomAttributes(true))
            {
                var attrType = attr.GetType();
                if (attrType.Name != "TableAttribute") continue;
                if (attrType.GetProperty("Name")?.GetValue(attr) is string name && name.Length > 0)
                    return (name, attrType.GetProperty("Schema")?.GetValue(attr) as string);
            }

            var resolver = SqlWrightSettings.TableNameResolver;
            return (resolver != null ? resolver(type) : ApplyNaming(type.Name), null);
        }

        private static bool HasAttribute(MemberInfo member, string name) =>
            member.GetCustomAttributes(true).Any(a => a.GetType().Name == name);

        /// <summary>
        /// Reads SqlWright's [Key(DatabaseGenerated = ...)] or DataAnnotations' [DatabaseGenerated(option)].
        /// </summary>
        private static bool? ExplicitGenerated(MemberInfo member)
        {
            foreach (var attr in member.GetCustomAttributes(true))
            {
                if (attr is Mapping.KeyAttribute key && key.Generated.HasValue) return key.Generated;

                var attrType = attr.GetType();
                if (attrType.Name == "DatabaseGeneratedAttribute")
                    return attrType.GetProperty("DatabaseGeneratedOption")?.GetValue(attr)?.ToString() != "None";
            }
            return null;
        }

        private static bool IsInteger(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            return type == typeof(int) || type == typeof(long) || type == typeof(short)
                || type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort);
        }

        private static Func<object, object?> CompileGetter(Type type, MemberInfo member)
        {
            var obj = Expression.Parameter(typeof(object), "obj");
            var access = Expression.MakeMemberAccess(Expression.Convert(obj, type), member);
            return Expression.Lambda<Func<object, object?>>(Expression.Convert(access, typeof(object)), obj).Compile();
        }

        internal static string ApplyNaming(string name) =>
            SqlWrightSettings.NamingStyle == NamingStyle.SnakeCase ? ToSnakeCase(name) : name;

        /// <summary>FirstName -> first_name, UserID -> user_id, HTMLParser -> html_parser.</summary>
        internal static string ToSnakeCase(string name)
        {
            var sb = new StringBuilder(name.Length + 4);
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                if (char.IsUpper(c))
                {
                    var prevIsLowerOrDigit = i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]));
                    var startsNewWord = i > 0 && char.IsUpper(name[i - 1]) && i + 1 < name.Length && char.IsLower(name[i + 1]);
                    if ((prevIsLowerOrDigit || startsNewWord) && sb.Length > 0 && sb[sb.Length - 1] != '_') sb.Append('_');
                    sb.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }
    }
}
