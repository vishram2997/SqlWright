using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;

namespace SqlWright.Internal
{
    /// <summary>One column-to-member assignment inside a compiled mapper, kept so failures can be explained.</summary>
    internal sealed class MappingStep
    {
        public MappingStep(int ordinal, string column, string target, Type targetType)
        {
            Ordinal = ordinal;
            Column = column;
            Target = target;
            TargetType = targetType;
        }

        public int Ordinal { get; }
        public string Column { get; }
        public string Target { get; }
        public Type TargetType { get; }
    }

    internal static class Diagnostics
    {
        /// <summary>
        /// Called after a compiled mapper throws. Replays each conversion to find the column that failed.
        /// Only runs on the failure path, so it costs nothing when mapping succeeds.
        /// </summary>
        public static Exception ExplainMappingFailure(Type type, IReadOnlyList<MappingStep> steps, IDataReader reader, Exception error)
        {
            foreach (var step in steps)
            {
                object? raw;
                try
                {
                    raw = reader.GetValue(step.Ordinal);
                }
                catch
                {
                    continue;
                }
                if (raw is null || raw is DBNull) continue;

                try
                {
                    ValueConverter.ChangeType(raw, step.TargetType);
                }
                catch (Exception conversionError)
                {
                    return new MappingException(
                        $"Error mapping column '{step.Column}' (ordinal {step.Ordinal}) to {step.Target} ({TypeName(step.TargetType)}): " +
                        $"cannot convert {Preview(raw)} of type {TypeName(raw.GetType())}. {conversionError.Message}",
                        type, step.Column, step.Ordinal, step.Target, conversionError);
                }
            }

            return new MappingException($"Error mapping a row to {TypeName(type)}: {error.Message}", type, innerException: error);
        }

        /// <summary>
        /// Finds the most likely intended name: first a match ignoring underscores and case, then a near spelling.
        /// </summary>
        public static string? Suggest(string name, IEnumerable<string> candidates, out bool underscoreOnly)
        {
            underscoreOnly = false;
            var list = candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var squashed = Squash(name);
            var byUnderscore = list.FirstOrDefault(c => Squash(c) == squashed);
            if (byUnderscore != null)
            {
                underscoreOnly = true;
                return byUnderscore;
            }

            var maxDistance = Math.Max(1, name.Length / 4);
            return list
                .Select(c => (Name: c, Distance: Distance(squashed, Squash(c))))
                .Where(x => x.Distance <= maxDistance)
                .OrderBy(x => x.Distance)
                .Select(x => x.Name)
                .FirstOrDefault();
        }

        /// <summary>A " Did you mean ...?" sentence for a column that matched no member, or empty.</summary>
        public static string ColumnHint(string column, IEnumerable<string> memberNames)
        {
            var suggestion = Suggest(column, memberNames, out var underscoreOnly);
            if (suggestion == null) return "";
            return underscoreOnly
                ? $" Did you mean '{suggestion}'? Set SqlWrightSettings.MatchNamesWithUnderscores = true, or add [Column(\"{column}\")] to it."
                : $" Did you mean '{suggestion}'? Alias the column in SQL, or add [Column(\"{column}\")] to the member.";
        }

        public static string TypeName(Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null) return TypeName(underlying) + "?";
            if (!type.IsGenericType) return type.Name;
            var name = type.Name.Substring(0, type.Name.IndexOf('`'));
            return name + "<" + string.Join(", ", type.GetGenericArguments().Select(TypeName)) + ">";
        }

        private static string Preview(object value)
        {
            var text = value is byte[] bytes ? $"byte[{bytes.Length}]" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            if (text.Length > 50) text = text.Substring(0, 47) + "...";
            return value is string ? $"'{text}'" : text;
        }

        private static string Squash(string name) => name.Replace("_", "").ToLowerInvariant();

        private static int Distance(string a, string b)
        {
            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];
            for (var j = 0; j <= b.Length; j++) previous[j] = j;

            for (var i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                }
                (previous, current) = (current, previous);
            }
            return previous[b.Length];
        }
    }
}
