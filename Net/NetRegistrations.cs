using KaCMultiplayer.Lobby;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;  // MethodInfo, FieldInfo, BindingFlags
using Assets.Code;        // ResourceAmount
using Assets.Interface;   // IResourceStorage
using UnityEngine;
using KaCMultiplayer.Net.Messages;
using KaCMultiplayer.Trade;   // TradeMath, the shared trade economics

// The gameplay side of the mod lives one namespace up; the net layer calls into it.
using KaCMultiplayer;

namespace KaCMultiplayer.Net
{
    /// <summary>
    /// The one place that says which message types exist and what happens when each one
    /// arrives. If you are looking for "what can travel over the wire", it is this file.
    ///
    /// Adding a message is three steps: give it an id in <see cref="NetMessageId"/>,
    /// write the class, add a block here. Miss the block and the type simply never
    /// arrives, which is why this is a table you can read rather than an assembly scan
    /// you have to trust.
    ///
    /// ## Relay shape
    ///
    /// Most messages follow "client does a thing, host passes it on". Two variants,
    /// and picking the wrong one is the easiest way to break behaviour:
    ///
    /// * <see cref="NetRouter.Relay"/>, everyone <i>except</i> the sender. For actions
    ///   the sender already applied locally.
    /// * <see cref="NetRouter.RelayIncludingSender"/>, everyone, sender included. For
    ///   actions where the sender waits for the echo to see its own effect.
    ///
    /// Check the send site before choosing. If it applies the change locally, use Relay.
    /// If it only fires the message and waits, use RelayIncludingSender.
    /// </summary>
    public static class NetRegistrations
    {
        public static void Install()
        {
            // ---- lobby ----------------------------------------------------

            // Chat: the send site (LobbyScreen) clears the input box and does not
            // add the line locally, so the sender needs its own echo back.
            NetRegistry.Register<ChatSayMessage>(NetMessageId.ChatSay);
            NetRegistry.OnServer<ChatSayMessage>(NetMessageId.ChatSay,
                (m, ctx) => { if (NetRouter.RelayAndApply(m, ctx)) RenderChatLine(m); });
            NetRegistry.OnClient<ChatSayMessage>(NetMessageId.ChatSay,
                (m, ctx) => RenderChatLine(m));

            // System notices in lobby chat. Host-originated and broadcast to everyone
            // including the host, so there is no server handler, nothing sends it up.
            NetRegistry.Register<ChatNoticeMessage>(NetMessageId.ChatNotice);
            NetRegistry.OnClient<ChatNoticeMessage>(NetMessageId.ChatNotice,
                (m, ctx) => RenderChatNotice(m));

            // Modal dialog pushed to one client, typically just before disconnecting it.
            NetRegistry.Register<NoticeMessage>(NetMessageId.Notice);
            NetRegistry.OnClient<NoticeMessage>(NetMessageId.Notice,
                (m, ctx) => ShowNotice(m));

            // Ready toggle. The host owns the answer: it flips the player's current state
            // and overwrites the field before relaying, so the value sent up is ignored.
            NetRegistry.Register<PlayerReadyMessage>(NetMessageId.ReadyState);
            NetRegistry.OnServer<PlayerReadyMessage>(NetMessageId.ReadyState, ToggleReady);
            NetRegistry.OnClient<PlayerReadyMessage>(NetMessageId.ReadyState,
                (m, ctx) => ApplyReady(m));

            // Banner choice. Sender doesn't apply locally, so it needs its own echo.
            NetRegistry.Register<BannerPickMessage>(NetMessageId.BannerPick);
            NetRegistry.OnServer<BannerPickMessage>(NetMessageId.BannerPick,
                (m, ctx) => { if (NetRouter.RelayAndApply(m, ctx)) ApplyBanner(m); });
            NetRegistry.OnClient<BannerPickMessage>(NetMessageId.BannerPick,
                (m, ctx) => ApplyBanner(m));

            // Kingdom name. Host records it too, so its own roster broadcasts carry it.
            NetRegistry.Register<KingdomLabelMessage>(NetMessageId.KingdomLabel);
            NetRegistry.OnServer<KingdomLabelMessage>(NetMessageId.KingdomLabel, RecordKingdomLabel);
            NetRegistry.OnClient<KingdomLabelMessage>(NetMessageId.KingdomLabel,
                (m, ctx) => ApplyKingdomLabel(m));

            // Full roster. Host-originated, never relayed, receivers reconcile against it.
            NetRegistry.Register<PeerRosterMessage>(NetMessageId.PeerRoster);
            NetRegistry.OnClient<PeerRosterMessage>(NetMessageId.PeerRoster,
                (m, ctx) => ApplyRoster(m));

            // ---- world ----------------------------------------------------

            // Game speed: whoever changed it already applied it locally through the
            // SpeedControlUI patch, so exclude them from the relay.
            NetRegistry.Register<TimeScaleMessage>(NetMessageId.TimeScale);
            NetRegistry.OnServer<TimeScaleMessage>(NetMessageId.TimeScale,
                (m, ctx) => NetRouter.Relay(m, ctx));
            NetRegistry.OnClient<TimeScaleMessage>(NetMessageId.TimeScale,
                (m, ctx) => ApplyTimeScale(m.Speed, m.Origin));

            // Trees. Relayed including the sender: the sender re-applies to a tree it has
            // already felled, which TreeSystem tolerates, and a blind relay is cheaper than
            // tracking who to skip.
            NetRegistry.Register<TreeFellMessage>(NetMessageId.TreeFell);
            NetRegistry.OnServer<TreeFellMessage>(NetMessageId.TreeFell,
                (m, ctx) => { if (NetRouter.RelayAndApply(m, ctx)) ApplyTreeFell(m); });
            NetRegistry.OnClient<TreeFellMessage>(NetMessageId.TreeFell,
                (m, ctx) => ApplyTreeFell(m));

            NetRegistry.Register<TreeShakeMessage>(NetMessageId.TreeShake);
            NetRegistry.OnServer<TreeShakeMessage>(NetMessageId.TreeShake,
                (m, ctx) => { if (NetRouter.RelayAndApply(m, ctx)) ApplyTreeShake(m); });
            NetRegistry.OnClient<TreeShakeMessage>(NetMessageId.TreeShake,
                (m, ctx) => ApplyTreeShake(m));

            // Growth is host-driven and pushed out, so there is nothing to handle upward.
            NetRegistry.Register<TreeGrowMessage>(NetMessageId.TreeGrow);
            NetRegistry.OnClient<TreeGrowMessage>(NetMessageId.TreeGrow,
                (m, ctx) => ApplyTreeGrow(m));

            NetRegistry.Register<WeatherSetMessage>(NetMessageId.WeatherSet);
            NetRegistry.OnServer<WeatherSetMessage>(NetMessageId.WeatherSet,
                (m, ctx) => { if (NetRouter.RelayAndApply(m, ctx)) ApplyWeather(m); });
            NetRegistry.OnClient<WeatherSetMessage>(NetMessageId.WeatherSet,
                (m, ctx) => ApplyWeather(m));

            // Dragons. Must include the sender: on a non-host client the Harmony Prefix
            // blocked the local spawn, so the echo is the only thing that spawns it.
            NetRegistry.Register<DragonSpawnMessage>(NetMessageId.DragonSpawn);
            NetRegistry.OnServer<DragonSpawnMessage>(NetMessageId.DragonSpawn,
                (m, ctx) => { if (NetRouter.RelayAndApply(m, ctx)) ApplyDragonSpawn(m); });
            NetRegistry.OnClient<DragonSpawnMessage>(NetMessageId.DragonSpawn,
                (m, ctx) => ApplyDragonSpawn(m));

            // Dragon flight. Host-authoritative and one-way: only the host publishes, so there is
            // no server-side handler and no relay decision to make. See Combat/DragonFlightSync.cs.
            NetRegistry.Register<DragonFlightMessage>(NetMessageId.DragonFlight);
            NetRegistry.OnClient<DragonFlightMessage>(NetMessageId.DragonFlight,
                (m, ctx) => KaCMultiplayer.Combat.DragonFlightSync.Apply(m));

            NetRegistry.Register<DragonHealthMessage>(NetMessageId.DragonHealth);
            NetRegistry.OnServer<DragonHealthMessage>(NetMessageId.DragonHealth,
                (m, ctx) => { if (NetRouter.RelayAndApply(m, ctx)) ApplyDragonHealth(m); });
            NetRegistry.OnClient<DragonHealthMessage>(NetMessageId.DragonHealth,
                (m, ctx) => ApplyDragonHealth(m));

            // Siege catapults. NOT relayed back to the sender for any of the three: unlike a
            // dragon, whose local spawn is blocked on a non-host client so the echo is the only
            // thing that creates it, a catapult is built by the game's own code on the owner's
            // machine and already exists there before any of this is sent.
            NetRegistry.Register<SiegeCatapultSpawnMessage>(NetMessageId.SiegeCatapultSpawn);
            NetRegistry.OnServer<SiegeCatapultSpawnMessage>(NetMessageId.SiegeCatapultSpawn,
                (m, ctx) => { if (NetRouter.RelayAndApply(m, ctx)) ApplySiegeCatapultSpawn(m); });
            NetRegistry.OnClient<SiegeCatapultSpawnMessage>(NetMessageId.SiegeCatapultSpawn,
                (m, ctx) => ApplySiegeCatapultSpawn(m));

            NetRegistry.Register<SiegeCatapultHealthMessage>(NetMessageId.SiegeCatapultHealth);
            NetRegistry.OnServer<SiegeCatapultHealthMessage>(NetMessageId.SiegeCatapultHealth,
                (m, ctx) => { if (NetRouter.RelayAndApply(m, ctx)) ApplySiegeCatapultHealth(m); });
            NetRegistry.OnClient<SiegeCatapultHealthMessage>(NetMessageId.SiegeCatapultHealth,
                (m, ctx) => ApplySiegeCatapultHealth(m));

            NetRegistry.Register<SiegeCatapultDespawnMessage>(NetMessageId.SiegeCatapultDespawn);
            NetRegistry.OnServer<SiegeCatapultDespawnMessage>(NetMessageId.SiegeCatapultDespawn,
                (m, ctx) => { if (NetRouter.RelayAndApply(m, ctx)) ApplySiegeCatapultDespawn(m); });
            NetRegistry.OnClient<SiegeCatapultDespawnMessage>(NetMessageId.SiegeCatapultDespawn,
                (m, ctx) => ApplySiegeCatapultDespawn(m));

            // Wolves, a den's pack at a time. Not relayed back to the sender: the arbiter is the
            // one that resolved the fight, so its own report tells it nothing new.
            // Streamer effects change the simulation for whoever has them, so everybody takes the
            // same set. Not relayed back to the sender, whose audience already applied it.
            NetRegistry.Register<StreamerEffectsMessage>(NetMessageId.StreamerEffects);
            NetRegistry.OnServer<StreamerEffectsMessage>(NetMessageId.StreamerEffects,
                (m, ctx) => { if (NetRouter.RelayAndApply(m, ctx)) ApplyStreamerEffects(m); });
            NetRegistry.OnClient<StreamerEffectsMessage>(NetMessageId.StreamerEffects,
                (m, ctx) => ApplyStreamerEffects(m));

            // Army positions. Owner-authoritative and not relayed back to the sender, who is the
            // one that stated them.
            NetRegistry.Register<ArmyPositionsMessage>(NetMessageId.ArmyPositions);
            NetRegistry.OnServer<ArmyPositionsMessage>(NetMessageId.ArmyPositions,
                (m, ctx) => { if (NetRouter.RelayAndApply(m, ctx)) ApplyArmyPositions(m); });
            NetRegistry.OnClient<ArmyPositionsMessage>(NetMessageId.ArmyPositions,
                (m, ctx) => ApplyArmyPositions(m));

            NetRegistry.Register<WolfPackHealthMessage>(NetMessageId.WolfPackHealth);
            NetRegistry.OnServer<WolfPackHealthMessage>(NetMessageId.WolfPackHealth,
                (m, ctx) => { if (NetRouter.RelayAndApply(m, ctx)) ApplyWolfPackHealth(m); });
            NetRegistry.OnClient<WolfPackHealthMessage>(NetMessageId.WolfPackHealth,
                (m, ctx) => ApplyWolfPackHealth(m));

            NetRegistry.Register<VillagerWarpMessage>(NetMessageId.VillagerWarp);
            NetRegistry.OnServer<VillagerWarpMessage>(NetMessageId.VillagerWarp,
                (m, ctx) => { if (NetRouter.RelayAndApply(m, ctx)) ApplyVillagerWarp(m); });
            NetRegistry.OnClient<VillagerWarpMessage>(NetMessageId.VillagerWarp,
                (m, ctx) => ApplyVillagerWarp(m));

            // A villager died, from whichever machine arbitrates the ground they died on. Same
            // model as the combat outcomes below, for the same reason: every machine simulates
            // every island, so without an arbiter each one kills its own copy on its own schedule.
            NetRegistry.Register<VillagerHomeMessage>(NetMessageId.VillagerHome);
            NetRegistry.OnServer<VillagerHomeMessage>(NetMessageId.VillagerHome,
                (m, ctx) => { if (NetRouter.RelayAndApply(m, ctx)) ApplyVillagerHome(m); });
            NetRegistry.OnClient<VillagerHomeMessage>(NetMessageId.VillagerHome,
                (m, ctx) => ApplyVillagerHome(m));

            NetRegistry.Register<VillagerDeathMessage>(NetMessageId.VillagerDeath);
            NetRegistry.OnServer<VillagerDeathMessage>(NetMessageId.VillagerDeath,
                (m, ctx) => { if (NetRouter.RelayAndApply(m, ctx)) ApplyVillagerDeath(m); });
            NetRegistry.OnClient<VillagerDeathMessage>(NetMessageId.VillagerDeath,
                (m, ctx) => ApplyVillagerDeath(m));

            // ---- Tier 3: ships, units, economy --------------------------------
            //
            // The relayed ones use RelayIncludingSender and discard their own echo inside the
            // handler. The self-check belongs in the handler rather than in the relay, because
            // some senders do act on their own echo and excluding them at the relay would
            // silently stop that.

            NetRegistry.Register<ShipDespawnMessage>(NetMessageId.ShipDespawn);
            NetRegistry.OnServer<ShipDespawnMessage>(NetMessageId.ShipDespawn,
                (m, ctx) => { if (NetRouter.RelayAndApply(m, ctx)) ApplyShipDespawn(m); });
            NetRegistry.OnClient<ShipDespawnMessage>(NetMessageId.ShipDespawn,
                (m, ctx) => ApplyShipDespawn(m));

            NetRegistry.Register<ArmySpawnMessage>(NetMessageId.ArmySpawn);
            NetRegistry.OnServer<ArmySpawnMessage>(NetMessageId.ArmySpawn,
                (m, ctx) => { if (NetRouter.RelayAndApply(m, ctx)) ApplyArmySpawn(m); });
            NetRegistry.OnClient<ArmySpawnMessage>(NetMessageId.ArmySpawn,
                (m, ctx) => ApplyArmySpawn(m));

            NetRegistry.Register<ArmyDespawnMessage>(NetMessageId.ArmyDespawn);
            NetRegistry.OnServer<ArmyDespawnMessage>(NetMessageId.ArmyDespawn,
                (m, ctx) => { if (NetRouter.RelayAndApply(m, ctx)) ApplyArmyDespawn(m); });
            NetRegistry.OnClient<ArmyDespawnMessage>(NetMessageId.ArmyDespawn,
                (m, ctx) => ApplyArmyDespawn(m));

            // Combat outcomes, from whichever machine arbitrates the ground the fight is on.
            // See Combat/CombatRule.cs for who that is and why.
            NetRegistry.Register<FireStartMessage>(NetMessageId.FireStart);
            NetRegistry.OnServer<FireStartMessage>(NetMessageId.FireStart,
                (m, ctx) => { if (NetRouter.RelayAndApply(m, ctx)) ApplyFireStart(m); });
            NetRegistry.OnClient<FireStartMessage>(NetMessageId.FireStart,
                (m, ctx) => ApplyFireStart(m));

            NetRegistry.Register<BuildingWreckedMessage>(NetMessageId.BuildingWrecked);
            NetRegistry.OnServer<BuildingWreckedMessage>(NetMessageId.BuildingWrecked,
                (m, ctx) => { if (NetRouter.RelayAndApply(m, ctx)) ApplyBuildingWrecked(m); });
            NetRegistry.OnClient<BuildingWreckedMessage>(NetMessageId.BuildingWrecked,
                (m, ctx) => ApplyBuildingWrecked(m));

            NetRegistry.Register<BuildingHealthMessage>(NetMessageId.BuildingHealth);
            NetRegistry.OnServer<BuildingHealthMessage>(NetMessageId.BuildingHealth,
                (m, ctx) => { if (NetRouter.RelayAndApply(m, ctx)) ApplyBuildingHealth(m); });
            NetRegistry.OnClient<BuildingHealthMessage>(NetMessageId.BuildingHealth,
                (m, ctx) => ApplyBuildingHealth(m));

            NetRegistry.Register<ArmyHealthMessage>(NetMessageId.ArmyHealth);
            NetRegistry.OnServer<ArmyHealthMessage>(NetMessageId.ArmyHealth,
                (m, ctx) => { if (NetRouter.RelayAndApply(m, ctx)) ApplyArmyHealth(m); });
            NetRegistry.OnClient<ArmyHealthMessage>(NetMessageId.ArmyHealth,
                (m, ctx) => ApplyArmyHealth(m));

            // Host-authoritative: sent from the host either as a broadcast on placement or
            // targeted at a joining client to catch it up. Never travels upward.
            NetRegistry.Register<HazardSpawnMessage>(NetMessageId.HazardSpawn);
            NetRegistry.OnClient<HazardSpawnMessage>(NetMessageId.HazardSpawn,
                (m, ctx) => ApplyHazardSpawn(m));

            NetRegistry.Register<EconomySnapshotMessage>(NetMessageId.EconomySnapshot);
            NetRegistry.OnServer<EconomySnapshotMessage>(NetMessageId.EconomySnapshot,
                (m, ctx) => { if (NetRouter.RelayAndApply(m, ctx)) ApplyEconomySnapshot(m); });
            NetRegistry.OnClient<EconomySnapshotMessage>(NetMessageId.EconomySnapshot,
                (m, ctx) => ApplyEconomySnapshot(m));

            NetRegistry.Register<KeepUpgradeMessage>(NetMessageId.KeepUpgrade);
            NetRegistry.OnServer<KeepUpgradeMessage>(NetMessageId.KeepUpgrade,
                (m, ctx) => { if (NetRouter.RelayAndApply(m, ctx)) ApplyKeepUpgrade(m); });
            NetRegistry.OnClient<KeepUpgradeMessage>(NetMessageId.KeepUpgrade,
                (m, ctx) => ApplyKeepUpgrade(m));

            // Host-driven position corrections, broadcast excluding the host itself.
            NetRegistry.Register<VillagerSnapshotMessage>(NetMessageId.VillagerSnapshot);
            NetRegistry.OnClient<VillagerSnapshotMessage>(NetMessageId.VillagerSnapshot,
                (m, ctx) => ApplyVillagerSnapshot(m));

            // Rubble cleared by a rebuild. Cell-based, because rubble guids differ per machine.
            NetRegistry.Register<RubbleClearMessage>(NetMessageId.RubbleClear);
            NetRegistry.OnServer<RubbleClearMessage>(NetMessageId.RubbleClear,
                (m, ctx) => { if (NetRouter.RelayAndApply(m, ctx)) ApplyRubbleClear(m); });
            NetRegistry.OnClient<RubbleClearMessage>(NetMessageId.RubbleClear,
                (m, ctx) => ApplyRubbleClear(m));

            NetRegistry.Register<TerrainDemolishMessage>(NetMessageId.TerrainDemolish);
            NetRegistry.OnServer<TerrainDemolishMessage>(NetMessageId.TerrainDemolish,
                (m, ctx) => { if (NetRouter.RelayAndApply(m, ctx)) ApplyTerrainDemolish(m); });
            NetRegistry.OnClient<TerrainDemolishMessage>(NetMessageId.TerrainDemolish,
                (m, ctx) => ApplyTerrainDemolish(m));

            NetRegistry.Register<ShipMoveMessage>(NetMessageId.ShipMove);
            NetRegistry.OnServer<ShipMoveMessage>(NetMessageId.ShipMove,
                (m, ctx) => { if (NetRouter.RelayAndApply(m, ctx)) ApplyShipMove(m); });
            NetRegistry.OnClient<ShipMoveMessage>(NetMessageId.ShipMove,
                (m, ctx) => ApplyShipMove(m));

            // Ship life, from whichever machine arbitrates the water it is fighting on (the host,
            // since water is unowned). No death message: a ship sinks itself once life hits zero.
            NetRegistry.Register<ShipHealthMessage>(NetMessageId.ShipHealth);
            NetRegistry.OnServer<ShipHealthMessage>(NetMessageId.ShipHealth,
                (m, ctx) => { if (NetRouter.RelayAndApply(m, ctx)) ApplyShipHealth(m); });
            NetRegistry.OnClient<ShipHealthMessage>(NetMessageId.ShipHealth,
                (m, ctx) => ApplyShipHealth(m));

            NetRegistry.Register<MerchantTradeMessage>(NetMessageId.MerchantTrade);
            NetRegistry.OnServer<MerchantTradeMessage>(NetMessageId.MerchantTrade,
                (m, ctx) => { if (NetRouter.RelayAndApply(m, ctx)) ApplyMerchantTrade(m); });
            NetRegistry.OnClient<MerchantTradeMessage>(NetMessageId.MerchantTrade,
                (m, ctx) => ApplyMerchantTrade(m));

            // Diplomacy. Applied on every machine including the sender's, see
            // PlayerRelationMessage for why this one does not apply locally first.
            // Tribute and peace deals. Relayed to everyone INCLUDING the sender, like relations
            // are, because the outcome is reasoned out identically on every machine rather than
            // applied locally first and announced afterwards.
            NetRegistry.Register<DiplomacyDealMessage>(NetMessageId.DiplomacyDeal);
            NetRegistry.OnServer<DiplomacyDealMessage>(NetMessageId.DiplomacyDeal,
                (m, ctx) => { if (NetRouter.RelayAndApply(m, ctx)) PlayerRelations.ApplyDeal(m); });
            NetRegistry.OnClient<DiplomacyDealMessage>(NetMessageId.DiplomacyDeal,
                (m, ctx) => PlayerRelations.ApplyDeal(m));

            NetRegistry.Register<PlayerRelationMessage>(NetMessageId.PlayerRelation);
            NetRegistry.OnServer<PlayerRelationMessage>(NetMessageId.PlayerRelation,
                (m, ctx) => { if (NetRouter.RelayAndApply(m, ctx)) ApplyPlayerRelation(m); });
            NetRegistry.OnClient<PlayerRelationMessage>(NetMessageId.PlayerRelation,
                (m, ctx) => ApplyPlayerRelation(m));

            // ---- Tier 4: world and save state ---------------------------------
            //
            // These build and mutate the world. A mistake here damages a game rather than
            // annoying someone, so each handler logs on entry and bails on anything missing
            // instead of pressing on with a partial apply.

            NetRegistry.Register<VillagerAddMessage>(NetMessageId.VillagerAdd);
            NetRegistry.OnServer<VillagerAddMessage>(NetMessageId.VillagerAdd,
                (m, ctx) => { if (NetRouter.RelayAndApply(m, ctx)) ApplyVillagerAdd(m); });
            NetRegistry.OnClient<VillagerAddMessage>(NetMessageId.VillagerAdd,
                (m, ctx) => ApplyVillagerAdd(m));

            // Host-originated, and it regenerates the map, never travels upward.
            NetRegistry.Register<WorldSeedMessage>(NetMessageId.WorldSeed);
            NetRegistry.OnClient<WorldSeedMessage>(NetMessageId.WorldSeed,
                (m, ctx) => ApplyWorldSeed(m));

            NetRegistry.Register<KeepPlaceRandomMessage>(NetMessageId.KeepPlaceRandom);
            NetRegistry.OnServer<KeepPlaceRandomMessage>(NetMessageId.KeepPlaceRandom,
                (m, ctx) => { if (NetRouter.RelayAndApply(m, ctx)) ApplyKeepPlaceRandom(m); });
            NetRegistry.OnClient<KeepPlaceRandomMessage>(NetMessageId.KeepPlaceRandom,
                (m, ctx) => ApplyKeepPlaceRandom(m));

            // Host-originated signal to leave the lobby and start playing.
            NetRegistry.Register<SessionStartMessage>(NetMessageId.SessionStart);
            NetRegistry.OnClient<SessionStartMessage>(NetMessageId.SessionStart,
                (m, ctx) => ApplySessionStart());

            NetRegistry.Register<BuildPlaceMessage>(NetMessageId.BuildPlace);
            NetRegistry.OnServer<BuildPlaceMessage>(NetMessageId.BuildPlace,
                (m, ctx) => { if (NetRouter.RelayAndApply(m, ctx)) ApplyBuildPlace(m); });
            NetRegistry.OnClient<BuildPlaceMessage>(NetMessageId.BuildPlace,
                (m, ctx) => ApplyBuildPlace(m));

            NetRegistry.Register<BuildSnapshotMessage>(NetMessageId.BuildSnapshot);
            NetRegistry.OnServer<BuildSnapshotMessage>(NetMessageId.BuildSnapshot,
                (m, ctx) => { if (NetRouter.RelayAndApply(m, ctx)) ApplyBuildSnapshot(m); });
            NetRegistry.OnClient<BuildSnapshotMessage>(NetMessageId.BuildSnapshot,
                (m, ctx) => ApplyBuildSnapshot(m));

            // Lobby settings. Host-originated, and must arrive before the world seed,
            // generating a map needs the type/size/rivers this carries.
            NetRegistry.Register<LobbySettingsMessage>(NetMessageId.LobbySettings);
            NetRegistry.OnClient<LobbySettingsMessage>(NetMessageId.LobbySettings,
                (m, ctx) => ApplyLobbySettings(m));

            // ---- session lifecycle --------------------------------------------
            //
            // Handlers live in SessionHandlers, they are the bulkiest in the mod and would
            // bury this table.

            // Host greets one client. Never relayed.
            NetRegistry.Register<HandshakeMessage>(NetMessageId.Handshake);
            NetRegistry.OnClient<HandshakeMessage>(NetMessageId.Handshake,
                (m, ctx) => SessionHandlers.OnHandshake(m));

            // Client announces itself; the host records it and starts the catch-up sequence.
            NetRegistry.Register<ClientJoinedMessage>(NetMessageId.ClientJoined);
            NetRegistry.OnServer<ClientJoinedMessage>(NetMessageId.ClientJoined,
                SessionHandlers.OnClientJoinedServer);
            NetRegistry.OnClient<ClientJoinedMessage>(NetMessageId.ClientJoined,
                (m, ctx) => SessionHandlers.OnClientJoinedClient(m));

            // Save transfer. Host to one client, chunked; the state machine lives in
            // SaveTransfer.
            NetRegistry.Register<SaveTransferMessage>(NetMessageId.SaveTransfer);
            NetRegistry.OnClient<SaveTransferMessage>(NetMessageId.SaveTransfer,
                (m, ctx) => SaveTransfer.Apply(m));

            NetRegistry.Seal();

            // Runs every load. Cheap (a few dozen round trips), and it is the only thing
            // standing between a codec typo and a desync that only shows up once two
            // machines are in a session together.
            NetSelfTest.Run();
        }

