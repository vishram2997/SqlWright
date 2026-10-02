using System;
using System.Collections.Generic;
using System.Data;
using SqlWright.Internal;

namespace SqlWright
{
    /// <summary>
    /// A parameter bag for when an anonymous object is not enough: explicit <see cref="DbType"/>s,
    /// sizes, and output / return-value parameters.
    /// </summary>
    /// <example>
    /// <code>
    /// var p = new SqlParameters(new { Name = "Ada" });
    /// p.Add("Id", direction: ParameterDirection.Output, dbType: DbType.Int32);
    /// connection.Execute("CreateUser", p, commandType: CommandType.StoredProcedure);
    /// int id = p.Get&lt;int&gt;("Id");
    /// </code>
    /// </example>
    public sealed class SqlParameters
    {
        private readonly List<ParameterSpec> _parameters = new List<ParameterSpec>();
        private readonly Dictionary<string, int> _index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Creates an empty parameter bag.</summary>
        public SqlParameters()
        {
        }

        /// <summary>Creates a parameter bag pre-populated from an object's properties or a dictionary.</summary>
        public SqlParameters(object? template)
        {
            AddObject(template);
        }

        /// <summary>The names of all parameters in the bag, without prefixes.</summary>
        public IEnumerable<string> ParameterNames
        {
            get
            {
                foreach (var p in _parameters) yield return p.Name;
            }
        }

        /// <summary>
        /// Adds (or replaces) a parameter. The name may include a provider prefix such as <c>@</c>; it is stripped.
        /// </summary>
        public SqlParameters Add(
            string name,
            object? value = null,
            DbType? dbType = null,
            ParameterDirection direction = ParameterDirection.Input,
            int? size = null,
            byte? precision = null,
            byte? scale = null)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Parameter name is required.", nameof(name));

            var spec = new ParameterSpec(ParameterSpec.Clean(name), value, value?.GetType())
            {
                DbType = dbType,
                Direction = direction,
                Size = size,
                Precision = precision,
                Scale = scale,
            };
            AddSpec(spec);
            return this;
        }

        /// <summary>Adds every property of an object (or entry of a dictionary) as an input parameter.</summary>
        public SqlParameters AddObject(object? template)
        {
            if (template is SqlParameters other)
            {
                foreach (var p in other._parameters) Add(p.Name, p.Value, p.DbType, p.Direction, p.Size, p.Precision, p.Scale);
                return this;
            }

            foreach (var spec in ParameterReader.Read(template))
                Add(spec.Name, spec.Value);
            return this;
        }

        /// <summary>
        /// Reads a parameter's value after the command has executed, which is how output and
        /// return-value parameters are retrieved.
        /// </summary>
        public T Get<T>(string name)
        {
            if (!_index.TryGetValue(ParameterSpec.Clean(name), out var i))
                throw new KeyNotFoundException($"No parameter named '{name}' was added.");

            var spec = _parameters[i];
            var value = spec.Attached != null ? spec.Attached.Value : spec.Value;
            return ValueConverter.To<T>(value);
        }

        internal IReadOnlyList<ParameterSpec> Specs => _parameters;

        internal void AddSpec(ParameterSpec spec)
        {
            if (_index.TryGetValue(spec.Name, out var existing))
            {
                _parameters[existing] = spec;
            }
            else
            {
                _index[spec.Name] = _parameters.Count;
                _parameters.Add(spec);
            }
        }
    }
}
