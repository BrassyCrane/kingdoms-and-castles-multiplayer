using KaCMultiplayer.Lobby;
using KaCMultiplayer.Net;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace KaCMultiplayer
{
    public class ServerRow : MonoBehaviour
    {
        // One row in the server browser. Every field below is filled from Steam lobby data by
        // BrowserScreen.LobbyHeartbeat, there is no longer a web backend behind any of it, so
        // Fields not carried here (Heartbeat, IPAddress, Port, Id,
        // CreatedAt, UpdatedAt, Permissions, DatabaseId, CollectionId) were never populated
        // again after that code was deleted, and are gone.

        /// <summary>Steam lobby to join. This, not an address, is how joining works.</summary>
        public ulong LobbyId { get; set; }

        public string Name { get; set; }
        public string Host { get; set; }
        public string PlayerId { get; set; }

        public int PlayerCount { get; set; }
        public int MaxPlayers { get; set; }
        public string Difficulty { get; set; }
        public bool Locked { get; set; }

        /// <summary>Base64 32x32 map picture published by the host. See MapThumbnail.</summary>
        public string MapThumb { get; set; }

        // Owned by this entry, so it has to be destroyed with it - see OnDestroy. Rows are
        // rebuilt on every browser refresh, and a leaked Texture2D per row per refresh adds up.
        private Texture2D mapTexture;


        public void Start()
        {
            // The prefab has an unused server-cover image that renders as a glaring white box.
            // Tint any default-white, sprite-less Image/RawImage in this entry to the dark panel
            // colour so it blends in instead of standing out.
            try
            {
                Color panel = new Color(0.10f, 0.14f, 0.20f, 1f);
                foreach (var img in GetComponentsInChildren<Image>(true))
                    if (img.sprite == null && img.color == Color.white)
                        img.color = panel;
                foreach (var ri in GetComponentsInChildren<RawImage>(true))
                    if (ri.texture == null && ri.color == Color.white)
                        ri.color = panel;
            }
            catch (Exception e) { Main.helper.Log("Server entry cover tint error: " + e.Message); }

            transform.Find("ServerName").GetComponent<TextMeshProUGUI>().text = Name;
            transform.Find("ServerHost").GetComponent<TextMeshProUGUI>().text = Host;
            transform.Find("ServerDifficulty").GetComponent<TextMeshProUGUI>().text = Difficulty;

            transform.Find("ServerLocked").gameObject.SetActive(Locked);
            transform.Find("ServerPlayers").GetComponent<TextMeshProUGUI>().text = $"{PlayerCount}/{MaxPlayers}";

            ShowMapThumbnail();

            transform.Find("Join").GetComponent<Button>().onClick.AddListener(() =>
            {
                try
                {
                    // Defensive: clear any leftover session so joining works even after a
                    // previous disconnect, without needing to restart the game.
                    SteamLobby.ResetNetworkState();

                    // Join through the Steam lobby (same path as accepting an invite);
                    // OnLobbyEnter then connects us to the host.
                    Main.helper.Log($"joining lobby {LobbyId} (host {PlayerId})");
                    SteamLobby.Active.JoinLobby(LobbyId);

                    // The lobby screen needs this row's details before the connection lands,
                    // so it has something to show while the handshake is in flight.
                    var lobbyScript = BrowserScreen.serverLobbyRef.GetComponent<LobbyScreen>();
                    if (lobbyScript == null)
                        lobbyScript = BrowserScreen.serverLobbyRef.AddComponent<LobbyScreen>();

                    lobbyScript.SetDetails(this);

                    ModalDialog.Show("Connecting to server", "Please wait while we connect to the server", "", false);
                }
                catch (Exception ex)
                {
                    Main.LogEx("ServerRow.Start (join server)", ex);
                }
            });
        }

        /// <summary>
        /// Decodes the host's published map picture into the row's ServerMap box.
        ///
        /// The RawImage ships disabled in the prefab so an empty slot reads as a deliberate dark
        /// inset rather than a broken image. It is only enabled once a thumbnail actually
        /// decodes, a host running an older build publishes nothing, and that has to look
        /// intentional rather than like a bug.
        /// </summary>
        private void ShowMapThumbnail()
        {
            try
            {
                Transform node = transform.Find("ServerMap");
                if (node == null) return;   // bundle predates the thumbnail box

                RawImage raw = node.GetComponent<RawImage>();
                if (raw == null) return;

                Texture2D tex = MapThumbnail.Decode(MapThumb);
                if (tex == null)
                {
                    raw.enabled = false;
                    return;
                }

                if (mapTexture != null) Destroy(mapTexture);
                mapTexture = tex;

                raw.texture = tex;
                raw.color = Color.white;   // the Start() cover tint darkens white RawImages
                raw.enabled = true;
            }
            catch (Exception e)
            {
                Main.helper.Log("Server map thumbnail error: " + e.Message);
            }
        }

        private void OnDestroy()
        {
            if (mapTexture != null)
            {
                Destroy(mapTexture);
                mapTexture = null;
            }
        }
    }
}
