using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using SqlWright.Internal;

namespace SqlWright
{
    /// <summary>
    /// SQL text whose interpolated values become parameters, so string interpolation is safe from SQL injection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every SqlWright method has an overload taking <see cref="Sql"/>, so you can pass an interpolated string directly:
    /// <code>
    /// var users = connection.Query&lt;User&gt;($"SELECT * FROM Users WHERE Age &gt; {minAge} AND Id IN {ids}");
    /// // SELECT * FROM Users WHERE Age &gt; @p0 AND Id IN (@p1_1, @p1_2, ...)
    /// </code>
    /// </para>
    /// <para>
    /// Build queries in pieces by declaring a <see cref="Sql"/> and appending to it. Embedding one <see cref="Sql"/>
    /// in another splices its text and parameters in:
    /// <code>
    /// Sql filter = $"Status = {status}";
    /// if (name != null) filter.Append($" AND Name = {name}");
    /// var rows = connection.Query&lt;User&gt;($"SELECT * FROM Users WHERE {filter}");
    /// </code>
    /// </para>
    /// <para>
    /// Use the <c>raw</c> format to insert text verbatim, e.g. an identifier: <c>$"SELECT * FROM {table:raw}"</c>.
    /// Never use <c>:raw</c> with user input.
    /// </para>
    /// <para>
    /// If every hole is a string constant (e.g. <c>{"ada"}</c>), C# folds the whole expression into a constant
    /// string, which binds to the plain-<see cref="string"/> overloads and is not parameterised. Use variables,
    /// or assign to a <see cref="Sql"/> first.
    /// </para>
    /// <para>Instances are mutable and not thread-safe.</para>
    /// </remarks>
    [InterpolatedStringHandler]
    public sealed class Sql
    {
        private readonly List<object> _parts;

        /// <summary>Creates an empty <see cref="Sql"/> to append to.</summary>
        public Sql()
        {
            _parts = new List<object>();
        }

        /// <summary>Used by the compiler for interpolated strings.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public Sql(int literalLength, int formattedCount)
        {
            _parts = new List<object>(formattedCount * 2 + 1);
        }

        /// <summary>Creates a <see cref="Sql"/> from trusted text, with no parameters. Never pass user input.</summary>
        public static Sql Raw(string text)
        {
            var sql = new Sql();
            sql.AppendLiteral(text);
            return sql;
        }

        /// <summary>Joins fragments with a separator, e.g. <c>Sql.Join(" AND ", conditions)</c>.</summary>
        public static Sql Join(string separator, IEnumerable<Sql> fragments)
        {
            var result = new Sql();
            var first = true;
            foreach (var fragment in fragments)
            {
                if (!first) result.AppendLiteral(separator);
                result.Append(fragment);
                first = false;
            }
            return result;
        }

        /// <summary>True when no text or parameters have been added.</summary>
        public bool IsEmpty => _parts.Count == 0;

        /// <summary>The SQL text with <c>@p0</c>-style placeholders, useful for logging and debugging.</summary>
        public string Text => Render("@", out _);

        /// <summary>The parameter values, in placeholder order (<c>@p0</c>, <c>@p1</c>, ...).</summary>
        public IReadOnlyList<object?> Values
        {
            get
            {
                var values = new List<object?>();
                foreach (var part in _parts)
                    if (part is Argument arg) values.Add(arg.Value);
                return values;
            }
        }

        /// <summary>Appends another fragment (its text and parameters). Returns this instance.</summary>
        public Sql Append(Sql fragment)
        {
            if (fragment is null) throw new ArgumentNullException(nameof(fragment));
            _parts.AddRange(ReferenceEquals(fragment, this) ? _parts.ToArray() : (IEnumerable<object>)fragment._parts);
            return this;
        }

        /// <summary>Used by the compiler for interpolated strings.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void AppendLiteral(string value)
        {
            if (!string.IsNullOrEmpty(value)) _parts.Add(value);
        }

        /// <summary>Used by the compiler for interpolated strings: adds <paramref name="value"/> as a parameter.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void AppendFormatted<T>(T value) => AppendFormatted(value, null);

        /// <summary>Used by the compiler for interpolated strings. The only supported format is <c>raw</c>.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void AppendFormatted<T>(T value, string? format)
        {
            if (value is Sql fragment)
            {
                Append(fragment);
                return;
            }

            if (format != null)
            {
                if (!string.Equals(format, "raw", StringComparison.OrdinalIgnoreCase))
                    throw new FormatException(
                        $"Unsupported format ':{format}' in interpolated SQL. Values become typed parameters, so format them in .NET " +
                        "before interpolating if you need to. The only supported format is ':raw', which inserts trusted text verbatim.");
                AppendLiteral(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
                return;
            }

            _parts.Add(new Argument(value, typeof(T)));
        }

        /// <summary>Returns <see cref="Text"/>.</summary>
        public override string ToString() => Text;

        internal string Render(string prefix, out SqlParameters parameters)
        {
            var sb = new StringBuilder();
            parameters = new SqlParameters();
            var n = 0;
            foreach (var part in _parts)
            {
                if (part is string text)
                {
                    sb.Append(text);
                    continue;
                }

                var arg = (Argument)part;
                var name = "p" + n++.ToString(CultureInfo.InvariantCulture);
                sb.Append(prefix).Append(name);
                parameters.AddSpec(new ParameterSpec(name, arg.Value, arg.Type));
            }
            return sb.ToString();
        }

        private sealed class Argument
        {
            public Argument(object? value, Type type)
            {
                Value = value;
                Type = type;
            }

            public object? Value { get; }
            public Type Type { get; }
        }
    }
}
