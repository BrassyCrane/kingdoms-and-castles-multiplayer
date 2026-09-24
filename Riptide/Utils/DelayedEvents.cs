// This file is provided under The MIT License as part of RiptideNetworking.
// Copyright (c) Tom Weiland
// For additional information please see the included LICENSE.md file or view it on GitHub:
// https://github.com/RiptideNetworking/Riptide/blob/main/LICENSE.md

using Riptide.Transports;

namespace Riptide.Utils
{
    /// <summary>Executes an action when invoked.</summary>
    internal abstract class DelayedEvent
    {
        /// <summary>Executes the action.</summary>
        public abstract void Invoke();
    }

    /// <summary>Resends a <see cref="PendingMessage"/> when invoked.</summary>
    internal class ResendEvent : DelayedEvent
    {
        /// <summary>The message to resend.</summary>
        private readonly PendingMessage message;

        /// <summary>
        /// The message's retry token at the moment this event was queued. The event does nothing
        /// unless it still matches, which is what keeps exactly one retry chain alive per send.
        /// See <see cref="PendingMessage.RetryToken"/> for what went wrong when this was a
        /// timestamp.
        /// </summary>
        private readonly int token;

        /// <summary>Initializes the event.</summary>
        /// <param name="message">The message to resend.</param>
        /// <param name="token">The message's retry token when the event was queued.</param>
        public ResendEvent(PendingMessage message, int token)
        {
            this.message = message;
            this.token = token;
        }

        /// <inheritdoc/>
        public override void Invoke()
        {
            if (token == message.RetryToken) // If this isn't the case then the message has moved on without us
                message.RetrySend();
        }
    }

    /// <summary>Executes a heartbeat when invoked.</summary>
    internal class HeartbeatEvent : DelayedEvent
    {
        /// <summary>The peer whose heart to beat.</summary>
        private readonly Peer peer;

        /// <summary>Initializes the event.</summary>
        /// <param name="peer">The peer whose heart to beat.</param>
        public HeartbeatEvent(Peer peer)
        {
            this.peer = peer;
        }

        /// <inheritdoc/>
        public override void Invoke()
        {
            peer.Heartbeat();
        }
    }
}
