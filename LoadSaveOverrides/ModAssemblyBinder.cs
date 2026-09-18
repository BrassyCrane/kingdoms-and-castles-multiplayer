using System;
using System.Reflection;
using System.Runtime.Serialization;

using KaCMultiplayer.Net;

namespace KaCMultiplayer.SaveIo
{
    /// <summary>
    /// Resolves types while deserializing a save that came over the network.
    ///
    /// Needed because the mod is compiled into a fresh assembly on every load, with a name
    /// that differs between runs and between machines. A save serialized on the host records
    /// that assembly name, and the default binder would look for it by name and fail. This
    /// ignores the recorded assembly and looks the type up in whichever assembly is running
    /// now, which is the same code either way.
    ///
    /// It only matters for the shared-save transfer, a save loaded from disk was written by
    /// the same process that reads it.
    /// </summary>
    public sealed class ModAssemblyBinder : SerializationBinder
    {
        public override Type BindToType(string assemblyName, string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;

            string local = Assembly.GetExecutingAssembly().FullName;

            // First choice: the type as it exists in this build.
            Type resolved = Type.GetType(typeName + ", " + local);
            if (resolved != null) return resolved;

            // Second: game and framework types, which live in stable assemblies and are
            // recorded correctly. Both forms have to be tried, resolving only mod-defined types
            // leaves everything else null, and the deserializer then throws with no indication of
            // which type it failed on.
            resolved = Type.GetType(typeName + ", " + assemblyName);
            if (resolved != null) return resolved;

            resolved = Type.GetType(typeName);
            if (resolved != null) return resolved;

            NetLog.Warn("save deserialization: cannot resolve type '" + typeName +
                        "' (recorded assembly '" + assemblyName + "')");
            return null;
        }
    }
}