        /// <summary>
        /// Adopts the host's lobby settings. Copies into the existing object rather than
        /// replacing it, so the many places holding a reference to
        /// LobbySettings.Current all see the update.
        /// </summary>
        private static void ApplyLobbySettings(LobbySettingsMessage m)
        {
            m.ApplyTo(LobbySettings.Current);
            LobbySettings.Current.ApplyToWorld();

            NetLog.Info("lobby settings: '" + m.ServerName + "' max=" + m.MaxPlayers +
                        " locked=" + m.Locked + " size=" + m.WorldSize + " type=" + m.WorldType);
        }

        // ---- Tier 4: world and save state -----------------------------------

        private static void ApplyVillagerAdd(VillagerAddMessage m)
        {
            if (IsOwnEcho(m.Origin)) return;

            SessionPlayer player;
            if (!NetPlayers.TryGet(m.Origin, "villager add", out player)) return;
            if (player.inst == null) { NetLog.Warn("villager add: player " + m.Origin + " has no inst"); return; }

            try
            {
                Villager v = Villager.CreateVillager();
                v.guid = m.Villager;                    // adopt the sender's id
                player.inst.Workers.Add(v);
                player.inst.Homeless.Add(v);
                NetLog.Info("villager add: " + m.Villager + " for " + player.name);
            }
            catch (Exception ex) { NetLog.Error("villager add", ex); }
        }

