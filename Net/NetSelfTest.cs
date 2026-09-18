using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Riptide;
using UnityEngine;

namespace KaCMultiplayer.Net
{
    /// <summary>
    /// Startup check that every registered message can survive a round trip through the
    /// wire format.
    ///
    /// Hand-written codecs buy explicit field order at the cost of one specific failure:
    /// Serialize and Deserialize drifting apart. Add a field to one and forget the other,
    /// or reorder a pair, and nothing complains, the sender writes one shape and the
    /// receiver reads another. In a two-machine game that surfaces as garbled state a
    /// long way from the cause.
    ///
    /// The check works without a second player, and without asserting anything about a
    /// message's contents:
    ///
    ///   1. build an instance and fill it with arbitrary but deterministic values
    ///   2. serialize it to bytes
    ///   3. deserialize those bytes into a fresh instance
    ///   4. serialize the fresh instance again
    ///   5. the two byte sequences must be identical
    ///
    /// Any disagreement between the two methods shows up as different bytes at step 5.
    /// Reading fields in the wrong order, reading the wrong type, forgetting to read one
    ///, all caught. What it cannot catch is both methods being wrong the same way, such
    /// as a field neither of them touches; that stays a code-review problem.
    /// </summary>
    public static class NetSelfTest
    {
        /// <summary>Fixed seed so a failure reproduces exactly on the next run.</summary>
        private const int Seed = 0x5CA1AB1E;

        /// <summary>
        /// Runs the check over every registered message. Logs one line per failure and a
        /// single summary line. Returns true when everything passed.
        /// </summary>
        public static bool Run()
        {
            int passed = 0;
            List<string> failures = new List<string>();

            foreach (NetMessageId id in NetRegistry.AllIds)
            {
                string failure = Check(id);
                if (failure == null)
                    passed++;
                else
                    failures.Add(failure);
            }

            foreach (string f in failures)
                NetLog.Warn("codec check FAILED, " + f);

            if (failures.Count == 0)
                NetLog.Info("codec check passed for all " + passed + " message types");
            else
                NetLog.Warn("codec check: " + passed + " passed, " + failures.Count +
                            " FAILED, those message types will corrupt state in a real session");

            return failures.Count == 0;
        }

        /// <summary>Returns null on success, or a description of the failure.</summary>
        private static string Check(NetMessageId id)
        {
            Type type = NetRegistry.TypeOf(id);
            string label = id + " (" + (type == null ? "?" : type.Name) + ")";

            try
            {
                INetMessage original = NetRegistry.Create(id);
                Populate(original, new System.Random(Seed));

                byte[] first = NetCodec.Encode(original, id);

                INetMessage decoded = NetCodec.Decode(first, id);
                if (decoded == null)
                    return label + ": decoded to null";

                byte[] second = NetCodec.Encode(decoded, id);

                if (first.Length != second.Length)
                    return label + ": wrote " + first.Length +
                           " bytes, re-wrote " + second.Length +
                           " after a round trip, Serialize and Deserialize disagree on the field list";

                for (int i = 0; i < first.Length; i++)
                {
                    if (first[i] != second[i])
                        return label + ": byte " + i + " changed across a round trip (" +
                               first[i] + " -> " + second[i] +
                               "), likely a field order mismatch between Serialize and Deserialize";
                }

                return null;
            }
            catch (Exception ex)
            {
                return label + ": threw " + ex.GetType().Name + ", " + ex.Message;
            }
        }

        /// <summary>
        /// Fills public fields and settable properties with deterministic junk, so the
        /// comparison is meaningful. Default-constructed messages are all zeros and nulls,
        /// which round-trip fine even when the codec is wrong, two same-typed fields
        /// could be swapped and nothing would show.
        ///
        /// Reflection is fine here: this is a startup diagnostic, never the send path.
        /// Types it doesn't know are left at their default, which weakens the check for
        /// that field but never produces a false failure.
        /// </summary>
        private static void Populate(object target, System.Random rng, int depth = 0)
        {
            Type type = target.GetType();

            foreach (FieldInfo f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object v = SampleFor(f.FieldType, rng, depth);
                if (v != null) f.SetValue(target, v);
            }

            foreach (PropertyInfo p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanWrite || !p.CanRead) continue;
                if (p.GetIndexParameters().Length > 0) continue;

                object v = SampleFor(p.PropertyType, rng, depth);
                if (v != null) p.SetValue(target, v, null);
            }
        }

