// Kingdoms and Castles Multiplayer. Copyright (c) BrassyCrane and contributors.
// All rights reserved, not open source: see LICENSE.md for what you may do with this code.

using System;
using Assets.Code;
using Harmony;
using UnityEngine;
using KaCMultiplayer.Lobby;

namespace KaCMultiplayer.Net
{
    /// <summary>
    /// Envoys from the Hall of Diplomacy, in multiplayer.
    ///
    /// The game's envoy code knows two kinds of envoy: the player's, on team 0, and an AI
    /// kingdom's, on any other team. In multiplayer no player is team 0, so every player's envoy
    /// ran the AI branch, looked up an AI kingdom that does not exist and threw every frame, and
    /// walking one to another kingdom's keep never opened anything. Its gift could only be handed
    /// to an AI kingdom running on the same machine.
    ///
    /// Here a player's envoy is run by this file instead:
    ///   reaching another kingdom's keep or hall opens the game's own conversation when that is an
    ///     AI kingdom this machine runs (the host), and the diplomacy window otherwise;
    ///   it picks up a gift waiting at its own Hall of Diplomacy, as the game's envoy does;
    ///   delivering the gift sends it to the other player (PlayerRelations.SendEnvoyGift), or to
    ///     the host for an AI kingdom (AiDiplomacy.GiveEnvoyGift).
    /// An AI kingdom's own envoys on the host keep the game's code untouched.
    /// </summary>
    public static class EnvoySync
    {
        private static bool updateErrorLogged;

        /// <summary>This machine's own team, or int.MinValue before it has one.</summary>
        private static int LocalTeam()
        {
            return (Player.inst != null && Player.inst.PlayerLandmassOwner != null)
                ? Player.inst.PlayerLandmassOwner.teamId : int.MinValue;
        }

        /// <summary>
        /// Envoy.Update for a player's envoy (and for an AI envoy mirrored onto a guest, which has
        /// no AI kingdom to ask). Multiplayer reason: see the class.
        /// </summary>
        [HarmonyPatch(typeof(Envoy), "Update")]
        public class EnvoyUpdateHook
        {
            public static bool Prefix(Envoy __instance)
            {
                if (!Main.InMultiplayer) return true;
                try
                {
                    UnitSystem.Army army = __instance.general != null ? __instance.general.army : null;
                    if (army == null) return true;

                    // An AI kingdom's envoy on the host that runs it: the game's own code.
                    if (AIBrainsContainer.inst != null && AIBrainsContainer.inst.GetKingdomFromTeamId(army.teamId) != null)
                        return true;
                    if (!UnitSystem.inst.IsAlive(army)) return false;

                    bool mine = army.teamId == LocalTeam();
                    if (mine && army.moveStatus == UnitSystem.MoveStatus.Complete && __instance.rightClickNeedsConsumed)
                    {
                        __instance.rightClickNeedsConsumed = false;
                        Building target = army.moveTarget as Building;
                        if (target != null && target.TeamID() != army.teamId
                            && (target.uniqueNameHash == World.keepHash || target.uniqueNameHash == World.hallofdiplomacyHash)
                            && OnScreen(__instance))
                            OpenTalks(__instance, target);
                    }

                    UpdateGift(__instance, army, mine);
                    __instance.gift_icon.transform.Rotate(0f, Time.deltaTime * 60f, 0f);
                    return false;
                }
                catch (Exception e)
                {
                    if (!updateErrorLogged) { updateErrorLogged = true; Main.LogEx("envoy update", e); }
                    return false;
                }
            }
        }

        /// <summary>
        /// Delivering an envoy's gift (the unit panel's button). Multiplayer reason: the game can
        /// only hand it to an AI kingdom on this machine; another player's kingdom, or an AI
        /// kingdom on a guest, is reached through the network instead.
        /// </summary>
        [HarmonyPatch(typeof(UnitUI), "OnGiftDeliver")]
        public class GiftDeliverHook
        {
            public static bool Prefix(UnitUI __instance)
            {
                if (!Main.InMultiplayer) return true;
                try
                {
                    UnitSystem.Army army = PrivateField.Get<IMoveableUnit>(__instance, "currentUnitSelected") as UnitSystem.Army;
                    if (army == null || army.armyType != UnitSystem.ArmyType.Envoy || army.teamId != LocalTeam()) return false;
                    Envoy envoy = army.general.GetComponent<Envoy>();
                    if (envoy == null || envoy.giftAmount == default(ResourceAmount)) return false;

                    int lm = World.inst.GetCellDataClamped(army.generalPos).landMassIdx;
                    LandmassOwner there = World.GetLandmassOwner(lm);
                    if (there == null || there.teamId == army.teamId) return false;

                    // The host's envoy at an AI kingdom it runs: the game's own conversation.
                    if (AIKingdom.GetKingdomByLandmass(lm) != null) return true;

                    if (AiDiplomacy.IsAiTeam(there.teamId)) AiDiplomacy.GiveEnvoyGift(army.teamId, there.teamId, envoy.giftAmount);
                    else PlayerRelations.SendEnvoyGift(army.teamId, there.teamId, envoy.giftAmount);
                    envoy.giftAmount = default(ResourceAmount);
                    return false;
                }
                catch (Exception e)
                {
                    Main.LogEx("envoy gift delivery", e);
                    return false;
                }
            }
        }

