using System;
using System.Collections.Generic;
using System.Linq;

namespace KaCMultiplayer.LoadSaveOverrides
{
    // FRAMEWORK for loading multiplayer saves: steamId-based player identity.
    //
    // In a FRESH game a player's teamId is derived from their Riptide clientId (clientId + 4), which
    // works because identity doesn't matter yet, any distinct id will do. In a LOADED game it breaks:
    // clientId depends on JOIN ORDER, but each returning player must get the teamId their saved kingdom
    // was built with (buildings/landmass ownership carry it). Assigning by formula gave the classic
    // orphan-phantom: a player whose derived teamId didn't match their saved kingdom owned nothing.
    //
    // This registry maps steamId -> saved teamId. It is filled from the save file at the START of
    // SessionSave.Unpack (before any player data is applied), and consulted everywhere a
    // teamId is assigned (SessionPlayer ctor, ServerHandshake). Fresh games leave it empty → formula applies,
    // nothing changes. Every machine unpacks the same save, so every machine builds the same registry,
    // no extra sync needed.
    public static class LoadIdentity
    {
        // steamId -> the teamId that player's kingdom was saved with.
        private static readonly Dictionary<string, int> savedTeamBySteamId = new Dictionary<string, int>();

        // steamId -> the teamId assigned during THIS session, for players with nothing in the save.
        //
        // Without this, a fresh game's teamId is `clientId + 4` and nothing else, and Riptide hands
        // out the lowest FREE client id, so it recycles one as soon as a player leaves. Player 2 of 3
        // disconnects, somebody joins, they are handed client id 2, and the formula puts them on the
        // departed player's team, on top of the very kingdom the disconnect handler goes out of its
        // way to preserve. Pinning by steamId means a player's team is decided once and survives any
        // later shuffling of client ids, which is the guarantee loaded games already had.
        private static readonly Dictionary<string, int> sessionTeamBySteamId = new Dictionary<string, int>();

        // True when the current session came from a loaded save (registry filled during Unpack).
        public static bool IsLoadedSession { get { return savedTeamBySteamId.Count > 0; } }

        // Called during save Unpack, once per saved player, BEFORE any player data is applied.
        public static void RegisterSavedPlayer(string steamId, int teamId)
        {
            if (string.IsNullOrEmpty(steamId)) return;
            savedTeamBySteamId[steamId] = teamId;
            Main.helper.Log($"[LOADID] registered saved player {steamId} -> teamId {teamId}");
        }

        // The authoritative teamId for a player, in priority order:
        //
        //   1. the team their SAVED kingdom carries, buildings and landmass ownership hold it
        //   2. the team already assigned to them THIS session, so a recycled client id cannot
        //      move them, and a reconnecting player returns to their own kingdom
        //   3. the clientId formula, stepped past anything already taken
        //
        // Deterministic across machines: every machine sees the same joins in the same order, and
        // once a team is pinned to a steamId it is published in the roster (see AdoptAssignedTeam),
        // so a machine that arrives later does not have to re-derive it.
        public static int TeamIdFor(string steamId, ushort clientId)
        {
            int saved;
            if (steamId != null && savedTeamBySteamId.TryGetValue(steamId, out saved))
                return saved;

            int pinned;
            if (steamId != null && sessionTeamBySteamId.TryGetValue(steamId, out pinned))
                return pinned;

            int team = clientId + 4; // fresh-game formula (see SessionPlayer for the +4 rationale)
            while (IsTaken(team)) team++;

            if (!string.IsNullOrEmpty(steamId))
            {
                sessionTeamBySteamId[steamId] = team;
                Main.helper.Log($"[LOADID] pinned {steamId} -> teamId {team} for this session");
            }
            return team;
        }

        // A team nobody else may be given: either a saved kingdom's, or one already handed out.
        private static bool IsTaken(int team)
        {
            return savedTeamBySteamId.Values.Contains(team)
                || sessionTeamBySteamId.Values.Contains(team);
        }

        /// <summary>
        /// Accepts the host's assignment for a player, so every machine agrees on who owns which
        /// team without each having to re-derive it from a client id that may have been recycled.
        ///
        /// THE HOST DECIDES, and this used to refuse to believe it. A client that reconnects is
        /// given a NEW Riptide client id, and its own registry was cleared on the way out, so it
        /// re-derives clientId + 4 and gets a different number from the one the host has been
        /// holding for that steamId since their first connection. The host is right: remembering a
        /// team across reconnects is exactly what this registry is for. The client was then told
        /// so, wrote "keeping ours" in the log, and carried on disagreeing for the rest of the
        /// session.
        ///
        /// What that costs: materials, landmass ownership, dock policy and combat arbitration are
        /// all looked up by team. A player who is team 9 to themselves and team 6 to everybody else
        /// is two different kingdoms as far as those lookups are concerned.
        ///
        /// The old caution was not wrong, only too broad. Re-pinning a team that a kingdom has
        /// ALREADY been built on would strand that kingdom, so that case still refuses and still
        /// says so. Before anything is built - which is where this actually happens, in the lobby,
        /// during a join - there is nothing to strand and the host's answer simply wins.
        /// </summary>
        public static void AdoptAssignedTeam(string steamId, int teamId)
        {
            if (string.IsNullOrEmpty(steamId) || teamId <= 0) return;
            if (savedTeamBySteamId.ContainsKey(steamId)) return;   // the save wins

            int existing;
            if (sessionTeamBySteamId.TryGetValue(steamId, out existing))
            {
                if (existing == teamId) return;   // already agreed

                // We ARE the host: ours is the authoritative answer, and this call is our own
                // roster coming back to us.
                if (NetHost.IsRunning)
                {
                    Main.helper.Log($"[LOADID] roster says {steamId} is team {teamId} but we are the host "
                                    + $"and have {existing}; ours stands");
                    return;
                }

                if (KingdomExistsOn(existing))
                {
                    Main.helper.Log($"[LOADID] host says {steamId} is team {teamId} but a kingdom is already "
                                    + $"built on team {existing} here; keeping ours rather than stranding it");
                    return;
                }

                sessionTeamBySteamId[steamId] = teamId;
                RetagPlayerObject(steamId, existing, teamId);

                Main.helper.Log($"[LOADID] host says {steamId} is team {teamId}, we had {existing}; "
                                + "adopting theirs, nothing is built on it yet");
                return;
            }

            sessionTeamBySteamId[steamId] = teamId;
            Main.helper.Log($"[LOADID] adopted host assignment {steamId} -> teamId {teamId}");
        }

