using System;
using System.Collections;
using System.Reflection;

namespace KaCMultiplayer.Net
{
    /// <summary>
    /// Reaching into the game's private fields. Needed because several pieces of state the
    /// mod has to reset or restore have no public setter.
    ///
    /// Field lookups are cached. Resolving a field on every call is fine once and expensive in
    /// the per-tick patches some of these calls sit inside.
    /// </summary>
    public static class PrivateField
    {
        private const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;

        // Keyed by type and name. A miss is cached as null too, so a field that a game update
        // has renamed doesn't cost a failed reflection lookup every tick forever.
        private static readonly Hashtable cache = new Hashtable();

        private static FieldInfo Find(Type type, string name)
        {
            string key = type.FullName + "|" + name;

            if (cache.ContainsKey(key)) return (FieldInfo)cache[key];

            FieldInfo field = type.GetField(name, Instance);
            cache[key] = field;

            if (field == null)
                NetLog.Warn("no private field '" + name + "' on " + type.Name +
                            ", a game update may have renamed it");

            return field;
        }

        /// <summary>
        /// Empties a private list field. Returns false if the field is missing or isn't a
        /// list, having logged why.
        ///
        /// Non-generic on purpose. An element type here would only pick a <c>Clear</c> overload,
        /// and every list implements <see cref="IList"/>, which has one, so a type argument
        /// would cost five call sites something and change nothing.
        /// </summary>
        public static bool ClearList(object instance, string fieldName)
        {
            if (instance == null) return false;

            FieldInfo field = Find(instance.GetType(), fieldName);
            if (field == null) return false;

            IList list = field.GetValue(instance) as IList;
            if (list == null)
            {
                NetLog.Warn("field '" + fieldName + "' on " + instance.GetType().Name +
                            " is not a list, not cleared");
                return false;
            }

            list.Clear();
            return true;
        }

        /// <summary>Sets a private field. Returns false if it isn't there.</summary>
        public static bool Set(object instance, string fieldName, object value)
        {
            if (instance == null) return false;

            FieldInfo field = Find(instance.GetType(), fieldName);
            if (field == null) return false;

            field.SetValue(instance, value);
            return true;
        }

        /// <summary>
        /// Reads a private field, returning <paramref name="fallback"/> if it is missing or
        /// holds an unexpected type.
        /// </summary>
        public static T Get<T>(object instance, string fieldName, T fallback = default(T))
        {
            if (instance == null) return fallback;

            FieldInfo field = Find(instance.GetType(), fieldName);
            if (field == null) return fallback;

            object value = field.GetValue(instance);
            return value is T ? (T)value : fallback;
        }
    }
}