        private static object SampleFor(Type t, System.Random rng, int depth = 0)
        {
            if (t == typeof(ushort)) return (ushort)rng.Next(1, ushort.MaxValue);
            if (t == typeof(short)) return (short)rng.Next(1, short.MaxValue);
            if (t == typeof(int)) return rng.Next(1, int.MaxValue);
            if (t == typeof(uint)) return (uint)rng.Next(1, int.MaxValue);
            if (t == typeof(long)) return (long)rng.Next(1, int.MaxValue) * 7919L;
            if (t == typeof(ulong)) return (ulong)rng.Next(1, int.MaxValue) * 7919UL;
            if (t == typeof(byte)) return (byte)rng.Next(1, 256);
            if (t == typeof(sbyte)) return (sbyte)rng.Next(1, 128);
            if (t == typeof(bool)) return rng.Next(2) == 1;
            if (t == typeof(float)) return (float)(rng.NextDouble() * 1000.0);
            if (t == typeof(double)) return rng.NextDouble() * 1000.0;
            if (t == typeof(string)) return RandomString(rng);
            if (t == typeof(Vector3))
                return new Vector3((float)(rng.NextDouble() * 100.0),
                                   (float)(rng.NextDouble() * 100.0),
                                   (float)(rng.NextDouble() * 100.0));
            if (t == typeof(Quaternion))
                return new Quaternion((float)(rng.NextDouble() - 0.5),
                                      (float)(rng.NextDouble() - 0.5),
                                      (float)(rng.NextDouble() - 0.5),
                                      (float)(rng.NextDouble() - 0.5));
            if (t == typeof(Guid)) return Guid.NewGuid();
            if (t == typeof(byte[]))
            {
                byte[] bytes = new byte[rng.Next(1, 16)];
                for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)rng.Next(256);
                return bytes;
            }
            if (t.IsEnum)
            {
                Array values = Enum.GetValues(t);
                if (values.Length > 0) return values.GetValue(rng.Next(values.Length));
            }

            // Lists get a few real entries. Without this a message whose payload is a
            // list would round-trip empty and pass no matter what its element codec did.
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>) && depth < 3)
            {
                Type element = t.GetGenericArguments()[0];
                object list = Activator.CreateInstance(t);
                MethodInfo add = t.GetMethod("Add");

                int count = rng.Next(2, 5);
                for (int i = 0; i < count; i++)
                {
                    object item = SampleFor(element, rng, depth + 1);

                    // Element type we have no sample for, build one and fill its fields.
                    if (item == null && element.IsClass && element != typeof(string)
                        && element.GetConstructor(Type.EmptyTypes) != null)
                    {
                        item = Activator.CreateInstance(element);
                        Populate(item, rng, depth + 1);
                    }

                    if (item == null) return null;   // can't build entries; skip the field
                    add.Invoke(list, new object[] { item });
                }
                return list;
            }

            // A nested payload class of our own, build one and fill it recursively, so a
            // message that delegates its fields to a helper object still gets tested. Only
            // our own types: anything from the game or Unity is left alone, since
            // constructing those can have side effects.
            if (t.IsClass && depth < 3
                && t.Namespace != null && t.Namespace.StartsWith("KaCMultiplayer")
                && t.GetConstructor(Type.EmptyTypes) != null)
            {
                object nested = Activator.CreateInstance(t);
                Populate(nested, rng, depth + 1);
                return nested;
            }

            // Unknown type: leave it alone rather than guess.
            return null;
        }

        private static string RandomString(System.Random rng)
        {
            const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 ";
            int length = rng.Next(3, 24);
            StringBuilder sb = new StringBuilder(length);
            for (int i = 0; i < length; i++)
                sb.Append(alphabet[rng.Next(alphabet.Length)]);
            return sb.ToString();
        }
    }
}