        /// <summary>
        /// Whether a kingdom has actually been built on this team on this machine.
        ///
        /// A keep or a single building is enough: both carry the team, and both would be orphaned
        /// by moving their owner to a different number. A Player object that merely EXISTS is not
        /// enough, because one is created for every peer the moment they appear in the roster,
        /// before they own anything at all.
        /// </summary>
        private static bool KingdomExistsOn(int team)
        {
            try
            {
                if (HasSomethingBuilt(Player.inst, team)) return true;

                foreach (SessionPlayer kp in Main.kCPlayers.Values)
                {
                    if (kp == null) continue;
                    if (HasSomethingBuilt(kp.inst, team)) return true;
                }
            }
            catch (Exception e)
            {
                // On any doubt, refuse to re-pin. Keeping a disagreement is recoverable; stranding
                // a kingdom is not.
                Main.helper.Log("[LOADID] could not tell whether team " + team + " has a kingdom ("
                                + e.Message + "); assuming it does");
                return true;
            }

            return false;
        }

        private static bool HasSomethingBuilt(Player p, int team)
        {
            if (p == null || p.PlayerLandmassOwner == null) return false;
            if (p.PlayerLandmassOwner.teamId != team) return false;

            return p.keep != null || (p.Buildings != null && p.Buildings.Count > 0);
        }

        /// <summary>
        /// Moves an already-created Player object onto the team we have just adopted.
        ///
        /// The registry and the object have to agree. The local player's team is written at the
        /// handshake, before the roster arrives, and a remote player's is written by the
        /// SessionPlayer constructor; changing only the dictionary would leave the object still
        /// answering to the number we just abandoned.
        /// </summary>
        private static void RetagPlayerObject(string steamId, int oldTeam, int newTeam)
        {
            try
            {
                if (steamId == Main.PlayerSteamID
                    && Player.inst != null && Player.inst.PlayerLandmassOwner != null
                    && Player.inst.PlayerLandmassOwner.teamId == oldTeam)
                {
                    Player.inst.PlayerLandmassOwner.teamId = newTeam;
                    Main.helper.Log($"[LOADID] our own kingdom re-tagged from team {oldTeam} to {newTeam}");
                }

                SessionPlayer kp;
                if (Main.kCPlayers.TryGetValue(steamId, out kp)
                    && kp != null && kp.inst != null && kp.inst.PlayerLandmassOwner != null
                    && kp.inst.PlayerLandmassOwner.teamId == oldTeam)
                {
                    kp.inst.PlayerLandmassOwner.teamId = newTeam;
                    Main.helper.Log($"[LOADID] {steamId}'s kingdom object re-tagged from team {oldTeam} to {newTeam}");
                }
            }
            catch (Exception e)
            {
                Main.helper.Log("[LOADID] could not re-tag the player object: " + e.Message);
            }
        }

        /// <summary>
        /// Keeps a departed player's team out of circulation for the rest of the session.
        ///
        /// Their kingdom still stands and still carries the team, so handing it to somebody else
        /// would put a live player on top of a preserved one. Their entry is normally pinned
        /// already; this covers the case where it is not, and states the intent at the call site.
        /// </summary>
        public static void ReserveTeamFor(SessionPlayer player)
        {
            if (player == null || player.inst == null || player.inst.PlayerLandmassOwner == null) return;
            if (string.IsNullOrEmpty(player.steamId)) return;

            int team = player.inst.PlayerLandmassOwner.teamId;
            if (savedTeamBySteamId.ContainsKey(player.steamId)) return;   // already permanent

            sessionTeamBySteamId[player.steamId] = team;
            Main.helper.Log($"[LOADID] reserved teamId {team} for {player.name} ({player.steamId}), they may reconnect");
        }

        // Forget the loaded session (new lobby / disconnect) so fresh games go back to pure formula.
        public static void Clear()
        {
            if (savedTeamBySteamId.Count > 0 || sessionTeamBySteamId.Count > 0)
                Main.helper.Log("[LOADID] cleared player identity registries");
            savedTeamBySteamId.Clear();
            sessionTeamBySteamId.Clear();
        }
    }
}