        private static void ApplyWorldSeed(WorldSeedMessage m)
        {
            try
            {
                NetLog.Info("world seed " + m.Seed + ", regenerating");

                // Guarded per player, because everything that matters here happens AFTER this loop.
                // Player.Reset reaches the global JobSystem.ClearAllJobs, which is the exact call
                // behind the remote-player reset crash: it dereferences job tables a half-built
                // kingdom has not allocated yet. When it threw, the map was never regenerated and
                // this client sat in the old world with no explanation.
                foreach (SessionPlayer p in Main.kCPlayers.Values)
                {
                    try
                    {
                        if (p != null && p.inst != null) p.inst.Reset();
                    }
                    catch (Exception ex)
                    {
                        NetLog.Error("resetting a kingdom before regenerating the world", ex);
                    }
                }

                // Generation depends on the seed AND on type/size/rivers. The settings
                // message is sent before this one, so LobbySettings.Current is
                // already current, apply it before generating or the map comes out
                // different until someone presses New Map.
                if (LobbySettings.Current != null)
                {
                    World.inst.mapBias = LobbySettings.Current.WorldType;
                    World.inst.mapRiverLakes = LobbySettings.Current.WorldRivers;
                    World.inst.mapSize = LobbySettings.Current.WorldSize;
                }

                World.inst.Generate(m.Seed);
                LobbyScreen.mapPreviewDirty = true;

                // The map has only just come into existence, and every kingdom built before now
                // sized its per-landmass job tables to the world that existed at handshake time,
                // usually the menu's. Nothing in the game grows them afterwards, and a table too
                // short to cover a player's own island makes the job hooks fall back to the local
                // player's settings, which leaves that kingdom's farms permanently unstaffed.
                // Now is the first moment NumLandMasses is the real number. See
                // Main.EnsureJobTablesCoverWorld.
                Main.EnsureAllJobTablesCoverWorld();

                Cell centre = World.inst.GetCellData(World.inst.GridWidth / 2, World.inst.GridHeight / 2);
                if (centre != null) Cam.inst.SetTrackingPos(centre.Center);
            }
            catch (Exception ex) { NetLog.Error("world seed", ex); }
        }

