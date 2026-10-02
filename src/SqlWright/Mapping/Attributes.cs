using System;

namespace SqlWright.Mapping
{
    /// <summary>
    /// Maps a property, field or constructor parameter to a column with a different name.
    /// <c>System.ComponentModel.DataAnnotations.Schema.ColumnAttribute</c> is honoured as well.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
    public sealed class ColumnAttribute : Attribute
    {
        /// <summary>Creates the attribute.</summary>
        /// <param name="name">The column name in the result set.</param>
        public ColumnAttribute(string name) => Name = name;

        /// <summary>The column name in the result set.</summary>
        public string Name { get; }
    }

    /// <summary>
    /// Excludes a property or field from row mapping, parameter binding and CRUD statements.
    /// <c>System.ComponentModel.DataAnnotations.Schema.NotMappedAttribute</c> is honoured as well.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class NotMappedAttribute : Attribute
    {
    }

    /// <summary>
    /// Sets the table used by the CRUD helpers. Without it, the type name is used (see <see cref="SqlWrightSettings.NamingStyle"/>).
    /// <c>System.ComponentModel.DataAnnotations.Schema.TableAttribute</c> is honoured as well.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public sealed class TableAttribute : Attribute
    {
        /// <summary>Creates the attribute.</summary>
        public TableAttribute(string name) => Name = name;

        /// <summary>The table name.</summary>
        public string Name { get; }

        /// <summary>The schema, e.g. <c>dbo</c>. Optional.</summary>
        public string? Schema { get; set; }
    }

    /// <summary>
    /// Marks a key property. Without any <c>[Key]</c>, a member named <c>Id</c> or <c>{TypeName}Id</c> is the key.
    /// Apply to several members for a composite key.
    /// <c>System.ComponentModel.DataAnnotations.KeyAttribute</c> is honoured as well.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class KeyAttribute : Attribute
    {
        internal bool? Generated { get; private set; }

        /// <summary>
        /// Whether the database generates the value (identity / auto-increment / serial). When it does, <c>Insert</c>
        /// leaves it out and writes the generated value back to the entity.
        /// If not set, a single integer key is assumed to be database-generated and any other key is not.
        /// </summary>
        public bool DatabaseGenerated
        {
            get => Generated ?? false;
            set => Generated = value;
        }
    }

    /// <summary>
    /// Marks a column whose value the database produces (defaults, computed columns, row versions).
    /// It is read by queries but never written by <c>Insert</c> or <c>Update</c>.
    /// <c>[DatabaseGenerated(DatabaseGeneratedOption.Computed)]</c> from DataAnnotations is honoured as well.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class ComputedAttribute : Attribute
    {
    }
}
