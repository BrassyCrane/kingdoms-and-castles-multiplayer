// This file is provided under The MIT License as part of RiptideNetworking.
// Copyright (c) Tom Weiland
// For additional information please see the included LICENSE.md file or view it on GitHub:
// https://github.com/RiptideNetworking/Riptide/blob/main/LICENSE.md

using Riptide.Transports;
using Riptide.Utils;
using System;
using System.Collections.Generic;

namespace Riptide
{
    /// <summary>Represents a currently pending reliably sent message whose delivery has not been acknowledged yet.</summary>
    internal class PendingMessage
    {
        /// <summary>The time of the latest send attempt.</summary>
        internal long LastSendTime { get; private set; }

        /// <summary>
        /// Which send of which use of this instance is currently live. A <see cref="ResendEvent"/>
        /// carries the token it was queued under and does nothing unless it still matches.
        ///
        /// WHY A TOKEN AND NOT THE TIMESTAMP THIS USED TO COMPARE (player report, 0.15.2 and
        /// 0.15.3: the game crawling, then a kick, with the transport reporting a QUARTER OF A
        /// MILLION refused sends a second). The old guard was
        /// <c>initiatedAtTime == message.LastSendTime</c>, and LastSendTime is
        /// <see cref="Peer.CurrentTime"/>, which is read ONCE PER Update. Every message sent in the
        /// same frame therefore carries the identical timestamp. Instances are pooled, so a message
        /// that was acked and released is handed straight back out to a new one, and a resend event
        /// left over from the previous use compares equal to the new use's timestamp and passes.
        /// The instance then has two live retry chains, each of which schedules another event, and
        /// every frame doubles it. That is how an ordinary stream of reliable messages turned into
        /// a send storm that filled Steam's buffer, jammed the link, and starved the heartbeat
        /// until the player was dropped.
        ///
        /// A counter cannot collide, so a stale event can never resurrect a recycled instance.
        /// </summary>
        internal int RetryToken { get; private set; }

        /// <summary>The multiplier used to determine how long to wait before resending a pending message.</summary>
        private const float RetryTimeMultiplier = 1.2f;

        /// <summary>The longest wait between two sends of the same message, in milliseconds.</summary>
        internal const int MaxRetryDelayMs = 2000;

        /// <summary>
        /// How long to wait before sending an unanswered message again: about one round trip
        /// after the first send, then double that after every further attempt, up to
        /// <see cref="MaxRetryDelayMs"/>.
        ///
        /// WHY IT DOUBLES (player reports, 0.16.0 and 0.16.3: a guest dropped with "Timed out"
        /// after about an hour while both games ran smoothly). The wait used to be the same short
        /// one every time, about 28 ms on a good link. That is right for a lost packet and wrong
        /// for a late one. When a burst leaves packets waiting in Steam's outgoing queue, the
        /// answers come back late, so EVERY waiting message was sent again 35 times a second, each
        /// copy joining the same queue and making the answers later still. Steam carries a fixed
        /// 256 KB/s, so past a certain burst size the queue never emptied again: both sides sat at
        /// the full rate sending copies of old messages until the heartbeat could not get through.
        /// Doubling the wait means a late answer costs a few extra copies instead of hundreds, and
        /// a jam drains instead of feeding itself.
        /// </summary>
        internal static long RetryDelay(short smoothRtt, int attempts)
        {
            long first = smoothRtt < 0 ? 50 : (long)Math.Max(10, smoothRtt * RetryTimeMultiplier);
            long delay = first;
            for (int i = 1; i < attempts && delay < MaxRetryDelayMs; i++)
                delay *= 2;
            return Math.Max(first, Math.Min(delay, MaxRetryDelayMs));
        }

        /// <summary>A pool of reusable <see cref="PendingMessage"/> instances.</summary>
        private static readonly List<PendingMessage> pool = new List<PendingMessage>();

        /// <summary>The <see cref="Connection"/> to use to send (and resend) the pending message.</summary>
        private Connection connection;
        /// <summary>The contents of the message.</summary>
        private readonly byte[] data;
        /// <summary>The length in bytes of the message.</summary>
        private int size;
        /// <summary>How many send attempts have been made so far.</summary>
        private byte sendAttempts;
        /// <summary>Whether the pending message has been cleared or not.</summary>
        private bool wasCleared;