        private static void ApplySessionStart()
        {
            NetLog.Info("session start");

            // Leave the lobby UI first, whatever happens next.
            Main.TransitionTo(MenuState.LeaveMenus);

            try
            {
                // A world that came from a save must NOT go through StartGame.
                //
                // StartGame is the NEW-GAME entry point. It re-enters playing mode, resets the town
                // name and re-arms the first-time UI, including the prompt to go and place a keep.
                // Run over a kingdom a save has just restored, it asks a returning player to found
                // the town they are already standing in.
                //
                // The HOST was always safe here, because SteamLobby.loadingSave is a host-side flag:
                // the host set it in the save picker, took this branch, and skipped StartGame. A
                // guest's copy is false no matter how the save reached them, so every guest fell
                // through to StartGame instead. That is the "I could build on my old castle but it
                // still said place your castle" report: the kingdom was restored and perfectly
                // usable, with the new-game UI laid over the top of it.
                //
                // SaveTransfer.LoadingSave is the guest's equivalent. It goes true on the first
                // chunk and is only cleared when networking is torn down, so it still reads true by
                // the time the host presses Start and this handler runs.
                bool hostLoadedFromPicker = SteamLobby.loadingSave;
                if (hostLoadedFromPicker || SaveTransfer.LoadingSave)
                {
                    SteamLobby.loadingSave = false;

                    // The flag says "this session came from a save", but nothing until now checked
                    // that a save was actually READ. In a user log (Rednax, 2026-09-02) the host
                    // entered load-save mode twice and LoadSave.LoadAtPath never ran, so no bytes
                    // existed: both joiners were told there was nothing to send, the session started
                    // anyway, and the log still announced a loaded save. The host quit within a
                    // minute, twice, then gave up and hosted a fresh world instead.
                    //
                    // The session is still allowed to start, because refusing here would strand a
                    // host mid-flow with no way forward. But it must not claim a save was loaded when
                    // none was, and a joiner who received nothing is now stated outright rather than
                    // being left to discover it as an empty world.
                    // Only the host reads a file, so only the host can have failed to. A guest's
                    // world arrives over the wire and its bytes never touch LoadAtPath, so testing
                    // that here would accuse every guest of a host-side mistake.
                    bool haveSave = !hostLoadedFromPicker
                                 || (Main.LoadSaveLoadAtPathHook.saveData != null
                                     && Main.LoadSaveLoadAtPathHook.saveData.Length > 0);

                    GameState.inst.SetNewMode(GameState.inst.playingMode);

                    // A guest's world arrives as bytes off the network rather than through the
                    // save picker, so it is worth asking what the game's own load path does that
                    // this one does not. The answer, tested, is: nothing that should be repeated
                    // here.
                    //
                    // SetupInitialPathCosts, CombineStone and GenerateStoneUIs were all called at
                    // this point for a while, on the theory that a guest skipping StartGame had
                    // missed them. The theory was wrong, and running them did real damage:
                    // GenerateStoneUIs re-created the "Stone" markers that only ever belong to a
                    // world where no keep has been placed yet (Keep.OnBuildingPlacement is what
                    // destroys them, and a restored kingdom's keep is placed before this runs, so
                    // nothing cleared them again), and the pathing reset took the roads with it.
                    // They are deliberately NOT called. A loaded world already has all three.
                    if (!hostLoadedFromPicker)
                    {
                        // The build menu's pictures are 3D models on the UI layer, so they need a
                        // camera drawing that layer. Report its state rather than guess at it.
                        Main.LogBuildMenuState("guest finished loading a saved world");

                        LookAtOurOwnKeep();
                    }

                    // A loaded save used to drop straight into a running world. The fresh-world path
                    // below has always paused on entry (twice, SetNewMode re-asserts a speed of its
                    // own, so pausing only beforehand does not stick); this branch returned before
                    // reaching it. Everyone lands paused now, whichever way the session started.
                    if (haveSave)
                    {
                        Main.PauseGame("save loaded");
                    }
                    else
                    {
                        NetLog.Warn("session started in a load-a-save lobby but NO save was ever read " +
                                    "(LoadSave.LoadAtPath never ran, so there were no bytes to send). " +
                                    "Anyone who joined this lobby received no world. Leave and host " +
                                    "again, choosing a save in the picker.");
                        Main.PauseGame("started from a save lobby with no save loaded");
                    }
                    return;
                }

                SpeedControlUI.inst.SetSpeed(0);

                // Prefer the game's own private StartGame, which does more setup than we can
                // replicate: it enters playing mode, then runs SetupInitialPathCosts, CombineStone
                // and GenerateStoneUIs, none of which we want to reimplement.
                //
                // It always throws partway through in multiplayer, and that is expected rather than
                // broken. Its last act is to build the rival-kingdom config by reading
                // RivalKingdomSettingsUI.inst.rivalItems, and that screen is never shown here, so
                // the field is null. Everything the world needs has already run by then; only the
                // AI kingdom setup is lost, which is exactly what this mode does not want.
                //
                // So the catch is the normal path, not an error, and it is logged as such. The
                // inner exception is unwrapped because reflection reports every failure as the same
                // useless "Exception has been thrown by the target of an invocation": if this ever
                // starts failing for a DIFFERENT reason, the log has to be able to say so.
                bool inPlayMode = false;
                try
                {
                    MethodInfo method = typeof(MainMenuMode).GetMethod(
                        "StartGame", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (method != null)
                    {
                        method.Invoke(GameState.inst.mainMenuMode, null);
                        inPlayMode = true;
                    }
                }
                catch (Exception ex)
                {
                    Exception cause = ex.InnerException ?? ex;

                    // StartGame enters playing mode before it can throw, so if we got there the
                    // world is already up and re-entering the mode would run its setup twice.
                    inPlayMode = GameState.inst.IsPlayMode();

                    NetLog.Info("StartGame stopped at the rival-kingdom setup, as expected in "
                                + "multiplayer (" + cause.GetType().Name + ": " + cause.Message
                                + "); already in play mode: " + inPlayMode);
                }

                if (!inPlayMode)
                    GameState.inst.SetNewMode(GameState.inst.playingMode);

                SpeedControlUI.inst.SetSpeed(0);
            }
            catch (Exception ex) { NetLog.Error("session start", ex); }
        }

        /// <summary>
        /// Points the camera at THIS player's keep after a received world has loaded.
        ///
        /// A save carries the camera with it. FromContainer copies CameraSaveData across and
        /// base.Unpack restores it, which is right for the machine that wrote the save and wrong
        /// for every other one: the file was written by the HOST, so a guest finishes loading
        /// looking at the host's castle, on the host's island, with their own kingdom somewhere
        /// off screen. It reads as "the load put me in the wrong place", and it is the first
        /// thing a joining player sees.
        ///
        /// Only the view is corrected. The camera's saved position is the only thing being
        /// overridden, and only for a guest, so a host loading its own save still opens exactly
        /// where it left off.
        /// </summary>
        private static void LookAtOurOwnKeep()
        {
            try
            {
                if (Cam.inst == null || Player.inst == null || Player.inst.keep == null) return;

                Building keep = Player.inst.keep.GetComponent<Building>();
                if (keep == null) return;

                Cam.inst.SetTrackingPos(keep.GetPosition());
                NetLog.Info("camera moved to our own keep (the save arrived holding the host's view)");
            }
            catch (Exception ex) { NetLog.Error("pointing the camera at our own keep", ex); }
        }

        /// <summary>
        /// Places a player's starting keep on the given landmass, choosing the site by the
        /// same rules on every machine rather than being told a position. The map is already
        /// identical everywhere by this point, so the same rules reach the same cell.
        /// </summary>
        private static void ApplyKeepPlaceRandom(KeepPlaceRandomMessage m)
        {
            try
            {
                Building keep = UnityEngine.Object.Instantiate<Building>(
                    GameState.inst.GetPlaceableByUniqueName(World.keepName));
                keep.Init();

                Cell[] cells = World.inst.GetCellsData()
                    .Where(c => c.landMassIdx == m.LandmassIndex).ToArray();

                Cell site = ChooseKeepSite(cells, m.LandmassIndex);
                if (site == null)
                {
                    NetLog.Warn("keep place: no suitable site on landmass " + m.LandmassIndex);
                    return;
                }

                keep.transform.position = site.Position;
                keep.SendMessage("OnPlayerPlacement", SendMessageOptions.DontRequireReceiver);

                Player.inst.PlayerLandmassOwner.TakeOwnership(keep.LandMass());
                Player.inst.keep = keep.GetComponent<Keep>();
                Player.inst.RefreshVisibility(true);

                // Broadcast this one. We are inside a handler, so sends are normally
                // suppressed, but the keep is a new building every other machine needs.
                using (NetApply.Bypass())
                {
                    World.inst.Place(keep);
                    Cam.inst.SetTrackingPos(keep.GetPosition());
                }

                NetLog.Info("keep place: landmass " + m.LandmassIndex + " at " + site.x + "," + site.z);
            }
            catch (Exception ex) { NetLog.Error("keep place", ex); }
        }

        /// <summary>Half-width of the block that must be clear for a keep. 2 gives 5x5.</summary>
        private const int KeepFootprintRadius = 2;

        /// <summary>
        /// Picks a site for a player's starting keep: within 15 tiles of stone, at least 6
        /// tiles from water, and with a genuinely clear footprint.
        ///
        /// The footprint test is the part that matters. Checking only the centre cell, plus
        /// an "is there any clear cell within 4 tiles" test, which is true almost everywhere and
        /// so decides nothing, passes sites where a keep, which spans several tiles, lands in a
        /// boulder field.
        ///
        /// Tried at decreasing strictness, because a cramped landmass should still get a
        /// keep, no keep at all is far worse than an ugly one.
        /// </summary>
        private static Cell ChooseKeepSite(Cell[] cells, int landmassIdx)
        {
            // Best: a clear block, stone nearby, away from water.
            Cell site = FindSite(cells, KeepFootprintRadius, true, true);
            if (site != null) return site;

            // Then a smaller block, same surroundings.
            site = FindSite(cells, 1, true, true);
            if (site != null)
            {
                NetLog.Warn("keep place: no clear radius-" + KeepFootprintRadius +
                            " footprint on landmass " + landmassIdx + "; used radius 1");
                return site;
            }

            // Centre cell only, surroundings still required.
            site = FindSite(cells, 0, false, true);
            if (site != null)
            {
                NetLog.Warn("keep place: landmass " + landmassIdx +
                            " has no clear footprint anywhere; falling back to centre-cell only " +
                            "(the keep may overlap scenery)");
                return site;
            }

            // Genuinely last: any buildable cell, surroundings ignored.
            //
            // This tier is what makes the method total, and it is not decoration. The stone and
            // water rules apply to *every* tier above, and either can reject an entire landmass:
            // a small island has every cell within 6 tiles of water, and an island with no stone
            // fails the stone rule everywhere. Multiplayer forces island maps, so both are
            // ordinary rather than exotic, and the result was a player who started with no keep
            // at all, reported only as one warning line.
            site = FindSite(cells, 1, true, false) ?? FindSite(cells, 0, false, false);
            if (site != null)
            {
                NetLog.Warn("keep place: landmass " + landmassIdx +
                            " has no site meeting the stone/water rules; placing on the first " +
                            "buildable cell instead (stone may be far away)");
                return site;
            }

            return null;
        }

        /// <summary>
        /// First cell meeting the requested rules.
        ///
        /// <paramref name="requireSurroundings"/> covers "stone within 15, no water within 6".
        /// It has to be droppable: on a small island it can be false for every cell, and a
        /// search that cannot relax it returns nothing for the whole landmass.
        /// </summary>
        private static Cell FindSite(Cell[] cells, int radius, bool requireClearFootprint, bool requireSurroundings)
        {
            foreach (Cell cell in cells)
            {
                if (requireClearFootprint)
                {
                    if (!IsFootprintClear(cell.x, cell.z, radius)) continue;
                }
                else if (cell.Type != ResourceType.None || cell.deepWater) continue;

                if (requireSurroundings)
                {
                    if (!HasInRadius(cells, cell.x, cell.z, 15, ResourceType.Stone)) continue;
                    if (HasInRadius(cells, cell.x, cell.z, 6, ResourceType.Water)) continue;
                }

                return cell;
            }
            return null;
        }

        /// <summary>
        /// True when every cell in the square of the given radius is buildable, no resource,
        /// no deep water, and nothing already standing on it.
        ///
        /// Checks <c>OccupyingStructure</c> and <c>SubStructure</c> as well as the resource
        /// type, because decorative rock formations register as occupying structures rather
        /// than as harvestable stone. Testing the resource type alone is what let a keep land
        /// inside a boulder.
        ///
        /// <c>saltWater</c> is deliberately not tested. It marks which body of water a cell
        /// belongs to rather than whether the cell is water, so on a coastal island it is set
        /// on land as well, including it rejected every cell on some maps.
        /// </summary>
        private static bool IsFootprintClear(int cx, int cz, int radius)
        {
            for (int x = cx - radius; x <= cx + radius; x++)
            {
                for (int z = cz - radius; z <= cz + radius; z++)
                {
                    Cell c = World.inst.GetCellData(x, z);
                    if (c == null) return false;                        // off the map
                    if (c.Type != ResourceType.None) return false;      // trees, stone, ore, water
                    if (c.deepWater) return false;

                    if (c.OccupyingStructure != null && c.OccupyingStructure.Count > 0) return false;
                    if (c.SubStructure != null && c.SubStructure.Count > 0) return false;
                }
            }
            return true;
        }

        private static bool HasInRadius(Cell[] cells, int x, int z, int radius, ResourceType wanted)
        {
            for (int i = 0; i < cells.Length; i++)
            {
                Cell c = cells[i];
                if (c.x == x && c.z == z) continue;                       // not the centre
                if (c.Type != wanted) continue;

                // Deep or shallow water disqualifies a cell unless water is what we want.
                if (wanted != ResourceType.Water && (c.deepWater || c.Type == ResourceType.Water))
                    continue;

                int dx = c.x - x, dz = c.z - z;
                if (dx * dx + dz * dz <= radius * radius) return true;    // squared: no sqrt
            }
            return false;
        }

        private static void ApplyBuildPlace(BuildPlaceMessage m)
        {
            if (IsOwnEcho(m.Origin)) return;   // placer already has it

            SessionPlayer player;
            if (!NetPlayers.TryGet(m.Origin, "build place", out player)) return;
            if (player.inst == null) { NetLog.Warn("build place: player " + m.Origin + " has no inst"); return; }

            BuildingState s = m.State;

            Building prefab = GameState.inst.GetPlaceableByUniqueName(s.UniqueName);
            if (prefab == null)
            {
                // Roads and world terrain aren't in the placeable registry. Expected, not an error.
                return;
            }

            try
            {
                Building building = UnityEngine.Object.Instantiate<Building>(prefab);
                building.transform.position = s.GlobalPosition;

                // Building.Init reads the owner's livery. The BuildingPlayerReferencePatch
                // transpiler rewrites Player.inst inside Building methods to
                // GetPlayerByBuilding(this), which resolves to this remote player, whose
                // SetIndexedBanner may not have run yet, giving a null material crash.
                if (player.inst.PlayerLandmassOwner.bannerIdx < 0)
                {
                    try { player.inst.SetIndexedBanner(Mathf.Max(0, player.banner)); } catch { }
                }

                // Also swap Player.inst for the call, so any path Init takes that the
                // transpiler missed still sees the right player.
                Player original = Player.inst;
                try
                {
                    Player.inst = player.inst;
                    building.Init();
                }
                finally { Player.inst = original; }

                building.transform.SetParent(player.inst.buildingContainer.transform, true);

                Building.BuildingSaveData data = s.ToSaveData();
                data.Unpack(building);
                player.inst.AddBuilding(building);

                try
                {
                    player.inst.PlayerLandmassOwner.TakeOwnership(building.LandMass());
                    if (building.GetComponent<Keep>() != null &&
                        building.TeamID() == player.inst.PlayerLandmassOwner.teamId)
                        player.inst.keep = building.GetComponent<Keep>();
                }
                catch (Exception ex) { NetLog.Warn("build place: TakeOwnership, " + ex.Message); }

                World.inst.PlaceFromLoad(building);
                data.UnpackStage2(building);
                building.SetVisibleForFog(false);

                // Guard >= 0 as well as < Count: a road/bridge over water has LandMass() == -1, and
                // LandMassNames[-1] throws IndexOutOfRange (the same trap as the per-landmass building
                // registries). A building with no landmass simply has no landmass name to set.
                int lm = building.LandMass();
                if (lm >= 0 && player.inst.LandMassNames != null && lm < player.inst.LandMassNames.Count)
                {
                    player.inst.LandMassNames[lm] = player.kingdomName;
                    if (lm < Player.inst.LandMassNames.Count)
                        Player.inst.LandMassNames[lm] = player.kingdomName;
                }

                NetLog.Info("build place: " + player.id + " placed " + s.UniqueName +
                            " at " + building.transform.position);
            }
            catch (Exception ex) { NetLog.Error("build place " + s.UniqueName, ex); }
        }

        private static void ApplyBuildSnapshot(BuildSnapshotMessage m)
        {
            if (IsOwnEcho(m.Origin)) return;

            SessionPlayer player;
            if (!NetPlayers.TryGet(m.Origin, "build snapshot", out player)) return;
            if (player.inst == null) return;

            BuildingState s = m.State;

            Building building = player.inst.GetBuilding(s.Guid);
            if (building == null)
            {
                NetLog.Info("build snapshot: " + s.Guid + " not found");
                return;
            }

            try
            {
                building.UniqueName = s.UniqueName;
                building.customName = s.CustomName;
                building.transform.position = s.GlobalPosition;
                building.transform.GetChild(0).rotation = s.Rotation;
                building.transform.GetChild(0).localPosition = s.LocalPosition;

                // A building is COMMISSIONED by CompleteBuild, not by the value of its 'built'
                // field. CompleteBuild is the only thing that sends OnBuilt, registers the
                // building's IResourceProviders with FreeResourceManager, calls
                // Player.BuildingNowBuilt (which takes it off the landmass's unbuilt list and
                // recalculates max storage), creates its worker jobs through TryAddJobs, and
                // bakes its pathing.
                //
                // Writing 'built = true' by reflection did none of that, and then made it
                // unrecoverable: BuildingCompleteBuildHook skips CompleteBuild on a building
                // that already reports IsBuilt(), so the local simulation's own completion a
                // moment later was suppressed as a "duplicate" and the building stayed
                // uncommissioned for the rest of the session. That is the
                // "skipped duplicate CompleteBuild for farm (...)" line in the logs, and the
                // hook's own comment named this write as the first place to look.
                //
                // What that costs depends on how the building reached us. One that arrived
                // through ApplyBuildPlace has had its providers and jobs set up already, by
                // BuildingSaveData.UnpackStage2, so what it loses is OnBuilt, BuildingNowBuilt
                // and BakePathing: it stays on the landmass's unbuilt list, its storage is
                // never added to the kingdom's maximum, and the cells under it never get their
                // pathing costs baked. One that arrived by snapshot alone, with no placement
                // to unpack, loses the jobs and the FreeResourceManager registration as well,
                // which on a farm is a field with no HarvesterJob that nobody can harvest.
                //
                // Ship-launch pads were the first case of this to be noticed (a pad with no
                // ship) and were fixed narrowly; the cause was never specific to launch pads,
                // so every building goes through CompleteBuild now.
                //
                // CompleteBuild is idempotent thanks to that same hook, so this is safe in
                // either arrival order: if our own simulation finished the building first, the
                // snapshot finds IsBuilt() already true and does nothing.
                if (s.Built && !building.IsBuilt())
                {
                    building.CompleteBuild();
                }
                else if (!s.Built && building.IsBuilt())
                {
                    // Un-completing has no vanilla entry point, so the field write is all there
                    // is. Only reachable if we ran ahead of the owner and finished a building
                    // they still have under construction.
                    SetPrivateField(building, "built", false);
                }

                SetPrivateField(building, "placed", s.Placed);
                SetPrivateField(building, "resourceProgress", m.ResourceProgress);
                SetPrivateField(building, "yearBuilt", s.YearBuilt);

                building.Open = s.Open;
                building.doBuildAnimation = s.DoBuildAnimation;
                building.constructionPaused = s.ConstructionPaused;
                building.constructionProgress = s.ConstructionProgress;
                building.Life = s.Life;
                building.ModifiedMaxLife = s.ModifiedMaxLife;
                building.decayProtection = s.DecayProtection;
            }
            catch (Exception ex) { NetLog.Error("build snapshot " + s.Guid, ex); }
        }

        /// <summary>
        /// Sets a private instance field. The game keeps several building fields private
        /// with no setter, and a snapshot has to restore them exactly.
        /// </summary>
        private static void SetPrivateField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(
                fieldName, BindingFlags.NonPublic | BindingFlags.Instance);

            if (field == null)
            {
                NetLog.Warn("no private field '" + fieldName + "' on " + target.GetType().Name +
                            ", a game update may have renamed it");
                return;
            }

            field.SetValue(target, value);
        }

