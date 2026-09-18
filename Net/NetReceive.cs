using Riptide;

namespace KaCMultiplayer.Net
{
    /// <summary>
    /// Adapts Riptide's MessageReceived event signature to <see cref="NetRouter.Receive"/>,
    /// which needs to know which side is handling the message.
    ///
    /// Riptide's event does not say which side received the message, and the router needs to
    /// know. That is the whole of this type's job.
    /// </summary>
    public static class NetReceive
    {
        public static void OnServer(object sender, MessageReceivedEventArgs e)
        {
            NetRouter.Receive(e, true);
        }

        public static void OnClient(object sender, MessageReceivedEventArgs e)
        {
            NetRouter.Receive(e, false);
        }
    }
}
