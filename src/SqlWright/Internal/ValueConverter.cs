using System;
using System.Globalization;

namespace SqlWright.Internal
{
    /// <summary>
    /// Converts raw provider values into CLR types. Called from compiled row mappers.
    /// </summary>
    internal static class ValueConverter
    {
        public static T To<T>(object? value)
        {
            if (value is T typed) return typed;
            if (value is null || value is DBNull) return default!;
            return (T)ChangeType(value, typeof(T));
        }

        public static object ChangeType(object value, Type target)
        {
            var type = Nullable.GetUnderlyingType(target) ?? target;
            if (type.IsInstanceOfType(value)) return value;

            if (type.IsEnum)
            {
                if (value is string name) return Enum.Parse(type, name, ignoreCase: true);
                var raw = Convert.ChangeType(value, Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture);
                return Enum.ToObject(type, raw!);
            }

            if (type == typeof(Guid))
            {
                if (value is string g) return Guid.Parse(g);
                if (value is byte[] bytes && bytes.Length == 16) return new Guid(bytes);
            }
            else if (type == typeof(DateTime) && value is string dt)
            {
                return DateTime.Parse(dt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            }
            else if (type == typeof(DateTimeOffset))
            {
                if (value is DateTime d) return new DateTimeOffset(d);
                if (value is string s) return DateTimeOffset.Parse(s, CultureInfo.InvariantCulture);
            }
            else if (type == typeof(TimeSpan) && value is string ts)
            {
                return TimeSpan.Parse(ts, CultureInfo.InvariantCulture);
            }
            else if (type == typeof(char) && value is string c && c.Length == 1)
            {
                return c[0];
            }
#if NET6_0_OR_GREATER
            else if (type == typeof(DateOnly))
            {
                if (value is DateTime d) return DateOnly.FromDateTime(d);
                if (value is string s) return DateOnly.Parse(s, CultureInfo.InvariantCulture);
            }
            else if (type == typeof(TimeOnly))
            {
                if (value is TimeSpan t) return TimeOnly.FromTimeSpan(t);
                if (value is DateTime d) return TimeOnly.FromDateTime(d);
                if (value is string s) return TimeOnly.Parse(s, CultureInfo.InvariantCulture);
            }
#endif

            return Convert.ChangeType(value, type, CultureInfo.InvariantCulture)!;
        }

        /// <summary>
        /// Types that map from a single column rather than from a row of named columns.
        /// </summary>
        public static bool IsScalar(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            return type.IsPrimitive
                || type.IsEnum
                || type == typeof(string)
                || type == typeof(decimal)
                || type == typeof(DateTime)
                || type == typeof(DateTimeOffset)
                || type == typeof(TimeSpan)
                || type == typeof(Guid)
                || type == typeof(byte[])
#if NET6_0_OR_GREATER
                || type == typeof(DateOnly)
                || type == typeof(TimeOnly)
#endif
                ;
        }
    }
}