        // ---- Tier 3: ships, units, economy ----------------------------------

        /// <summary>True when a message we sent has come back to us and should be ignored.</summary>
        private static bool IsOwnEcho(ushort origin)
        {
            return origin == NetRouter.LocalClientId;
        }

        /// <summary>
        /// Removes an army that left the world on its owner's machine.
        ///
        /// Releases it and nothing else. The tempting call is Disband(), because that is usually
        /// what happened, but Disband hands every soldier back as a villager through Player.inst,
        /// which on this machine is the WRONG kingdom, and rolls SRand for the armament drop, which
        /// would pull the shared random sequence out of step with everyone else. The owner already
        /// did all of that on their own machine; here the army just has to stop existing.
        ///
        /// ReleaseArmy is the same call every death and disband funnels through, so it also takes
        /// the army out of OrdersManager, which is what stops a phantom degrading local pathing.
        /// </summary>
        /// <summary>
        /// Applies a battle outcome published by the machine arbitrating that fight.
        ///
        /// No own-echo guard is needed on the value itself, the arbiter is the only sender for a
        /// given army, but the sender must still ignore its own relayed copy or it would fight its
        /// own simulation a tick later.
        /// </summary>
        private static void ApplyArmyHealth(ArmyHealthMessage m)
        {
            if (IsOwnEcho(m.Origin)) return;   // we are the arbiter; this is our own report coming back

            using (NetApply.Scope())
                KaCMultiplayer.Combat.CombatSync.ApplyHealth(m);
        }

        private static void ApplyArmyDespawn(ArmyDespawnMessage m)
        {
            if (IsOwnEcho(m.Origin)) return;   // the owner already released it locally

            try
            {
                UnitSystem.Army army = UnitSystem.inst.FindArmyByGuid(m.Army);
                if (army == null)
                {
                    // Not an error worth shouting about: combat is not synced yet, so this machine
                    // may already have killed its own copy independently.
                    NetLog.Info("army despawn: " + m.Army + " not found, already gone here");
                    return;
                }

                // Scoped so the ReleaseArmy hook treats this as an applied change and does not
                // broadcast it straight back out.
                using (NetApply.Scope())
                    UnitSystem.inst.ReleaseArmy(army);

                NetLog.Info("army despawn: released " + m.Army);
            }
            catch (Exception ex) { NetLog.Error("army despawn", ex); }
        }

        private static void ApplyShipDespawn(ShipDespawnMessage m)
        {
            if (IsOwnEcho(m.Origin)) return;   // sender already removed it locally

            try
            {
                var ships = ShipSystem.inst.ships;
                for (int i = 0; i < ships.Count; i++)
                {
                    if (ships.data[i] != null && ships.data[i].guid == m.Ship)
                    {
                        UnityEngine.Object.Destroy(ships.data[i].gameObject);
                        NetLog.Info("ship despawn: removed " + m.Ship);
                        return;
                    }
                }
                NetLog.Info("ship despawn: " + m.Ship + " not found");
            }
            catch (Exception ex) { NetLog.Error("ship despawn", ex); }
        }

        private static void ApplyArmySpawn(ArmySpawnMessage m)
        {
            if (IsOwnEcho(m.Origin)) return;   // spawner already has it

            try
            {
                // Lets the spawn through BarracksTickForeignHook, which otherwise suppresses
                // a foreign player's barracks so the army is never created twice.
                Main.applyingRemoteArmySpawn = true;

                UnitSystem.Army army = UnitSystem.inst.MakeArmy(
                    m.Position, m.TeamId, (UnitSystem.ArmyType)m.ArmyType, true);

                if (army != null)
                {
                    // Adopt the spawner's guid so later move orders resolve to this army.
                    army.guid = m.Army;
                    NetLog.Info("army spawn: team " + m.TeamId + " type " +
                                (UnitSystem.ArmyType)m.ArmyType + " guid " + m.Army);
                }
            }
            catch (Exception ex) { NetLog.Error("army spawn", ex); }
            finally { Main.applyingRemoteArmySpawn = false; }
        }

        private static void ApplyHazardSpawn(HazardSpawnMessage m)
        {
            try
            {
                // Lets the placement past the client-side suppression hooks.
                Main.applyingWorldHazard = true;

                if (m.HazardType == 0) World.inst.AddWolfDen(m.X, m.Z);
                else if (m.HazardType == 1) World.inst.AddWitchHut(m.X, m.Z);
                else { NetLog.Warn("unknown hazard type " + m.HazardType); return; }

                NetLog.Info("hazard type " + m.HazardType + " at " + m.X + "," + m.Z);
            }
            catch (Exception ex) { NetLog.Error("hazard spawn", ex); }
            finally { Main.applyingWorldHazard = false; }
        }

        private static void ApplyEconomySnapshot(EconomySnapshotMessage m)
        {
            if (IsOwnEcho(m.Origin)) return;   // our own figures are already real

            SessionPlayer player;
            if (!NetPlayers.TryGet(m.Origin, "economy snapshot", out player)) return;

            try
            {
                ResourceAmount r = new ResourceAmount();
                r.Set(FreeResourceType.Wheat, m.Wheat);
                r.Set(FreeResourceType.Tree, m.Tree);
                r.Set(FreeResourceType.Stone, m.Stone);
                r.Set(FreeResourceType.Charcoal, m.Charcoal);
                r.Set(FreeResourceType.Gold, m.Gold);
                r.Set(FreeResourceType.IronOre, m.Iron);
                r.Set(FreeResourceType.Tools, m.Tools);
                r.Set(FreeResourceType.Armament, m.Armament);
                r.Set(FreeResourceType.Fish, m.Fish);
                r.Set(FreeResourceType.Apples, m.Apple);
                r.Set(FreeResourceType.Pork, m.Pork);

                // Applied straight onto the player. Nothing caches these separately; a second
                // copy would only be read back a frame later by something that could have read
                // the player.
                if (player.inst != null)
                    player.inst.resourcesTotal = r;
            }
            catch (Exception ex) { NetLog.Error("economy snapshot", ex); }
        }

        private static void ApplyKeepUpgrade(KeepUpgradeMessage m)
        {
            if (IsOwnEcho(m.Origin)) return;   // sender already upgraded it locally

            try
            {
                Building b = Main.FindBuildingByGuidAnywhere(m.Building);
                if (b == null)
                {
                    NetLog.Info("keep upgrade: " + m.Building + " not found");
                    return;
                }

                Upgradeable up = b.GetComponent<Upgradeable>();
                if (up == null)
                {
                    NetLog.Warn("keep upgrade: " + m.Building + " has no Upgradeable");
                    return;
                }

                // Only ever upward, so a late or duplicate message cannot downgrade.
                if (up.level < m.Level)
                {
                    up.SetUpgrade(m.Level);
                    NetLog.Info("keep upgrade: " + m.Building + " -> level " + m.Level);
                }
            }
            catch (Exception ex) { NetLog.Error("keep upgrade", ex); }
        }

        /// <summary>~3 units. Below this the villager is close enough to leave alone.</summary>
        private const float VillagerCorrectThresholdSqr = 9f;

        private static void ApplyVillagerSnapshot(VillagerSnapshotMessage m)
        {
            if (m.Villagers == null || m.Positions == null) return;

            try
            {
                int myTeam = -99999;
                try { myTeam = Player.inst.PlayerLandmassOwner.teamId; } catch { }

                Dictionary<Guid, Villager> lookup = new Dictionary<Guid, Villager>();
                var all = Villager.villagers;
                if (all != null)
                {
                    for (int i = 0; i < all.Count; i++)
                    {
                        Villager v = all.data[i];
                        if (v != null && !lookup.ContainsKey(v.guid))
                            lookup.Add(v.guid, v);
                    }
                }

                // Shorter of the two, so a truncated payload degrades instead of throwing.
                int n = Math.Min(m.Villagers.Count, m.Positions.Count);
                for (int i = 0; i < n; i++)
                {
                    Villager v;
                    if (!lookup.TryGetValue(m.Villagers[i], out v) || v == null) continue;

                    // Never correct our own villagers, ours are the authority locally.
                    try
                    {
                        LandmassOwner owner = World.GetLandmassOwner(v.landMass);
                        if (owner != null && owner.teamId == myTeam) continue;
                    }
                    catch { }

                    if ((v.GetPosition() - m.Positions[i]).sqrMagnitude > VillagerCorrectThresholdSqr)
                        v.TeleportTo(m.Positions[i]);
                }
            }
            catch (Exception ex) { NetLog.Error("villager snapshot", ex); }
        }

