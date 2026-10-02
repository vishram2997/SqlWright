using System;
using System.Collections.Generic;
using System.Linq;
using SqlWright.Internal;

namespace SqlWright.Mapping
{
    /// <summary>
    /// Read-only view of how SqlWright maps a type to a table: the same table, column and key resolution the CRUD
    /// helpers use (attributes, <see cref="SqlWrightSettings.NamingStyle"/>, key conventions).
    /// </summary>
    public sealed class EntityMetadata
    {
        private EntityMetadata(EntityInfo info)
        {
            Type = info.Type;
            TableName = info.Table;
            Schema = info.Schema;
            Columns = info.Columns.Select(c => new EntityColumnMetadata(c)).ToArray();
            Keys = Columns.Where(c => c.IsKey).ToArray();
        }

        /// <summary>Metadata for <typeparamref name="T"/>.</summary>
        public static EntityMetadata For<T>() => For(typeof(T));

        /// <summary>Metadata for <paramref name="type"/>.</summary>
        public static EntityMetadata For(Type type) => new EntityMetadata(EntityInfo.Get(type));

        /// <summary>The mapped type.</summary>
        public Type Type { get; }

        /// <summary>The table name, unquoted.</summary>
        public string TableName { get; }

        /// <summary>The schema, if one was set with <see cref="TableAttribute.Schema"/>.</summary>
        public string? Schema { get; }

        /// <summary>Mapped columns, in member declaration order.</summary>
        public IReadOnlyList<EntityColumnMetadata> Columns { get; }

        /// <summary>Key columns.</summary>
        public IReadOnlyList<EntityColumnMetadata> Keys { get; }
    }

    /// <summary>How one property or field maps to a column.</summary>
    public sealed class EntityColumnMetadata
    {
        internal EntityColumnMetadata(EntityColumn column)
        {
            MemberName = column.Member.Name;
            ColumnName = column.Column;
            Type = column.Type;
            IsKey = column.IsKey;
            IsGenerated = column.IsGenerated;
            IsComputed = column.IsComputed;
        }

        /// <summary>The property or field name.</summary>
        public string MemberName { get; }

        /// <summary>The database column name, unquoted.</summary>
        public string ColumnName { get; }

        /// <summary>The member's type.</summary>
        public Type Type { get; }

        /// <summary>Whether the column is (part of) the key.</summary>
        public bool IsKey { get; }

        /// <summary>Whether the database generates the key value.</summary>
        public bool IsGenerated { get; }

        /// <summary>Whether the column is produced by the database and never written.</summary>
        public bool IsComputed { get; }
    }
}