        /// <summary>Handles initial setup.</summary>
        internal PendingMessage()
        {
            data = new byte[Message.MaxSize];
        }

        #region Pooling
        /// <summary>Retrieves a <see cref="PendingMessage"/> instance and initializes it.</summary>
        /// <param name="sequenceId">The sequence ID of the message.</param>
        /// <param name="message">The message that is being sent reliably.</param>
        /// <param name="connection">The <see cref="Connection"/> to use to send (and resend) the pending message.</param>
        /// <returns>An intialized <see cref="PendingMessage"/> instance.</returns>
        internal static PendingMessage Create(ushort sequenceId, Message message, Connection connection)
        {
            PendingMessage pendingMessage = RetrieveFromPool();
            pendingMessage.connection = connection;

            message.SetBits(sequenceId, sizeof(ushort) * Converter.BitsPerByte, Message.HeaderBits);
            pendingMessage.size = message.BytesInUse;
            Buffer.BlockCopy(message.Data, 0, pendingMessage.data, 0, pendingMessage.size);

            pendingMessage.sendAttempts = 0;
            pendingMessage.wasCleared = false;
            pendingMessage.RetryToken++;   // anything queued against the previous use is now dead
            return pendingMessage;
        }

        /// <summary>Retrieves a <see cref="PendingMessage"/> instance from the pool. If none is available, a new instance is created.</summary>
        /// <returns>A <see cref="PendingMessage"/> instance.</returns>
        private static PendingMessage RetrieveFromPool()
        {
            PendingMessage message;
            if (pool.Count > 0)
            {
                message = pool[0];
                pool.RemoveAt(0);
            }
            else
                message = new PendingMessage();

            return message;
        }

        /// <summary>Empties the pool. Does not affect <see cref="PendingMessage"/> instances which are actively pending and therefore not in the pool.</summary>
        public static void ClearPool()
        {
            pool.Clear();
        }

        /// <summary>Returns the <see cref="PendingMessage"/> instance to the pool so it can be reused.</summary>
        private void Release()
        {
            if (!pool.Contains(this))
                pool.Add(this); // Only add it if it's not already in the list, otherwise this method being called twice in a row for whatever reason could cause *serious* issues

            // TODO: consider doing something to decrease pool capacity if there are far more
            //       available instance than are needed, which could occur if a large burst of
            //       messages has to be sent for some reason
        }
        #endregion

        /// <summary>Resends the message.</summary>
        internal void RetrySend()
        {
            if (!wasCleared)
            {
                long time = connection.Peer.CurrentTime;
                if (LastSendTime + (connection.SmoothRTT < 0 ? 25 : connection.SmoothRTT / 2) <= time) // Avoid triggering a resend if the latest resend was less than half a RTT ago
                    TrySend();
                else
                    // Requeued under the SAME token, not a fresh one. This is the "came round too
                    // soon" path, so the send it is waiting on has not happened yet and this is
                    // still the one live event for it.
                    connection.Peer.ExecuteLater(RetryDelay(connection.SmoothRTT, sendAttempts), new ResendEvent(this, RetryToken));
            }
        }

        /// <summary>Attempts to send the message.</summary>
        internal void TrySend()
        {
            if (sendAttempts >= connection.MaxSendAttempts && connection.CanQualityDisconnect)
            {
                RiptideLogger.Log(LogType.Info, connection.Peer.LogName, $"Could not guarantee delivery of a {(MessageHeader)(data[0] & Message.HeaderBitmask)} message after {sendAttempts} attempts! Disconnecting...");
                connection.Peer.Disconnect(connection, DisconnectReason.PoorConnection);
                return;
            }

            connection.Send(data, size);
            connection.Metrics.SentReliable(size);

            LastSendTime = connection.Peer.CurrentTime;
            sendAttempts++;
            RetryToken++;   // this send owns the chain from here; older events stop matching

            connection.Peer.ExecuteLater(RetryDelay(connection.SmoothRTT, sendAttempts), new ResendEvent(this, RetryToken));
        }

        /// <summary>Clears the message.</summary>
        internal void Clear()
        {
            connection.Metrics.RollingReliableSends.Add(sendAttempts);
            wasCleared = true;
            RetryToken++;   // acked: nothing already queued may send this again
            Release();
        }
    }
}
