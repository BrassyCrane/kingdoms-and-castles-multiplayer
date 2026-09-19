namespace KaCMultiplayer.Net
{
    /// <summary>
    /// Wire identifier for every message this mod sends. Values are grouped into fixed
    /// bands so a new message in an existing subsystem never has to disturb the others,
    /// and an unknown id read off the wire can still be attributed to a subsystem when
    /// logging.
    ///
    /// The space starts at 1000, which leaves the low ids free and makes an id obviously a
    /// message id at a glance in a log. Do not compact it: renumbering breaks the wire contract
    /// for no gain.
    ///
    /// These values are part of that contract: change one and old clients desync. Append
    /// within a band, never renumber.
    /// </summary>
    public enum NetMessageId : ushort
    {
        None = 0,

        // 1000-1019  session lifecycle
        Handshake = 1001,
        ClientJoined = 1002,
        PeerRoster = 1003,
        SessionStart = 1004,
        WorldSeed = 1005,
        SaveTransfer = 1006,
        Notice = 1007,

        /// <summary>A joiner naming the save chunks it still needs. See SaveTransfer.</summary>
        SaveResend = 1008,

        // 1020-1039  lobby
        ChatSay = 1020,
        ChatNotice = 1021,
        LobbySettings = 1022,
        ReadyState = 1023,
        BannerPick = 1024,
        KingdomLabel = 1025,

        // 1040-1059  world and terrain
        TerrainPlace = 1040,
        TerrainDemolish = 1041,
        TimeScale = 1042,
        WeatherSet = 1043,
        HazardSpawn = 1044,
        TreeFell = 1050,
        TreeShake = 1051,
        TreeGrow = 1052,

        // 1060-1079  buildings
        BuildPlace = 1060,
        BuildProgress = 1061,
        BuildFinish = 1062,
        BuildSnapshot = 1063,
        KeepPlaceRandom = 1064,
        KeepUpgrade = 1065,
        RubbleClear = 1066,
        BuildingHealth = 1067,
        BuildingWrecked = 1068,
        FireStart = 1069,

        // 1080-1089  economy
        EconomySnapshot = 1080,
        TaxRate = 1081,

        // 1090-1109  population
        VillagerAdd = 1090,
        VillagerWarp = 1091,
        VillagerSnapshot = 1092,
        WorkerSeed = 1093,
        VillagerDeath = 1094,
        VillagerHome = 1095,
        ArmySpawn = 1100,
        ArmyDespawn = 1101,
        ArmyHealth = 1102,
        ArmyPositions = 1103,

        // 1110-1119  shipping
        ShipMove = 1110,
        ShipDespawn = 1111,
        MerchantTrade = 1112,
        ShipHealth = 1113,

        /// <summary>What a kingdom charges for its exports. See Trade/ExportPrices.cs.</summary>
        ExportPrices = 1114,

        // 1120-1129  dragons
        // One id for all three kinds, the kind travels in the payload, since the three spawns
        // differ only in which method the receiver calls.
        DragonSpawn = 1120,
        DragonHealth = 1121,
        DragonFlight = 1122,

        // 1130-1139  diplomacy
        PlayerRelation = 1130,
        DiplomacyDeal = 1131,

        // 1132 was MenuPause, a shared gate that stopped the world while anyone had a menu open.
        // Removed: it stopped and started the simulation on events that were not part of the game
        // and desynced more than it saved. The number is left unused rather than reassigned, so a
        // copy of the mod that predates this cannot be handed a different message under its id.

        // 1140-1149  siege catapults
        // Catapults are built from a barracks, and that barracks only ticks on its owner's
        // machine, so a catapult used to exist on exactly one machine in the session. The spawn
        // carries the id every machine then uses to talk about it; health is the arbiter's verdict
        // on a fight; despawn covers the one death that is not a fight, namely being disbanded.
        SiegeCatapultSpawn = 1140,
        SiegeCatapultHealth = 1141,
        SiegeCatapultDespawn = 1142,

        // 1150-1159  wildlife
        WolfPackHealth = 1150,

        // 1160-1169  streamer effects
        StreamerEffects = 1160,
    }

    public static class NetMessageIdExtensions
    {
        /// <summary>Band name for an id, used when logging an unrecognised message.</summary>
        public static string Band(this NetMessageId id)
        {
            ushort v = (ushort)id;
            if (v == 0) return "none";
            if (v < 1000) return "legacy";
            if (v < 1020) return "session";
            if (v < 1040) return "lobby";
            if (v < 1060) return "world";
            if (v < 1080) return "building";
            if (v < 1090) return "economy";
            if (v < 1110) return "population";
            if (v < 1120) return "shipping";
            if (v < 1130) return "dragon";
            if (v < 1140) return "diplomacy";
            if (v < 1150) return "siege";
            if (v < 1160) return "wildlife";
            if (v < 1170) return "streamer";
            return "unassigned";
        }
    }
}
