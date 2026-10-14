using System;
using UnityEngine;

namespace KaCMultiplayer.LoadSaveOverrides
{
    /// <summary>
    /// Repaints this player's kingdom a few times just after a multiplayer save loads.
    ///
    /// A guest's flag colours and building materials can come back wrong after a load, because they
    /// are set up before the kingdom knows its banner. A few passes, spaced out, put them right once
    /// everything they depend on exists.
    ///
    /// Villager meshes are deliberately left alone. A rebuild of them here used to read
    /// VillagerSystem.instances.Count, which is always 0 (the game fills that list's slots directly
    /// and never sets its count), so it reset nothing and added every villager a second time to the
    /// default mesh. That left a frozen copy of each villager on screen and pointed working villagers
    /// at mesh slots that do not exist, and the resulting throw every frame stopped Player.Update and
    /// JobSystem.Update partway through their loops, freezing most of the kingdom.
    /// </summary>
    internal static class PostLoadVisualRepair
    {
        private static int remaining;
        private static int nextTick;

        /// <summary>Called when a save has finished unpacking: queues four repair passes.</summary>
        public static void Schedule()
        {
            remaining = 4;
            nextTick = Main.FixedUpdateInterval + 5;
        }

        /// <summary>Cancels any passes still queued, for a session ending.</summary>
        public static void Reset()
        {
            remaining = 0;
        }

        /// <summary>
        /// Runs the next queued pass when it is due: banner, building materials, build menu and
        /// visibility.
        /// </summary>
        public static void Tick()
        {
            if (remaining <= 0 || Main.FixedUpdateInterval < nextTick) return;

            try
            {
                SessionPlayer session = null;
                Player local = Player.inst;
                if (Main.kCPlayers.TryGetValue(Main.PlayerSteamID, out session)
                    && session != null && session.inst != null)
                    local = session.inst;

                if (local == null || local.PlayerLandmassOwner == null || World.inst == null)
                {
                    nextTick = Main.FixedUpdateInterval + 30;
                    return;
                }

                int pass = 5 - remaining;
                remaining--;
                nextTick = Main.FixedUpdateInterval + (pass < 2 ? 30 : 120);
                LandmassOwner owner = local.PlayerLandmassOwner;
                int banner = owner.bannerIdx;
                if (banner < 0 && session != null) banner = session.banner;
                if (banner < 0) banner = Main.localChosenBanner;
                if (banner < 0 || banner >= World.inst.liverySets.Count) banner = 0;

                // Force the runtime material references even when the saved banner number already
                // matches. Player.SetIndexedBanner deliberately no-ops on an equal number.
                Main.SetKingdomBanner(owner, banner, "post-load guest visual repair");

                int buildings = 0;
                if (local.Buildings != null)
                    for (int i = 0; i < local.Buildings.Count; i++)
                    {
                        Building b = local.Buildings.data[i];
                        if (b == null) continue;
                        try { b.UpdateMaterialSelection(); buildings++; } catch { }
                    }

                if (BuildUI.inst != null)
                {
                    try { BuildUI.inst.UpdateMaterials(banner); } catch { }
                    BuildMenuMaterials.Refresh(pass == 1 || remaining == 0);
                }

                try { local.RefreshVisibility(true); } catch { }

                Main.MarkBannersDirty();
                Main.helper.Log("[LOADVIS] pass " + pass + ": team=" + owner.teamId
                    + " banner=" + banner + " buildings=" + buildings);
            }
            catch (Exception ex) { Main.LogEx("post-load guest visual repair", ex); }
        }
    }
}
