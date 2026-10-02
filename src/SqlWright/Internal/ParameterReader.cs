using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace SqlWright.Internal
{
    /// <summary>
    /// Turns a parameter object (anonymous type, POCO or dictionary) into parameter specs,
    /// using compiled, cached property getters.
    /// </summary>
    internal static class ParameterReader
    {
        private sealed class Accessor
        {
            public Accessor(string name, Type type, Func<object, object?> getter)
            {
                Name = name;
                Type = type;
                Getter = getter;
            }

            public string Name { get; }
            public Type Type { get; }
            public Func<object, object?> Getter { get; }
        }

        private static readonly ConcurrentDictionary<Type, Accessor[]> Cache = new ConcurrentDictionary<Type, Accessor[]>();

        public static void ClearCache() => Cache.Clear();

        public static IEnumerable<ParameterSpec> Read(object? param)
        {
            switch (param)
            {
                case null:
                    yield break;

                case IEnumerable<KeyValuePair<string, object?>> pairs:
                    foreach (var pair in pairs)
                        yield return new ParameterSpec(ParameterSpec.Clean(pair.Key), pair.Value, pair.Value?.GetType());
                    yield break;

                case IDictionary dictionary:
                    foreach (DictionaryEntry entry in dictionary)
                        yield return new ParameterSpec(ParameterSpec.Clean(entry.Key.ToString()!), entry.Value, entry.Value?.GetType());
                    yield break;

                default:
                    foreach (var accessor in Cache.GetOrAdd(param.GetType(), Discover))
                        yield return new ParameterSpec(accessor.Name, accessor.Getter(param), accessor.Type);
                    yield break;
            }
        }

        private static Accessor[] Discover(Type type)
        {
            return TypeMembers.Get(type)
                .Where(m => m.CanRead)
                .Select(m =>
                {
                    var obj = Expression.Parameter(typeof(object), "obj");
                    var access = Expression.MakeMemberAccess(Expression.Convert(obj, type), m.Member);
                    var getter = Expression.Lambda<Func<object, object?>>(Expression.Convert(access, typeof(object)), obj).Compile();
                    return new Accessor(m.Member.Name, m.Type, getter);
                })
                .ToArray();
        }

        /// <summary>
        /// True when the parameter is a sequence of parameter objects, meaning "execute once per item".
        /// </summary>
        public static bool IsMultiExec(object? param) =>
            param is IEnumerable
            && !(param is string)
            && !(param is IDictionary)
            && !(param is IEnumerable<KeyValuePair<string, object?>>);
    }
}
