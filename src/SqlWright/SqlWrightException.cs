using System;

namespace SqlWright
{
    /// <summary>
    /// Base type for errors raised by SqlWright itself (as opposed to errors from the database provider,
    /// which are always passed through unchanged).
    /// </summary>
    public class SqlWrightException : InvalidOperationException
    {
        /// <summary>Creates the exception.</summary>
        public SqlWrightException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// A result set could not be mapped to the requested type. The message names the column, member and types involved.
    /// </summary>
    public sealed class MappingException : SqlWrightException
    {
        /// <summary>Creates the exception.</summary>
        public MappingException(string message, Type targetType, string? columnName = null, int? columnOrdinal = null,
            string? memberName = null, Exception? innerException = null)
            : base(message, innerException)
        {
            TargetType = targetType;
            ColumnName = columnName;
            ColumnOrdinal = columnOrdinal;
            MemberName = memberName;
        }

        /// <summary>The type rows were being mapped to.</summary>
        public Type TargetType { get; }

        /// <summary>The column involved, when the problem is specific to one column.</summary>
        public string? ColumnName { get; }

        /// <summary>The zero-based ordinal of <see cref="ColumnName"/> in the result set.</summary>
        public int? ColumnOrdinal { get; }

        /// <summary>The property, field or constructor parameter involved, if any.</summary>
        public string? MemberName { get; }
    }
}
