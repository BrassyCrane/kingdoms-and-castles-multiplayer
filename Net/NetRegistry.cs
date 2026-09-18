using System;
using System.Collections.Generic;

namespace KaCMultiplayer.Net
{
    /// <summary>
    /// Delivery guarantee for a message type, declared once at registration rather than
    /// per send, so a type can't be reliable down one code path and unreliable down
    /// another.
    /// </summary>
    public enum NetDelivery
    {
        /// <summary>Arrives, eventually. The default, and correct for anything that
        /// changes world state, a dropped building placement is unrecoverable.</summary>
        Reliable = 0,

        /// <summary>May be dropped. Only for high-frequency state that the next update
        /// supersedes anyway, such as position corrections.</summary>
        Unreliable = 1,
    }

    /// <summary>
    /// Maps wire ids to the code that constructs and handles them.
    ///
    /// Registration is an explicit table written out in <c>NetRegistrations</c>, not an
    /// assembly scan. Scanning looks tidier, but it makes the set of live messages
    /// whatever happens to be compiled in, a type moved to another namespace, or an
    /// abstract base that stops being abstract, quietly changes what the wire accepts.
    /// A table you can read in one screen is worth the typing.
    /// </summary>
    public static class NetRegistry
    {
        /// <summary>Creates an empty instance ready to be filled by Deserialize.</summary>
        public delegate INetMessage Factory();

        /// <summary>Acts on a received message. Registered per side.</summary>
        public delegate void Handler(INetMessage message, NetContext context);

        private class Entry
        {
            public Type Type;
            public Factory Create;
            public NetDelivery Delivery;
            public Handler OnServer;
            public Handler OnClient;
        }

        private static readonly Dictionary<NetMessageId, Entry> entries =
            new Dictionary<NetMessageId, Entry>();

        public static bool IsSealed { get; private set; }

        /// <summary>
        /// Declares a message type and its codec. Call once per type during startup.
        /// </summary>
        public static void Register<T>(NetMessageId id,
                                       NetDelivery delivery = NetDelivery.Reliable)
            where T : INetMessage, new()
        {
            if (IsSealed)
                throw new InvalidOperationException(
                    "Cannot register " + typeof(T).Name + " after the registry is sealed.");

            if (id == NetMessageId.None)
                throw new ArgumentException("Message id None is reserved.", "id");

            Entry existing;
            if (entries.TryGetValue(id, out existing))
                throw new InvalidOperationException(
                    "Wire id " + (ushort)id + " (" + id + ") is claimed by both " +
                    existing.Type.Name + " and " + typeof(T).Name + ".");

            Entry entry = new Entry();
            entry.Type = typeof(T);
            entry.Create = delegate { return new T(); };
            entry.Delivery = delivery;
            entries.Add(id, entry);
        }

        /// <summary>
        /// Attaches the handler that runs when the authoritative host receives this
        /// message. Typically validates, applies, then rebroadcasts.
        /// </summary>
        public static void OnServer<T>(NetMessageId id, Action<T, NetContext> handler)
            where T : INetMessage
        {
            Bind<T>(id, handler, true);
        }

        /// <summary>
        /// Attaches the handler that runs when a client receives this message. Typically
        /// applies the host's decision to the local world.
        /// </summary>
        public static void OnClient<T>(NetMessageId id, Action<T, NetContext> handler)
            where T : INetMessage
        {
            Bind<T>(id, handler, false);
        }

        private static void Bind<T>(NetMessageId id, Action<T, NetContext> handler, bool server)
            where T : INetMessage
        {
            if (handler == null) throw new ArgumentNullException("handler");

            Entry entry = Require(id, "bind a handler to");

            if (entry.Type != typeof(T))
                throw new InvalidOperationException(
                    "Wire id " + id + " is registered as " + entry.Type.Name +
                    ", but a handler for " + typeof(T).Name + " was supplied.");

            // Cast once here so the per-message path stays a plain delegate call.
            Handler adapter = delegate (INetMessage m, NetContext ctx) { handler((T)m, ctx); };

            if (server)
            {
                if (entry.OnServer != null)
                    throw new InvalidOperationException(id + " already has a server handler.");
                entry.OnServer = adapter;
            }
            else
            {
                if (entry.OnClient != null)
                    throw new InvalidOperationException(id + " already has a client handler.");
                entry.OnClient = adapter;
            }
        }

        /// <summary>
        /// Closes registration. Call once startup is done so a late registration becomes
        /// a loud error rather than a message that silently works on one machine.
        /// </summary>
        public static void Seal()
        {
            IsSealed = true;
            NetLog.Info("registry sealed with " + entries.Count + " message types");
        }

        /// <summary>True when this id is known to us.</summary>
        public static bool IsKnown(NetMessageId id)
        {
            return entries.ContainsKey(id);
        }

        /// <summary>Every registered id. Used by the startup codec self-check.</summary>
        public static IEnumerable<NetMessageId> AllIds
        {
            get { return entries.Keys; }
        }

        /// <summary>CLR type registered against an id, for diagnostics.</summary>
        public static Type TypeOf(NetMessageId id)
        {
            Entry entry;
            return entries.TryGetValue(id, out entry) ? entry.Type : null;
        }

        internal static INetMessage Create(NetMessageId id)
        {
            return Require(id, "create").Create();
        }

        internal static NetDelivery DeliveryFor(NetMessageId id)
        {
            return Require(id, "send").Delivery;
        }

        /// <summary>
        /// Returns the handler for this id on the given side, or null if the message is
        /// known but not actionable there. A null result is legitimate: plenty of
        /// messages only mean something to one side.
        /// </summary>
        internal static Handler HandlerFor(NetMessageId id, bool server)
        {
            Entry entry;
            if (!entries.TryGetValue(id, out entry)) return null;
            return server ? entry.OnServer : entry.OnClient;
        }

        private static Entry Require(NetMessageId id, string verb)
        {
            Entry entry;
            if (!entries.TryGetValue(id, out entry))
                throw new KeyNotFoundException(
                    "Cannot " + verb + " unregistered wire id " + (ushort)id +
                    " (band: " + id.Band() + ").");
            return entry;
        }
    }
}