        /// <summary>
        /// Removes rubble that somebody else rebuilt over.
        ///
        /// Whatever is standing on the named cell is checked for actually being rubble before
        /// anything is removed. The replacement building arrives on its own BuildPlace message, and
        /// the two can land in either order, so by the time this runs the cell may already hold the
        /// NEW building. Vanishing that would delete the very thing the rebuild created.
        /// </summary>
        private static void ApplyRubbleClear(RubbleClearMessage m)
        {
            if (IsOwnEcho(m.Origin)) return;   // the rebuilder already cleared it locally

            try
            {
                Cell cell = World.inst.GetCellData(m.X, m.Z);
                if (cell == null) return;

                Building here = cell.TopMostStructure;
                if (here == null || here.UniqueName != "rubble")
                {
                    NetLog.Info("rubble clear: nothing to clear at " + m.X + "," + m.Z);
                    return;
                }

                using (NetApply.Scope())
                {
                    // Clears the rest of the same ruin first, then the piece we were handed.
                    World.inst.TryClearRelatedRubbles(here);
                    World.inst.VanishBuilding(here);
                }

                NetLog.Info("rubble clear: cleared at " + m.X + "," + m.Z);
            }
            catch (Exception ex) { NetLog.Error("rubble clear", ex); }
        }

        private static void ApplyTerrainDemolish(TerrainDemolishMessage m)
        {
            if (IsOwnEcho(m.Origin)) return;

            try
            {
                Building target = Main.FindBuildingByGuidAnywhere(m.Building);

                if (target == null)
                {
                    NetLog.Info("demolish: " + m.Building + " not found (already gone?)");
                    return;
                }

                // The game credits the demolition to Player.inst, so borrow the owner's
                // identity for the call and put it back afterwards no matter what.
                Player original = Player.inst;
                try
                {
                    Player owner = Main.GetPlayerByBuilding(target);
                    if (owner != null) Player.inst = owner;
                    World.inst.DemolishBuilding(target, false);
                }
                finally { Player.inst = original; }

                NetLog.Info("demolish: " + m.Building);
            }
            catch (Exception ex) { NetLog.Error("demolish", ex); }
        }

        /// <summary>
        /// Turns an order message back into the thing it named.
        ///
        /// Falls back to the carried position whenever the entity cannot be found, which is a
        /// normal outcome rather than an error: combat is not synced, so the target may already be
        /// dead on this machine. Walking to where it was beats dropping the order and leaving the
        /// unit standing still while its owner watches it fight on their screen.
        /// </summary>
        private static IMoveTarget ResolveMoveTarget(ShipMoveMessage m)
        {
            Cell cell = World.inst.GetCellData(m.X, m.Z);   // Cell is an IMoveTarget

            switch (m.TargetKind)
            {
                case MoveTargetKind.Army:
                {
                    UnitSystem.Army army = UnitSystem.inst.FindArmyByGuid(m.Target);
                    if (army != null) return army;
                    break;
                }
                case MoveTargetKind.Building:
                {
                    // Buildings are looked up across every player: the target of an attack belongs
                    // to somebody else by definition, and Player.GetBuilding only ever sees one
                    // kingdom's own buildings.
                    Building b = Main.FindBuildingByGuidAnyPlayer(m.Target);
                    if (b != null) return b;
                    break;
                }
                case MoveTargetKind.SiegeCatapult:
                {
                    SiegeCatapult c = Main.FindSiegeCatapultByGuid(m.Target);
                    if (c != null) return c;
                    break;
                }
                case MoveTargetKind.Ship:
                {
                    var ships = ShipSystem.inst.ships;
                    for (int i = 0; i < ships.Count; i++)
                    {
                        ShipBase sb = ships.data[i];
                        if (sb == null || sb.guid != m.Target) continue;

                        // Only some ships can be ordered at. Anything else falls through to the
                        // position, rather than being handed over as a target the game will reject.
                        IMoveTarget asTarget = sb as IMoveTarget;
                        if (asTarget != null) return asTarget;
                        break;
                    }
                    break;
                }
            }

            if (m.TargetKind != MoveTargetKind.Cell)
                NetLog.Info("order: " + m.TargetKind + " target " + m.Target +
                            " not found here, falling back to position " + m.X + "," + m.Z);

            return cell;
        }

        private static void ApplyShipMove(ShipMoveMessage m)
        {
            if (IsOwnEcho(m.Origin)) return;   // sender already issued the order locally

            try
            {
                // Resolve what was actually ordered. An entity target makes this an attack or a
                // boarding rather than a walk, and re-issuing it as a plain position order would
                // quietly turn every attack into a stroll to where the enemy was standing.
                IMoveTarget target = ResolveMoveTarget(m);
                if (target == null) return;

                var ships = ShipSystem.inst.ships;
                for (int i = 0; i < ships.Count; i++)
                {
                    if (ships.data[i] == null || ships.data[i].guid != m.Unit) continue;

                    ShipBase ship = ships.data[i];
                    IMoveableUnit unit = ship as IMoveableUnit;

                    // Player-controlled ships go through OrdersManager; route ships such as
                    // the player merchant aren't IMoveableUnit and move directly.
                    if (unit != null) OrdersManager.inst.MoveTo(unit, target);
                    else ship.MoveTo(target, false);

                    NetLog.Info("ship order: " + ship.type + " " + m.Unit + " -> " +
                                m.TargetKind + " at " + m.X + "," + m.Z);
                    return;
                }

                // Armies share the guid space and are commanded the same way. UnitSystem offers
                // the lookup, so use it rather than walking the list again here.
                UnitSystem.Army army = UnitSystem.inst.FindArmyByGuid(m.Unit);
                if (army != null)
                {
                    OrdersManager.inst.MoveTo(army, target);
                    NetLog.Info("army order: " + army.armyType + " " + m.Unit + " -> " +
                                m.TargetKind + " at " + m.X + "," + m.Z);
                    return;
                }

                // Siege catapults are commanded the same way and share the guid space, so they
                // are the last place a move order can land before it is genuinely unresolvable.
                SiegeCatapult movingCatapult = Main.FindSiegeCatapultByGuid(m.Unit);
                if (movingCatapult != null)
                {
                    OrdersManager.inst.MoveTo(movingCatapult, target);
                    NetLog.Info("catapult order: " + m.Unit + " -> " +
                                m.TargetKind + " at " + m.X + "," + m.Z);
                    return;
                }

                NetLog.Info("move: unit " + m.Unit + " not found as ship, army or catapult");
            }
            catch (Exception ex) { NetLog.Error("ship move", ex); }
        }

        private static void ApplyShipHealth(ShipHealthMessage m)
        {
            if (IsOwnEcho(m.Origin)) return;   // we are the arbiter; this is our own report coming back

            using (NetApply.Scope())
                KaCMultiplayer.Combat.CombatSync.ApplyShipHealth(m);
        }

        private static void ApplyMerchantTrade(MerchantTradeMessage m)
        {
            if (IsOwnEcho(m.Origin)) return;   // the trader already applied it locally

            try
            {
                ResourceAmount goods = TradeMath.FromList(m.Resources);

                IMerchant merchant = null;
                var ships = ShipSystem.inst.ships;
                for (int i = 0; i < ships.Count; i++)
                {
                    if (ships.data[i] != null && ships.data[i].guid == m.Merchant)
                    {
                        merchant = ships.data[i] as IMerchant;
                        break;
                    }
                }

                Stockpile dockStore = null;
                Building dockBuilding = Main.FindBuildingByGuidAnywhere(m.Dock);
                if (dockBuilding != null)
                {
                    Dock dock = dockBuilding.GetComponent<Dock>();
                    if (dock != null) dockStore = dock.loadingStorageComponent;
                }

                LandmassOwner buyer = World.GetLandmassOwnerByTeamId(m.BuyerTeam);
                LandmassOwner seller = World.GetLandmassOwnerByTeamId(m.SellerTeam);

                // One rule, shared with the machine that sent this, so the two can never settle
                // different figures for the same trade. See Trade/TradeMath.cs.
                //
                // Only the gold comes from the delta table. The hold and stockpile APIs take a
                // positive amount plus a direction rather than a signed delta, so IsBuy still
                // picks the call; it is the gold, where vanilla itself gets the two halves wrong,
                // that benefits from a single definition.
                TradeDeltas deltas = TradeMath.Compute(m.IsBuy, goods, m.Cost);

                if (m.IsBuy)
                {
                    if (merchant != null) merchant.RemoveFromHold(goods);
                    if (dockStore != null) dockStore.Deposit(ref goods);
                }
                else
                {
                    if (merchant != null) merchant.AddToHold(goods);
                    if (dockStore != null) ((IResourceStorage)dockStore).RemoveResources(goods);
                }

                if (buyer != null) buyer.Gold += deltas.BuyerGoldDelta;
                if (seller != null) seller.Gold += deltas.SellerGoldDelta;

                NetLog.Info("merchant " + (m.IsBuy ? "buy" : "sell") + " cost=" + m.Cost +
                            " buyer=" + m.BuyerTeam + " seller=" + m.SellerTeam +
                            " | merchant=" + (merchant != null) + " dock=" + (dockStore != null));
            }
            catch (Exception ex) { NetLog.Error("merchant trade", ex); }
        }

        /// <summary>
        /// Applies a relation change on this machine.
        ///
        /// No own-echo guard, deliberately: the sender does not apply locally before sending, so
        /// its own copy arriving back is the first and only time it applies. PlayerRelations.Set is
        /// idempotent, an unchanged relation returns before doing any of the work, so a duplicate
        /// costs a log line and nothing else.
        /// </summary>
        private static void ApplyPlayerRelation(PlayerRelationMessage m)
        {
            try
            {
                // Every machine runs the SAME rule over the same message, so all of them reach
                // the same answer without a second round trip: an alliance needs both sides to
                // have asked, a war starts only after its notice period, and just the outcome of
                // that reasoning is applied here.
                World.Relations? now = PlayerRelations.RequestFrom(
                    m.TeamA, m.TeamB, (World.Relations)m.Relation);

                if (now.HasValue) PlayerRelations.Set(m.TeamA, m.TeamB, now.Value);
            }
            catch (Exception ex) { NetLog.Error("player relation", ex); }
        }

        // ---- Tier 2: world events -------------------------------------------
        //
        // Every one of these applies a change by calling the same game method that a
        // local player's action would, and those methods are Harmony-patched to broadcast.
        // So each apply must happen inside NetApply.Scope(), or the patch treats it as a
        // fresh local action and sends it straight back out.

        private static void ApplyTreeFell(TreeFellMessage m)
        {
            using (NetApply.Scope())
            {
                Cell cell = World.inst.GetCellData(m.X, m.Z);
                if (cell == null)
                {
                    NetLog.Warn("tree fell: no cell at " + m.X + "," + m.Z);
                    return;
                }
                TreeSystem.inst.FellTree(cell, m.Index);
            }
        }

        private static void ApplyTreeShake(TreeShakeMessage m)
        {
            using (NetApply.Scope())
                TreeSystem.inst.ShakeTree(m.Index);
        }

        private static void ApplyTreeGrow(TreeGrowMessage m)
        {
            using (NetApply.Scope())
            {
                Cell cell = World.inst.GetCellData(m.X, m.Z);
                if (cell == null)
                {
                    NetLog.Warn("tree grow: no cell at " + m.X + "," + m.Z);
                    return;
                }
                TreeSystem.inst.GrowTree(cell);
            }
        }

