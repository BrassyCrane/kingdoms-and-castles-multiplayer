using System;
using Riptide;

namespace KaCMultiplayer.Net
{
    /// <summary>
    /// <see cref="INetTransport"/> over a Riptide <see cref="Server"/>/<see cref="Client"/>
    /// pair. This is the only file above the Riptide vendor drop that knows those types
    /// exist.
    ///
    /// Both may be live at once: hosting a game still runs a local client, which is how
    /// the host's own actions travel the same path as everyone else's instead of needing
    /// a parallel "apply locally" branch at every call site.
    ///
    /// The peers are resolved through delegates rather than captured once, because
    /// hosting a second game replaces the Server instance outright. Holding a direct
    /// reference would leave this pointing at a stopped server, and every send after the
    /// first host would quietly go nowhere.
    /// </summary>
    public class RiptideTransport : INetTransport
    {
        private readonly Func<Server> serverSource;
        private readonly Func<Client> clientSource;

        public RiptideTransport(Func<Server> serverSource, Func<Client> clientSource)
        {
            if (serverSource == null) throw new ArgumentNullException("serverSource");
            if (clientSource == null) throw new ArgumentNullException("clientSource");

            this.serverSource = serverSource;
            this.clientSource = clientSource;
        }

        private Server Server { get { return serverSource(); } }
        private Client Client { get { return clientSource(); } }

        public bool IsServer
        {
            get { Server s = Server; return s != null && s.IsRunning; }
        }

        public bool IsClientConnected
        {
            get { Client c = Client; return c != null && c.IsConnected; }
        }

        public ushort LocalClientId
        {
            get { Client c = Client; return c != null && c.IsConnected ? c.Id : (ushort)0; }
        }

        public int ConnectedClientCount
        {
            get { Server s = Server; return s != null && s.IsRunning ? s.ClientCount : 0; }
        }

        public void SendToServer(Message message)
        {
            Client c = Client;
            if (c == null || !c.IsConnected) return;
            c.Send(message);
        }

        public void SendTo(Message message, ushort clientId)
        {
            Server s = Server;
            if (s == null || !s.IsRunning) return;
            s.Send(message, clientId);
        }

        public void Broadcast(Message message, ushort exceptClientId)
        {
            Server s = Server;
            if (s == null || !s.IsRunning) return;

            // Riptide's two-arg overload excludes a client; the one-arg form sends to
            // everyone. Client ids start at 1, so 0 reliably means "exclude nobody".
            if (exceptClientId == 0)
                s.SendToAll(message);
            else
                s.SendToAll(message, exceptClientId);
        }
    }
}
