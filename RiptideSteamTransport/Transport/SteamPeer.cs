// This file is provided under The MIT License as part of RiptideSteamTransport.
// Copyright (c) Tom Weiland
// For additional information please see the included LICENSE.md file or view it on GitHub:
// https://github.com/tom-weiland/RiptideSteamTransport/blob/main/LICENSE.md

using Steamworks;
using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Riptide.Transports.Steam
{
    public abstract class SteamPeer
    {
        /// <summary>The name to use when logging messages via <see cref="Utils.RiptideLogger"/>.</summary>
        public const string LogName = "STEAM";

        protected const int MaxMessages = 256;

        private readonly byte[] receiveBuffer;

        protected SteamPeer()
        {
            receiveBuffer = new byte[Message.MaxSize + sizeof(ushort)];
        }

        protected void Receive(SteamConnection fromConnection)
        {
            IntPtr[] ptrs = new IntPtr[MaxMessages]; // TODO: remove allocation?

            // TODO: consider using poll groups -> https://partner.steamgames.com/doc/api/ISteamNetworkingSockets#functions_poll_groups
            int messageCount = SteamNetworkingSockets.ReceiveMessagesOnConnection(fromConnection.SteamNetConnection, ptrs, MaxMessages);
            if (messageCount > 0)
            {
                for (int i = 0; i < messageCount; i++)
                {
                    SteamNetworkingMessage_t data = Marshal.PtrToStructure<SteamNetworkingMessage_t>(ptrs[i]);

                    if (data.m_cbSize > 0)
                    {
                        int byteCount = data.m_cbSize;
                        if (data.m_cbSize > receiveBuffer.Length)
                        {
                            Debug.LogWarning($"{LogName}: Can't fully handle {data.m_cbSize} bytes because it exceeds the maximum of {receiveBuffer.Length}. Data will be incomplete!");
                            byteCount = receiveBuffer.Length;
                        }

                        Marshal.Copy(data.m_pData, receiveBuffer, 0, data.m_cbSize);
                        OnDataReceived(receiveBuffer, byteCount, fromConnection);
                    }
                }
            }
        }

        /// <summary>
        /// How long to stay quiet between refused-send reports. A refusal is not a one-off: when
        /// Steam's outgoing buffer fills, EVERY send is refused until it drains, which is thousands
        /// a second.
        /// </summary>
        private const int RefusalReportMs = 5000;

        /// <summary>Refusals since the last report, and when that report went out.</summary>
        private static int refusedSinceReport;
        private static int lastRefusalReport;

        /// <summary>
        /// Hands one packet to Steam.
        ///
        /// THE 26 MEGABYTE LOG (player report, 0.15.2). Steam refuses a send with LimitExceeded
        /// when its outgoing buffer for that connection is already full, and it stays full until
        /// the link drains. This method used to write a warning line for every single refusal, so
        /// one congested connection produced hundreds of thousands of identical lines, and writing
        /// them is itself slow enough to stall the frame, starve the heartbeat and get the player
        /// dropped for a timeout. The report is now one line at most every few seconds, carrying
        /// the count that was swallowed and the message id, which is what says WHICH message is
        /// filling the buffer.
        /// </summary>
        internal void Send(byte[] dataBuffer, int numBytes, HSteamNetConnection toConnection)
        {
            GCHandle handle = GCHandle.Alloc(dataBuffer, GCHandleType.Pinned);
            IntPtr pDataBuffer = handle.AddrOfPinnedObject();

            EResult result = SteamNetworkingSockets.SendMessageToConnection(toConnection, pDataBuffer, (uint)numBytes, Constants.k_nSteamNetworkingSend_Unreliable, out long _);

            handle.Free();

            if (result == EResult.k_EResultOK)
                return;

            refusedSinceReport++;

            // Environment.TickCount rather than UnityEngine.Time: no frame is guaranteed to have
            // happened between two sends, and this must stay safe to call off the main thread.
            int now = Environment.TickCount;
            if (unchecked(now - lastRefusalReport) < RefusalReportMs)
                return;

            lastRefusalReport = now;
            int swallowed = refusedSinceReport;
            refusedSinceReport = 0;

            Debug.LogWarning($"{LogName}: Steam refused {swallowed} send(s) in the last "
                             + $"{RefusalReportMs / 1000} seconds - {result}; latest was {numBytes} bytes, "
                             + $"{DescribeOutgoing(dataBuffer, numBytes)}");
        }

        /// <summary>
        /// Names the message in a buffer Steam refused, so a report says what is flooding the
        /// connection rather than only how big it was. Reads nothing but the header and the id, and
        /// never throws: this only ever runs on a path that is already going wrong.
        /// </summary>
        private static string DescribeOutgoing(byte[] dataBuffer, int numBytes)
        {
            try
            {
                if (dataBuffer == null || numBytes < 1) return "empty";

                MessageHeader header;
                Message m = Message.Create().Init(dataBuffer[0], numBytes, out header);
                try
                {
                    // Anything other than an unreliable message is Riptide's own traffic
                    // (heartbeat, ack, connect), which carries no id of ours.
                    if (header != MessageHeader.Unreliable)
                        return "riptide " + header;

                    if (numBytes > 1)
                        Buffer.BlockCopy(dataBuffer, 1, m.Data, 1, numBytes - 1);

                    return "message id " + m.GetVarULong();
                }
                finally { m.Release(); }
            }
            catch { return "unreadable"; }
        }

        protected abstract void OnDataReceived(byte[] dataBuffer, int amount, SteamConnection fromConnection);
    }
}
