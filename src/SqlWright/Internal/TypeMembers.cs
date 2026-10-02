using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace SqlWright.Internal
{
    internal sealed class MemberMap
    {
        public MemberMap(MemberInfo member, Type type, string columnName, bool canRead, bool canWrite)
        {
            Member = member;
            Type = type;
            ColumnName = columnName;
            CanRead = canRead;
            CanWrite = canWrite;
        }

        public MemberInfo Member { get; }
        public Type Type { get; }
        public string ColumnName { get; }
        public bool CanRead { get; }
        public bool CanWrite { get; }
    }

    /// <summary>
    /// Discovers the mappable public properties and fields of a type.
    /// </summary>
    internal static class TypeMembers
    {
        private static readonly ConcurrentDictionary<Type, MemberMap[]> Cache = new ConcurrentDictionary<Type, MemberMap[]>();

        public static MemberMap[] Get(Type type) => Cache.GetOrAdd(type, Discover);

        private static MemberMap[] Discover(Type type)
        {
            var result = new List<MemberMap>();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;

            foreach (var prop in type.GetProperties(flags))
            {
                if (prop.GetIndexParameters().Length > 0 || IsIgnored(prop)) continue;
                result.Add(new MemberMap(prop, prop.PropertyType, ColumnName(prop) ?? prop.Name,
                    canRead: prop.GetGetMethod() != null,
                    canWrite: prop.GetSetMethod() != null));
            }

            foreach (var field in type.GetFields(flags))
            {
                if (IsIgnored(field)) continue;
                result.Add(new MemberMap(field, field.FieldType, ColumnName(field) ?? field.Name,
                    canRead: true,
                    canWrite: !field.IsInitOnly && !field.IsLiteral));
            }

            return result.ToArray();
        }

        /// <summary>
        /// Recognises SqlWright's attributes and the DataAnnotations equivalents by name,
        /// so users don't need a dependency on either package.
        /// </summary>
        private static bool IsIgnored(ICustomAttributeProvider member) =>
            member.GetCustomAttributes(true).Any(a => a.GetType().Name == "NotMappedAttribute");

        internal static string? ColumnName(ICustomAttributeProvider member)
        {
            foreach (var attr in member.GetCustomAttributes(true))
            {
                var attrType = attr.GetType();
                if (attrType.Name != "ColumnAttribute") continue;
                if (attrType.GetProperty("Name")?.GetValue(attr) is string name && name.Length > 0)
                    return name;
            }
            return null;
        }

        public static bool NamesMatch(string column, string member)
        {
            if (string.Equals(column, member, StringComparison.OrdinalIgnoreCase)) return true;
            return SqlWrightSettings.MatchNamesWithUnderscores
                && string.Equals(column.Replace("_", ""), member.Replace("_", ""), StringComparison.OrdinalIgnoreCase);
        }
    }
}
