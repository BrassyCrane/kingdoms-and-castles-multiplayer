using System;
using Riptide;
using Riptide.Transports;   // MessageHeader lives here, not in Riptide itself

namespace KaCMultiplayer.Net
{
    /// <summary>
    /// Turns messages into bytes and back, outside of any live connection.
    ///
    /// The send path does not need this, it hands a <see cref="Message"/> straight to
    /// Riptide. This exists for the two places that need a full round trip in-process:
    /// the startup codec check (<see cref="NetSelfTest"/>) and the solo loopback
    /// (<see cref="NetLoopback"/>). Both must go through the real format rather than
    /// passing object references around, or they would prove nothing about what actually
    /// travels between machines.
    ///
    /// Unreliable mode throughout: its header is the simplest and its receive path is a
    /// plain byte copy with no sequencing state to reconstruct. The payload encoding is
    /// identical either way, and the payload is what is under test.
    /// </summary>
    public static class NetCodec
    {
        /// <summary>Serializes a message to the bytes that would go on the wire.</summary>
        public static byte[] Encode(INetMessage message, NetMessageId id)
        {
            Message m = Message.Create(MessageSendMode.Unreliable, (ushort)id);
            try
            {
                message.Serialize(m);

                int amount = m.BytesInUse;
                byte[] buffer = new byte[amount];
                Buffer.BlockCopy(m.Data, 0, buffer, 0, amount);
                return buffer;
            }
            finally
            {
                m.Release();
            }
        }

        /// <summary>
        /// Rebuilds a message from bytes produced by <see cref="Encode"/>. Mirrors
        /// Peer.HandleData for the unreliable case: seed the header from byte zero, then
        /// copy the remainder in behind it.
        /// </summary>
        public static INetMessage Decode(byte[] buffer, NetMessageId expectedId)
        {
            MessageHeader header;
            Message m = Message.Create().Init(buffer[0], buffer.Length, out header);
            try
            {
                if (buffer.Length > 1)
                    Buffer.BlockCopy(buffer, 1, m.Data, 1, buffer.Length - 1);

                ushort readId = (ushort)m.GetVarULong();
                if (readId != (ushort)expectedId)
                    throw new InvalidOperationException(
                        "id round-tripped as " + readId + ", expected " + (ushort)expectedId);

                INetMessage fresh = NetRegistry.Create(expectedId);
                fresh.Deserialize(m);
                return fresh;
            }
            finally
            {
                m.Release();
            }
        }
    }
}
