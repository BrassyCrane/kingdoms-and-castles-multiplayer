using KaCMultiplayer.Net;

namespace KaCMultiplayer.Lobby
{
    /// <summary>
    /// Entry point for loading the UI bundle.
    ///
    /// The mod loader finds <c>PreScriptLoad</c> by reflection and calls it before any script
    /// runs, which is the earliest hook available and the only place the bundle can be ready
    /// before a screen wants a prefab out of it. This class exists solely to carry that
    /// method name, the work is in <see cref="LobbyPrefabs"/>.
    ///
    /// <see cref="LobbyPrefabs.Load"/> is idempotent, and Main.Preload calls it too, so if the
    /// loader ever stops finding this hook the bundle still loads, just a little later.
    /// </summary>
    public class PrefabLoader
    {
        public void PreScriptLoad(KCModHelper helper)
        {
            NetLog.Sink = helper.Log;   // earliest point we can log anything
            LobbyPrefabs.Load(helper);
        }
    }
}
