using System;
using System.Collections.Generic;
using Riptide;
using UnityEngine;

namespace KaCMultiplayer.Net
{
    /// <summary>
    /// Wire encodings for the composite types this mod actually sends. Riptide covers the
    /// primitives; these are the game-shaped ones that would otherwise be spelled out by
    /// hand in every message that uses them.
    ///
    /// Extension methods rather than a static helper class so a codec reads as one
    /// continuous sequence of writes, <c>m.AddGuid(x).AddVector3(y)</c>, which makes a
    /// Serialize and its Deserialize easy to compare line by line. That comparison is the
    /// only thing standing between a codec and a desync, so it is worth optimising for.
    ///
    /// Every list here is length-prefixed with an int and its elements written inline. No
    /// per-element length prefixes, since every element type below is fixed-size.
    /// </summary>
    public static class NetWire
    {
        private const int GuidBytes = 16;

        // ---- Guid -------------------------------------------------------------
        //
        // Raw 16 bytes, no length prefix: the size is fixed and known on both ends, so a
        // prefix would be four wasted bytes on messages that can fire every tick.

        public static Message AddGuid(this Message m, Guid value)
        {
            return m.AddBytes(value.ToByteArray(), false);
        }

        public static Guid GetGuid(this Message m)
        {
            return new Guid(m.GetBytes(GuidBytes));
        }

        // ---- Vector3 ----------------------------------------------------------

        public static Message AddVector3(this Message m, Vector3 value)
        {
            m.AddFloat(value.x);
            m.AddFloat(value.y);
            m.AddFloat(value.z);
            return m;
        }

        public static Vector3 GetVector3(this Message m)
        {
            float x = m.GetFloat();
            float y = m.GetFloat();
            float z = m.GetFloat();
            return new Vector3(x, y, z);
        }

        // ---- Quaternion -------------------------------------------------------
        //
        // All four components, uncompressed. A rotation could be sent as three components
        // and the fourth reconstructed, but building rotations are sent once on placement
        // rather than streamed, so four floats is not worth the reconstruction risk.

        public static Message AddQuaternion(this Message m, Quaternion value)
        {
            m.AddFloat(value.x);
            m.AddFloat(value.y);
            m.AddFloat(value.z);
            m.AddFloat(value.w);
            return m;
        }

        public static Quaternion GetQuaternion(this Message m)
        {
            float x = m.GetFloat();
            float y = m.GetFloat();
            float z = m.GetFloat();
            float w = m.GetFloat();
            return new Quaternion(x, y, z, w);
        }

        // ---- lists ------------------------------------------------------------
        //
        // A null list is written as length 0 and read back as an empty list, never null.
        // Handlers then never have to null-check, and a round trip is stable, which
        // matters because the startup codec check compares re-serialized bytes.

        public static Message AddGuidList(this Message m, List<Guid> values)
        {
            int count = values == null ? 0 : values.Count;
            m.AddInt(count);
            for (int i = 0; i < count; i++) m.AddGuid(values[i]);
            return m;
        }

        public static List<Guid> GetGuidList(this Message m)
        {
            int count = m.GetInt();
            List<Guid> result = new List<Guid>(count < 0 ? 0 : count);
            for (int i = 0; i < count; i++) result.Add(m.GetGuid());
            return result;
        }

        public static Message AddVector3List(this Message m, List<Vector3> values)
        {
            int count = values == null ? 0 : values.Count;
            m.AddInt(count);
            for (int i = 0; i < count; i++) m.AddVector3(values[i]);
            return m;
        }

        public static List<Vector3> GetVector3List(this Message m)
        {
            int count = m.GetInt();
            List<Vector3> result = new List<Vector3>(count < 0 ? 0 : count);
            for (int i = 0; i < count; i++) result.Add(m.GetVector3());
            return result;
        }

        public static Message AddIntList(this Message m, List<int> values)
        {
            int count = values == null ? 0 : values.Count;
            m.AddInt(count);
            for (int i = 0; i < count; i++) m.AddInt(values[i]);
            return m;
        }

        public static List<int> GetIntList(this Message m)
        {
            int count = m.GetInt();
            List<int> result = new List<int>(count < 0 ? 0 : count);
            for (int i = 0; i < count; i++) result.Add(m.GetInt());
            return result;
        }

        public static Message AddFloatList(this Message m, List<float> values)
        {
            int count = values == null ? 0 : values.Count;
            m.AddInt(count);
            for (int i = 0; i < count; i++) m.AddFloat(values[i]);
            return m;
        }

        public static List<float> GetFloatList(this Message m)
        {
            int count = m.GetInt();
            List<float> result = new List<float>(count < 0 ? 0 : count);
            for (int i = 0; i < count; i++) result.Add(m.GetFloat());
            return result;
        }
    }
}