        private static void ApplyWeather(WeatherSetMessage m)
        {
            using (NetApply.Scope())
            {
                NetLog.Info("weather -> " + m.WeatherType);
                Weather.CurrentWeather = (Weather.WeatherType)m.WeatherType;
            }
        }

        private static void ApplyDragonSpawn(DragonSpawnMessage m)
        {
            Vector3 at = new Vector3(m.X, m.Y, m.Z);
            NetLog.Info("dragon " + m.Kind + " at " + at);

            using (NetApply.Scope())
            {
                switch (m.Kind)
                {
                    case DragonKind.Siege: DragonSpawn.inst.SpawnSiegeDragon(at); break;
                    case DragonKind.Mama: DragonSpawn.inst.SpawnMamaDragon(at); break;
                    case DragonKind.Baby: DragonSpawn.inst.SpawnBabyDragon(at); break;
                    case DragonKind.Visiting: DragonSpawn.inst.SpawnBabyDragonToVisit(at); break;
                    default: NetLog.Warn("unknown dragon kind " + (int)m.Kind); break;
                }

                // Adopt the spawner's id for our copy, the same way an arriving villager adopts
                // theirs. Each machine's spawn rolls a fresh Guid of its own, so without this the
                // same dragon has a different name everywhere and nothing about it, damage or death,
                // can be reported afterwards.
                if (m.Dragon != Guid.Empty)
                {
                    Dragon spawned = Main.NewestDragon();
                    if (spawned != null) spawned.id = m.Dragon;
                    else NetLog.Warn("dragon spawn: nothing appeared to adopt id " + m.Dragon);
                }
            }
        }

        /// <summary>
        /// Kills the local copy of a villager the arbiter has declared dead.
        ///
        /// The death is applied through the game's own <c>Player.DestroyPerson</c> rather than by
        /// hiding or detaching the villager, so everything that normally follows a death still
        /// happens here: the job is released, the home loses its occupant, and the corpse, if the
        /// arbiter left one, is created by the same code that would have created it locally.
        ///
        /// Inside a <see cref="NetApply.Scope"/> so the local kill does not broadcast a death of
        /// its own and bounce the message back around the session.
        ///
        /// A villager we cannot find is not an error worth shouting about. It means this machine
        /// had already lost them, which is the state the message was asking for anyway.
        /// </summary>
        /// <summary>
        /// Lights a fire the arbiter started, on the cell they named.
        ///
        /// Runs the game's own StartFireAt so the fire behaves, spreads and can be fought exactly
        /// as a local one, inside an apply scope so our own hook does not announce it back.
        /// StartFireAt decides for itself whether a cell can burn at all, so a cell that is already
        /// alight or cannot catch simply returns nothing, which makes this safe to receive twice.
        /// </summary>
        private static void ApplyFireStart(FireStartMessage m)
        {
            if (m.Origin == NetRouter.LocalClientId) return;

            using (NetApply.Scope())
            {
                try
                {
                    Cell cell = World.inst.GetCellData(m.X, m.Z);
                    if (cell == null) return;

                    FireManager.inst.StartFireAt(cell);
                }
                catch (Exception ex) { NetLog.Error("fire start", ex); }
            }
        }

        /// <summary>
        /// Destroys a building the arbiter says is gone, leaving rubble.
        ///
        /// Runs the game's own WreckBuilding rather than removing the building ourselves, so the
        /// ruins, the cell bookkeeping and everything WreckBuilding notifies all happen exactly as
        /// they do for the machine that decided it. Inside an apply scope, which is also what stops
        /// our own hook announcing it straight back out.
        ///
        /// A building we cannot find is not an error: it means this machine had already lost it,
        /// which is the state being asked for.
        /// </summary>
        private static void ApplyBuildingWrecked(BuildingWreckedMessage m)
        {
            if (m.Origin == NetRouter.LocalClientId) return;

            using (NetApply.Scope())
            {
                try
                {
                    Building b = Main.FindBuildingByGuidAnywhere(m.Building);
                    if (b == null) return;

                    World.inst.WreckBuilding(b);
                    NetLog.Info("building wrecked: " + m.Building);
                }
                catch (Exception ex) { NetLog.Error("building wrecked", ex); }
            }
        }

        /// <summary>
        /// Applies a building's arbitrated health, inside an apply scope so the local damage this
        /// causes is not broadcast straight back out.
        /// </summary>
        private static void ApplyBuildingHealth(BuildingHealthMessage m)
        {
            if (m.Origin == NetRouter.LocalClientId) return;

            using (NetApply.Scope())
            {
                KaCMultiplayer.Combat.CombatSync.ApplyBuildingHealth(m);
            }
        }

        /// <summary>
        /// Moves a villager into the house their owner put them in.
        ///
        /// Also takes them off the Homeless list, which nothing else would: our own VillagerAdd
        /// handler puts every arriving villager there, since at that moment nobody has told us where
        /// they live. The game's own settling path never files them as homeless at all, so this is
        /// undoing our own placeholder rather than fighting the game.
        ///
        /// A missing villager or a missing house is not worth shouting about. Either can legitimately
        /// arrive before the thing it refers to, and the state will be carried by the next snapshot.
        /// </summary>
        private static void ApplyVillagerHome(VillagerHomeMessage m)
        {
            if (m.Origin == NetRouter.LocalClientId) return;

            using (NetApply.Scope())
            {
                try
                {
                    Villager v = FindVillagerByGuid(m.Villager);
                    if (v == null) return;

                    Building b = Main.FindBuildingByGuidAnywhere(m.Home);
                    if (b == null) return;

                    Home home = b.GetComponent<Home>();
                    if (home == null) return;

                    foreach (SessionPlayer sp in Main.kCPlayers.Values)
                    {
                        if (sp == null || sp.inst == null || sp.inst.Homeless == null) continue;
                        sp.inst.Homeless.RemoveSwap(v);   // ArrayExt, not a List: no Remove(item)
                    }

                    v.SetHome(home);
                }
                catch (Exception ex)
                {
                    NetLog.Error("villager home", ex);
                }
            }
        }

        private static void ApplyVillagerDeath(VillagerDeathMessage m)
        {
            // Skip our own echo here rather than at the relay, matching the other relayed messages.
            if (m.Origin == NetRouter.LocalClientId) return;

            using (NetApply.Scope())
            {
                try
                {
                    Villager target = FindVillagerByGuid(m.Villager);
                    if (target == null) return;

                    Player.inst.DestroyPerson(target, m.LeaveBody);
                }
                catch (Exception ex)
                {
                    NetLog.Error("villager death", ex);
                }
            }
        }

        /// <summary>
        /// Finds a villager by the id every machine agrees on.
        ///
        /// Bounded by Count and null-checked, NOT a LINQ pass over <c>.data</c>. ArrayExt.data is
        /// the backing ARRAY and is longer than Count: unfilled capacity is null, and RemoveAtSwap
        /// decrements Count while leaving a stale reference sitting past it, so walking the array
        /// either dereferences a null or matches a villager who has already been removed.
        /// </summary>
        private static Villager FindVillagerByGuid(Guid id)
        {
            var all = Villager.villagers;
            if (all == null) return null;

            for (int i = 0; i < all.Count; i++)
            {
                Villager v = all.data[i];
                if (v != null && v.guid == id) return v;
            }
            return null;
        }

        /// <summary>
        /// Builds our copy of a siege catapult somebody else just trained.
        ///
        /// Built the same way the game builds one, prefab and all, so the copy is a real catapult
        /// rather than a decoration: it paths, it targets, it takes damage and it can be attacked.
        /// Only its id is imposed from outside, because SiegeCatapult's constructor rolls a fresh
        /// Guid and without adopting the spawner's there would be no shared name for the same
        /// machine and nothing about it could be reported afterwards.
        ///
        /// Init(teamId) is what makes it belong to the right kingdom, which is what the whole
        /// landmass-authority model then reads to decide who resolves its fights.
        /// </summary>
        private static void ApplySiegeCatapultSpawn(SiegeCatapultSpawnMessage m)
        {
            if (IsOwnEcho(m.Origin)) return;   // our own catapult, built here by the game already

            try
            {
                if (Main.FindSiegeCatapultByGuid(m.Catapult) != null) return;   // already have it

                // Both places the game spawns a catapult use RaiderSystem's prefab, oddly enough,
                // including the barracks. SiegeCatapultSystem carries one too, so fall back to it
                // rather than dropping the spawn if the raider system is not up.
                GameObject prefab = (RaiderSystem.inst != null) ? RaiderSystem.inst.siegeCatapultPrefab : null;
                if (prefab == null && SiegeCatapultSystem.inst != null)
                    prefab = SiegeCatapultSystem.inst.siegeCatapultPrefab;

                if (prefab == null) { NetLog.Warn("catapult spawn: no prefab to build " + m.Catapult); return; }

                using (NetApply.Scope())
                {
                    Vector3 at = new Vector3(m.X, m.Y, m.Z);

                    GameObject go = UnityEngine.Object.Instantiate<GameObject>(prefab);
                    go.transform.position = at;

                    SiegeCatapult c = go.GetComponent<SiegeCatapult>();
                    if (c == null) { NetLog.Warn("catapult spawn: prefab has no SiegeCatapult"); return; }

                    c.Init(m.TeamId);
                    c.guid = m.Catapult;

                    // SetPos is the game's own way of placing one after Init, and it is what the
                    // barracks does. Without it the catapult reports a position of zero until its
                    // first Tick, and a position of zero is a different island as far as the
                    // authority rule is concerned.
                    c.SetPos(at);

                    NetLog.Info("catapult spawn: " + m.Catapult + " team " + m.TeamId + " at " + at);
                }
            }
            catch (Exception ex) { NetLog.Error("catapult spawn", ex); }
        }

        /// <summary>Adopts the streamer effect set another player's audience chose.</summary>
        private static void ApplyStreamerEffects(StreamerEffectsMessage m)
        {
            if (IsOwnEcho(m.Origin)) return;   // our own audience; already applied here

            using (NetApply.Scope())
                StreamerEffectSync.Apply(m);
        }

        /// <summary>Moves our copies of another player's armies to where they say those armies are.</summary>
        private static void ApplyArmyPositions(ArmyPositionsMessage m)
        {
            if (IsOwnEcho(m.Origin)) return;   // our own armies; we are the authority on them

            using (NetApply.Scope())
                KaCMultiplayer.Combat.ArmyPositionSync.Apply(m);
        }

        /// <summary>Applies a wolf den's arbitrated pack health, inside an apply scope so nothing
        /// the assignment sets off is announced straight back out.</summary>
        private static void ApplyWolfPackHealth(WolfPackHealthMessage m)
        {
            if (IsOwnEcho(m.Origin)) return;   // we are the arbiter; this is our own report coming back

            using (NetApply.Scope())
                KaCMultiplayer.Combat.CombatSync.ApplyWolfPackHealth(m);
        }

        /// <summary>Applies a catapult's arbitrated life, inside an apply scope so the local damage
        /// and the death it may cause are not announced straight back out.</summary>
        private static void ApplySiegeCatapultHealth(SiegeCatapultHealthMessage m)
        {
            if (IsOwnEcho(m.Origin)) return;   // we are the arbiter; this is our own report coming back

            using (NetApply.Scope())
                KaCMultiplayer.Combat.CombatSync.ApplyCatapultHealth(m);
        }

        /// <summary>
        /// Removes our copy of a catapult its owner disbanded.
        ///
        /// Through the game's own Release, which is the single choke point that also takes the unit
        /// out of OrdersManager. Skipping that is what leaves a destroyed unit behind as a pathing
        /// obstacle, the lesson armies taught. A catapult we cannot find is not an error: it means
        /// this machine never had one, or already lost it in a fight.
        /// </summary>
        private static void ApplySiegeCatapultDespawn(SiegeCatapultDespawnMessage m)
        {
            if (IsOwnEcho(m.Origin)) return;   // we disbanded it; it is already gone here

            try
            {
                SiegeCatapult c = Main.FindSiegeCatapultByGuid(m.Catapult);
                if (c == null) return;

                using (NetApply.Scope())
                {
                    c.Release();
                    NetLog.Info("catapult despawn: " + m.Catapult);
                }
            }
            catch (Exception ex) { NetLog.Error("catapult despawn", ex); }
        }

