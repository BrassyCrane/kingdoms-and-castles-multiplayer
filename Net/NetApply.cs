using System;

namespace KaCMultiplayer.Net
{
    /// <summary>
    /// Marks the window during which we are applying a change that came from the network,
    /// so the Harmony patches that normally broadcast a player's action know to stay quiet
    /// and not bounce it straight back out.
    ///
    /// An explicit scope, rather than inspecting the call stack for a dispatch method by name
    /// and depth. A stack check depends on two things that are not contracts: the method being
    /// named a particular way, and the game call sitting a fixed number of frames below it.
    /// Rename a method, add a wrapper, or let the JIT inline something, and the check silently
    /// starts answering the wrong question, with no error, just actions echoing
    /// around the session.
    ///
    /// Usage in a handler:
    /// <code>
    /// using (NetApply.Scope())
    ///     SpeedControlUI.inst.SetSpeed(speed);
    /// </code>
    ///
    /// Usage in a patch:
    /// <code>
    /// if (NetApply.InProgress) return;   // came from the network; don't rebroadcast
    /// </code>
    /// </summary>
    public static class NetApply
    {
        [ThreadStatic]
        private static int depth;

        /// <summary>True while a network-originated change is being applied.</summary>
        public static bool InProgress
        {
            get { return depth > 0; }
        }

        /// <summary>
        /// Opens an apply window; dispose to close it. Nests safely, a handler that
        /// triggers game code which itself applies another change stays marked throughout.
        /// </summary>
        public static IDisposable Scope()
        {
            depth++;
            return Closer.Instance;
        }

        /// <summary>
        /// Temporarily steps out of the apply window, so work done inside <i>is</i>
        /// broadcast even though we are handling an incoming message.
        ///
        /// There is one case that needs this: placing a player's starting keep. The handler
        /// is applying a remote request, so broadcasting is normally suppressed, but the
        /// resulting keep is a genuinely new building that every other machine has to be
        /// told about. Wrapping just that placement in a bypass sends it.
        ///
        /// Stated outright rather than signalled by calling through a marker method that a
        /// stack-frame check could recognise by name. Same effect, and it survives a rename.
        /// </summary>
        public static IDisposable Bypass()
        {
            int saved = depth;
            depth = 0;
            return new Restorer(saved);
        }

        /// <summary>
        /// Force-clears the counter. Only for the top of the receive loop, as a guard
        /// against a leak from an exception escaping a scope in some path we missed,
        /// without it one bad frame would leave the session permanently unable to
        /// broadcast.
        /// </summary>
        internal static void Reset()
        {
            if (depth != 0)
            {
                NetLog.Warn("apply depth was " + depth + " at dispatch; forcing to 0");
                depth = 0;
            }
        }

        // Single shared instance: Scope() is called per received message, and the state
        // is the counter rather than anything per-object, so there is nothing to allocate.
        private class Closer : IDisposable
        {
            public static readonly Closer Instance = new Closer();
            public void Dispose() { if (depth > 0) depth--; }
        }

        // Bypass has to remember the depth it displaced, so unlike Closer it cannot be
        // shared. It is rare enough that the allocation does not matter.
        private class Restorer : IDisposable
        {
            private readonly int saved;
            public Restorer(int saved) { this.saved = saved; }
            public void Dispose() { depth = saved; }
        }
    }
}
