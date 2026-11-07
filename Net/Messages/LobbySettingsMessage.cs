// Kingdoms and Castles Multiplayer. Copyright (c) BrassyCrane and contributors.
// All rights reserved, not open source: see LICENSE.md for what you may do with this code.

using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// The host's lobby configuration, pushed to everyone whenever it changes and again
    /// when someone joins.
    ///
    /// Host-originated only, a client never proposes settings, so there is no server
    /// handler. It must also arrive before <see cref="WorldSeedMessage"/>, because
    /// generating a map needs the world type, size and rivers from here.
    /// </summary>
    public class LobbySettingsMessage : INetMessage
    {
        public NetMessageId Id { get { return NetMessageId.LobbySettings; } }

        public string ServerName;
        public int MaxPlayers;
        public bool Locked;
        public string Password;
        public int Difficulty;
        public string WorldSeed;
        public int WorldSize;
        public int WorldType;
        public int WorldRivers;
        public int PlacementType;
        public bool FogOfWar;
        public bool WitchShared;
        public bool SavedGame;
        public int SavedYear;
        public int SavedKingdoms;
        public int AiKingdomCount;

        // Always exactly LobbySettings.MaxAiKingdoms entries on the wire, used or not, so the
        // number of ints a receiver reads never depends on a value it has not read yet.
        public int[] AiDifficulties = new int[LobbySettings.MaxAiKingdoms];
        public int[] AiCodes = new int[LobbySettings.MaxAiKingdoms];
        public string[] AiNames = new string[LobbySettings.MaxAiKingdoms];

        public void Serialize(Message m)
        {
            m.AddString(ServerName ?? string.Empty);
            m.AddInt(MaxPlayers);
            m.AddBool(Locked);
            m.AddString(Password ?? string.Empty);
            m.AddInt(Difficulty);
            m.AddString(WorldSeed ?? string.Empty);
            m.AddInt(WorldSize);
            m.AddInt(WorldType);
            m.AddInt(WorldRivers);
            m.AddInt(PlacementType);
            m.AddBool(FogOfWar);
            m.AddBool(WitchShared);
            m.AddBool(SavedGame);
            m.AddInt(SavedYear);
            m.AddInt(SavedKingdoms);
            m.AddInt(AiKingdomCount);
            for (int i = 0; i < LobbySettings.MaxAiKingdoms; i++)
            {
                m.AddInt(AiDifficulties[i]);
                m.AddInt(AiCodes[i]);
                m.AddString(AiNames[i] ?? string.Empty);
            }
        }

        public void Deserialize(Message m)
        {
            ServerName = m.GetString();
            MaxPlayers = m.GetInt();
            Locked = m.GetBool();
            Password = m.GetString();
            Difficulty = m.GetInt();
            WorldSeed = m.GetString();
            WorldSize = m.GetInt();
            WorldType = m.GetInt();
            WorldRivers = m.GetInt();
            PlacementType = m.GetInt();
            FogOfWar = m.GetBool();
            WitchShared = m.GetBool();
            SavedGame = m.GetBool();
            SavedYear = m.GetInt();
            SavedKingdoms = m.GetInt();
            AiKingdomCount = m.GetInt();
            for (int i = 0; i < LobbySettings.MaxAiKingdoms; i++)
            {
                AiDifficulties[i] = m.GetInt();
                AiCodes[i] = m.GetInt();
                AiNames[i] = m.GetString();
            }
        }

        /// <summary>
        /// Snapshots live settings for sending. The world enums travel as ints so a game
        /// update that renumbers them cannot silently change what a byte on the wire means.
        /// </summary>
        public static LobbySettingsMessage From(LobbySettings s)
        {
            var msg = new LobbySettingsMessage
            {
                ServerName = s.ServerName,
                MaxPlayers = s.MaxPlayers,
                Locked = s.Locked,
                Password = s.Password,
                Difficulty = s.Difficulty,
                WorldSeed = s.WorldSeed,
                WorldSize = (int)s.WorldSize,
                WorldType = (int)s.WorldType,
                WorldRivers = (int)s.WorldRivers,
                PlacementType = s.PlacementType,
                FogOfWar = s.FogOfWar,
                WitchShared = s.WitchShared,
                SavedGame = s.SavedGame,
                SavedYear = s.SavedYear,
                SavedKingdoms = s.SavedKingdoms,
                AiKingdomCount = s.AiKingdomCount
            };
            System.Array.Copy(s.AiDifficulties, msg.AiDifficulties, LobbySettings.MaxAiKingdoms);
            System.Array.Copy(s.AiCodes, msg.AiCodes, LobbySettings.MaxAiKingdoms);
            System.Array.Copy(s.AiNames, msg.AiNames, LobbySettings.MaxAiKingdoms);
            return msg;
        }

        /// <summary>Copies received values into the live settings object.</summary>
        public void ApplyTo(LobbySettings s)
        {
            s.ServerName = ServerName;
            s.MaxPlayers = MaxPlayers;
            s.Locked = Locked;
            s.Password = Password;
            s.Difficulty = Difficulty;
            s.WorldSeed = WorldSeed;
            s.WorldSize = (World.MapSize)WorldSize;
            s.WorldType = (World.MapBias)WorldType;
            s.WorldRivers = (World.MapRiverLakes)WorldRivers;
            s.PlacementType = PlacementType;
            s.FogOfWar = FogOfWar;
            s.WitchShared = WitchShared;
            s.SavedGame = SavedGame;
            s.SavedYear = SavedYear;
            s.SavedKingdoms = SavedKingdoms;
            s.AiKingdomCount = AiKingdomCount;
            System.Array.Copy(AiDifficulties, s.AiDifficulties, LobbySettings.MaxAiKingdoms);
            System.Array.Copy(AiCodes, s.AiCodes, LobbySettings.MaxAiKingdoms);
            System.Array.Copy(AiNames, s.AiNames, LobbySettings.MaxAiKingdoms);
        }
    }
}