        /// <summary>Applies a dragon's arbitrated health, inside an apply scope so the local damage
        /// this causes is not announced straight back out.</summary>
        private static void ApplyDragonHealth(DragonHealthMessage m)
        {
            if (m.Origin == NetRouter.LocalClientId) return;

            using (NetApply.Scope())
            {
                KaCMultiplayer.Combat.CombatSync.ApplyDragonHealth(m);
            }
        }

        private static void ApplyVillagerWarp(VillagerWarpMessage m)
        {
            // Skip our own echo here rather than at the relay: this message goes out with
            // RelayIncludingSender, which other receivers depend on.
            if (m.Origin == NetRouter.LocalClientId) return;

            using (NetApply.Scope())
            {
                try
                {
                    // Bounded by Count and null-checked, NOT a LINQ pass over .data.
                    //
                    // ArrayExt.data is the backing ARRAY and is longer than Count: unfilled capacity
                    // is null, and RemoveAtSwap decrements Count while leaving a stale reference
                    // sitting past it. Walking .data therefore either dereferences a null, which is
                    // what this handler was doing and why a warp could fail with an error instead of
                    // moving anybody, or matches a villager that has already been removed and warps
                    // a corpse. Same loop shape as ApplyVillagerSnapshot, for the same reason.
                    Villager target = null;
                    var all = Villager.villagers;
                    if (all != null)
                    {
                        for (int i = 0; i < all.Count; i++)
                        {
                            Villager v = all.data[i];
                            if (v == null || v.guid != m.Villager) continue;
                            target = v;
                            break;
                        }
                    }

                    if (target == null)
                    {
                        NetLog.Info("villager warp: no villager with guid " + m.Villager);
                        return;
                    }
                    target.TeleportTo(new Vector3(m.X, m.Y, m.Z));
                }
                catch (Exception ex)
                {
                    NetLog.Error("villager warp", ex);
                }
            }
        }

        /// <summary>
        /// Host side of the ready toggle. Reads the player's real state, flips it, and
        /// writes the result into the message so everyone, the clicker included, gets
        /// the host's answer rather than whatever the client guessed.
        /// </summary>
        private static void ToggleReady(PlayerReadyMessage m, NetContext ctx)
        {
            SessionPlayer player;
            if (!NetPlayers.TryGet(ctx.SenderId, "ready toggle", out player)) return;

            player.ready = !player.ready;
            m.IsReady = player.ready;

            NetLog.Info("ready: client " + ctx.SenderId + " -> " + m.IsReady);
            NetRouter.RelayIncludingSender(m, ctx);
        }

        private static void ApplyReady(PlayerReadyMessage m)
        {
            SessionPlayer player;
            if (!NetPlayers.TryGet(m.Origin, "ready state", out player)) return;
            player.ready = m.IsReady;
        }

        private static void ApplyBanner(BannerPickMessage m)
        {
            SessionPlayer player;
            if (!NetPlayers.TryGet(m.Origin, "banner pick", out player)) return;

            player.banner = m.Banner;

            // The recorded choice above is what matters and it is now safe. Everything below needs
            // the player's kingdom object, which does not exist yet for someone still joining, and
            // the chain through inst and PlayerLandmassOwner would throw on them. The banner is
            // re-applied from the roster once their kingdom is built, so deferring costs nothing.
            if (player.inst == null || player.inst.PlayerLandmassOwner == null)
            {
                NetLog.Info("banner: client " + m.Origin + " has no kingdom yet; recorded " +
                            m.Banner + " for when it does");
                return;
            }

            // Through Main so the repaint is asked for and so a throw out of vanilla's army
            // material refresh cannot abort the rest of this handler. Every flag in the world
            // subscribed to the LOCAL player's updateBanner delegate, so setting a REMOTE player's
            // banner repaints nothing on its own.
            Main.SetKingdomBanner(player.inst.PlayerLandmassOwner, m.Banner, "client " + m.Origin);

            // The full SetIndexedBanner also initializes the livery material. Without it
            // Building.Init hits a null material when we later place a building on this
            // player's behalf. Guarded because it is not critical to the banner showing.
            try { player.inst.SetIndexedBanner(m.Banner); }
            catch (Exception ex) { NetLog.Warn("SetIndexedBanner failed for client " + m.Origin + ": " + ex.Message); }

            NetLog.Info("banner: client " + m.Origin + " -> " + m.Banner);
        }

        /// <summary>
        /// Host side of a kingdom rename. The host keeps its own copy up to date so the
        /// roster broadcasts it builds are correct, then passes the message on.
        /// </summary>
        private static void RecordKingdomLabel(KingdomLabelMessage m, NetContext ctx)
        {
            SessionPlayer player;
            if (NetPlayers.TryGet(ctx.SenderId, "kingdom label", out player))
                player.kingdomName = m.KingdomName;

            NetRouter.RelayIncludingSender(m, ctx);
        }

        private static void ApplyKingdomLabel(KingdomLabelMessage m)
        {
            SessionPlayer player;
            if (!NetPlayers.TryGet(m.Origin, "kingdom label", out player)) return;

            player.kingdomName = m.KingdomName;
            NetLog.Info("kingdom: " + player.name + " -> " + m.KingdomName);
        }

        /// <summary>
        /// Rebuilds the local view of the lobby from the host's roster.
        ///
        /// Reconciles rather than replacing: after a save load the player records already
        /// exist locally, so blindly adding would throw on a duplicate key. Update what is
        /// there, create only what is genuinely new.
        /// </summary>
        private static void ApplyRoster(PeerRosterMessage m)
        {
            NetLog.Info("roster: " + (m.Players == null ? 0 : m.Players.Count) + " players");

            LobbyView.ClearPlayers();
            if (m.Players == null) return;

            int failed = 0;

            foreach (PeerRosterMessage.Entry e in m.Players)
            {
                // Guarded PER ENTRY, because the whole roster used to ride on every entry
                // succeeding. This handler has thrown before, on a half-built remote player, and the
                // throw did not merely lose that one player: it abandoned the loop, so everybody
                // after them was missing too. That is the "empty player list" symptom from the
                // remote-player Reset crash. One unusable entry should cost one player, not the
                // lobby.
                try
                {
                    // Before anything constructs a SessionPlayer: its constructor asks LoadIdentity
                    // for a team, and left to itself it would re-derive one from the client id.
                    // Taking the host's answer first is what stops two machines disagreeing about
                    // who owns which team after a client id has been recycled.
                    KaCMultiplayer.LoadSaveOverrides.LoadIdentity.AdoptAssignedTeam(e.SteamId, e.TeamId);

                    SessionPlayer existing;
                    if (Main.kCPlayers.TryGetValue(e.SteamId, out existing))
                    {
                        existing.id = e.ClientId;
                        existing.name = e.Name;
                        existing.ready = e.Ready;
                        existing.banner = e.Banner;
                        existing.kingdomName = e.KingdomName;
                    }
                    else
                    {
                        Main.kCPlayers.Add(e.SteamId, new SessionPlayer(e.Name, e.ClientId, e.SteamId)
                        {
                            name = e.Name,
                            ready = e.Ready,
                            banner = e.Banner,
                            kingdomName = e.KingdomName
                        });
                    }

                    Main.clientSteamIds[e.ClientId] = e.SteamId;

                    // A player whose kingdom object is not built yet still belongs in the roster and
                    // on the lobby list; only their banner has to wait. Chaining straight through
                    // inst and PlayerLandmassOwner is what made a half-built player fatal.
                    SessionPlayer sp = Main.kCPlayers[e.SteamId];
                    if (sp.inst != null && sp.inst.PlayerLandmassOwner != null)
                    {
                        // SetBannerIdx only refreshes the materials the LandmassOwner itself holds.
                        // The flags already standing in the world are painted from a copy they took
                        // earlier, and they listen to the local player, not to this one, so the
                        // repaint has to be asked for. Going through Main also stops a throw out of
                        // vanilla's army material refresh from costing this player their row.
                        Main.SetKingdomBanner(sp.inst.PlayerLandmassOwner, e.Banner, e.Name);
                    }
                    else
                        NetLog.Info("roster: " + e.Name + " has no kingdom yet; banner deferred");

                    LobbyView.AddPlayer(e.ClientId);
                }
                catch (Exception ex)
                {
                    failed++;
                    NetLog.Error("roster entry '" + e.Name + "' (" + e.SteamId + "), skipping them", ex);
                }
            }

            if (failed > 0)
                NetLog.Warn("roster: " + failed + " of " + m.Players.Count +
                            " entries could not be applied; the rest of the lobby is intact");
        }

        /// <summary>
        /// Renders a system notice in lobby chat. Logged for the same reason as every
        /// other handler here: reaching the handler and reaching the screen are different
        /// questions, and without a line the log can't tell them apart.
        /// </summary>
        private static void RenderChatNotice(ChatNoticeMessage m)
        {
            NetLog.Info("notice: " + m.Text);

            // Chat rows are instantiated into LobbyScreen.ChatContent, which belongs to the lobby
            // prefab and is gone once the session starts, so in-game these notices were reaching the
            // handler and then landing nowhere a player could see. The game's own event feed is the
            // right surface once play has begun; the lobby list is right before it.
            if (GameState.inst != null && GameState.inst.IsPlayMode())
            {
                // A distinct id per notice: TryLog suppresses a repeat of the same id inside its
                // interval, and these are one-off announcements rather than recurring warnings.
                KingdomLog.TryLog("mpNotice" + (noticeSeq++), m.Text, KingdomLog.LogStatus.Important, 0f);
                return;
            }

            LobbyView.AddChatNotice(m.Text);
        }

        /// <summary>Counter that keeps each in-game notice's KingdomLog id unique.</summary>
        private static int noticeSeq;

        /// <summary>
        /// Puts a host-sent modal on screen. Logged because the host usually disconnects
        /// us right afterwards, and knowing which notice arrived is the difference between
        /// a diagnosable refusal and an unexplained drop.
        /// </summary>
        private static void ShowNotice(NoticeMessage m)
        {
            NetLog.Info("notice from host: " + m.Title + ", " + m.Body);
            ModalDialog.Show(m.Title, m.Body);
        }

        /// <summary>
        /// Renders an incoming chat line. Logs first: whether a message reached the
        /// handler and whether it reached the screen are different questions, and without
        /// a line here a silent rendering failure is indistinguishable from success.
        /// </summary>
        private static void RenderChatLine(ChatSayMessage m)
        {
            NetLog.Info("chat from " + m.Origin + " (" + m.PlayerName + "): " + m.Text);

            // Same split as the notices above, and for the same reason: chat rows are instantiated
            // into the lobby prefab, which is gone once play starts, so a message sent mid-game
            // reached this handler and then landed nowhere anybody could see.
            if (GameState.inst != null && GameState.inst.IsPlayMode())
            {
                InGameChat.Add(m.PlayerName, m.Text, m.Origin == NetRouter.LocalClientId);
                return;
            }

            LobbyView.AddChatLine(m.Origin, m.PlayerName, m.Text);
        }

        /// <summary>
        /// Applies a speed change that came from another player. The guard flag stops the
        /// SpeedControlUI patch from treating our own call as a fresh local change and
        /// broadcasting it straight back out.
        /// </summary>
        private static void ApplyTimeScale(int speed, ushort origin)
        {
            // Two guards on purpose. NetApply is the general one; Main.applyingRemoteSpeed is
            // what the SpeedControlUI patch reads specifically. Both have to be set, or that
            // patch reads this as a local change and echoes it straight back out.
            using (NetApply.Scope())
            {
                try
                {
                    Main.applyingRemoteSpeed = true;
                    NetLog.Info("speed " + speed + " from client " + origin + ", applying");
                    SpeedControlUI.inst.SetSpeed(speed);
                }
                finally
                {
                    Main.applyingRemoteSpeed = false;
                }
            }
        }
    }
}