        /// <summary>
        /// "Gift ready for pickup" and a minimap ping when this kingdom's hall finishes a gift.
        /// Multiplayer reason: the game only tells team 0, which is nobody in multiplayer. Runs
        /// before the game's Update, while the gift icon is still hidden, as the game's check does.
        /// </summary>
        [HarmonyPatch(typeof(HallOfDiplomacy), "Update")]
        public class GiftReadyHook
        {
            public static void Prefix(HallOfDiplomacy __instance)
            {
                if (!Main.InMultiplayer) return;
                try
                {
                    if (__instance.giftIcon.activeSelf || !__instance.HasGift() || __instance.b.TeamID() != LocalTeam()) return;

                    Vector3 at = __instance.b.Center();
                    KingdomLog.TryLog("giftready", I2.Loc.ScriptLocalization.HallOfDiplomacyGiftReadyForPickup,
                                      KingdomLog.LogStatus.Important, 20f, at);
                    MiniMapUIPing ping = UnityEngine.Object.Instantiate(MiniMapUI.inst.pingPrefab, MiniMapUI.inst.mapPingMarkerContainer);
                    ping.maxflashCount = 2;
                    ping.color = Color.green;
                    ping.pingSpeed = 17f;
                    ping.pingOpacityStrength = 0.75f;
                    MiniMapUI.inst.PingAtWorldLocation(ping, at);
                }
                catch (Exception e) { Main.LogEx("hall gift notice", e); }
            }
        }

        /// <summary>The game only starts a conversation for an envoy the player can see.</summary>
        private static bool OnScreen(Envoy envoy)
        {
            Renderer r = envoy.GetComponentInChildren<Renderer>();
            if (r == null || Cam.inst == null) return true;
            return GeometryUtility.TestPlanesAABB(GeometryUtility.CalculateFrustumPlanes(Cam.inst.GetActiveCamera()), r.bounds);
        }

        /// <summary>
        /// The envoy reached another kingdom: the game's conversation for an AI kingdom this
        /// machine runs, the diplomacy window for anyone else.
        /// </summary>
        private static void OpenTalks(Envoy envoy, Building target)
        {
            int lm = target.LandMass();
            Cam.inst.SetDesiredTrackingPos(target.Center());

            AIKingdom k = AIKingdom.GetKingdomByLandmass(lm);
            if (k != null)
            {
                GameUI.inst.diplomacyUI.Show(envoy, World.GetLandmassOwner(lm), lm, k);
                return;
            }
            DiplomacyWindow.Show();
        }

        /// <summary>
        /// The gift the envoy carries: shown while it has one, and picked up from its own Hall of
        /// Diplomacy when one is ready there, as Envoy.TryGrabbingGift does for team 0. Only this
        /// machine's own envoys pick up, because only this machine holds its own halls' gifts.
        /// </summary>
        private static void UpdateGift(Envoy envoy, UnitSystem.Army army, bool mine)
        {
            if (envoy.giftAmount != default(ResourceAmount))
            {
                envoy.gift.SetActive(true);
                envoy.gift_icon.SetActive(true);
                return;
            }

            envoy.gift.SetActive(false);
            envoy.gift_icon.SetActive(false);
            if (!mine) return;

            Vector3 pos = envoy.transform.position;
            int lm = World.inst.GetCellDataClamped(pos).landMassIdx;
            LandmassOwner own = World.GetLandmassOwnerByTeamId(army.teamId);
            if (own == null || World.GetLandmassOwner(lm) != own) return;

            ArrayExt<Building> halls = Player.inst.GetBuildingListForLandMass(lm, World.hallofdiplomacyHash);
            for (int i = 0; i < halls.Count; i++)
            {
                HallOfDiplomacy hall = halls.data[i].GetComponent<HallOfDiplomacy>();
                if (hall == null || !hall.HasGift()) continue;
                Vector3 c = halls.data[i].Center();
                float dx = pos.x - c.x, dz = pos.z - c.z;
                if (dx * dx + dz * dz >= 12.25f) continue;

                envoy.giftAmount = hall.TakeGift();
                envoy.gift_icon.GetComponent<MeshRenderer>().material = own.UniMaterialFogClip;
                EffectsMan.inst.HealthEffect.CreateAndPlay(pos);
                return;
            }
        }
    }
}
