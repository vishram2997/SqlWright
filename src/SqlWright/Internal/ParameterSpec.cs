using System;
using System.Data;

namespace SqlWright.Internal
{
    internal sealed class ParameterSpec
    {
        public ParameterSpec(string name, object? value, Type? declaredType)
        {
            Name = name;
            Value = value;
            DeclaredType = declaredType;
        }

        public string Name { get; }
        public object? Value { get; }
        public Type? DeclaredType { get; }
        public DbType? DbType { get; set; }
        public ParameterDirection Direction { get; set; } = ParameterDirection.Input;
        public int? Size { get; set; }
        public byte? Precision { get; set; }
        public byte? Scale { get; set; }

        /// <summary>The provider parameter created for this spec on the last execution, used to read outputs.</summary>
        public IDbDataParameter? Attached { get; set; }

        public static string Clean(string name) => name.TrimStart('@', ':', '?', '$');
    }
}
