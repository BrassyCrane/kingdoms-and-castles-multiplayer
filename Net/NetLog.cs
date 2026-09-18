using System;
using System.Text;

namespace KaCMultiplayer.Net
{
    /// <summary>
    /// Single place where network errors get written. The mod helper's Log() is the only
    /// sink the game gives us, so everything funnels through here rather than each call
    /// site hand-rolling its own exception dump.
    /// </summary>
    public static class NetLog
    {
        /// <summary>Set once during startup so this class has something to write to.</summary>
        public static Action<string> Sink;

        private const string Prefix = "[net] ";

        public static void Info(string message)
        {
            if (Sink != null) Sink(Prefix + message);
        }

        public static void Warn(string message)
        {
            if (Sink != null) Sink(Prefix + "WARN " + message);
        }

        /// <summary>
        /// Logs an exception plus every inner exception as one block. Nesting is flattened
        /// with an arrow prefix so a chain stays readable in the game's flat log file.
        /// </summary>
        public static void Error(string context, Exception ex)
        {
            if (Sink == null) return;

            StringBuilder sb = new StringBuilder();
            sb.Append(Prefix).Append("ERROR ").AppendLine(context);

            int depth = 0;
            for (Exception e = ex; e != null; e = e.InnerException, depth++)
            {
                string indent = depth == 0 ? "  " : "  " + new string(' ', depth * 2) + "-> ";
                sb.Append(indent).Append(e.GetType().Name).Append(": ").AppendLine(e.Message);
                if (!string.IsNullOrEmpty(e.StackTrace))
                    sb.AppendLine(e.StackTrace);

                if (depth > 8) { sb.AppendLine("  (inner exception chain truncated)"); break; }
            }

            Sink(sb.ToString());
        }
    }
}
