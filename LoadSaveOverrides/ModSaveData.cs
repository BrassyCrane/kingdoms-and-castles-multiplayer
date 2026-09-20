using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace KaCMultiplayer.LoadSaveOverrides
{
    /// <summary>
    /// Everything the multiplayer session must persist that a stock <see cref="LoadSaveContainer"/>
    /// does not: one <see cref="Player.PlayerSaveData"/> per kingdom (which carries that kingdom's
    /// buildings, villagers and economy), the kingdom-name map, and the steamId -> teamId identity
    /// map that <see cref="LoadIdentity"/> rebuilds from.
    ///
    /// This is JSON'd into the vanilla save's built-in mod-data dictionary
    /// (<c>LoadSave.CustomSaveData</c>), so the file on disk stays a plain, unmodded-openable
    /// <see cref="LoadSaveContainer"/>, which is the whole point of the migration off the old
    /// <c>SessionSave : LoadSaveContainer</c> subclass, which shredded saves for anyone without the
    /// mod. See docs/save-migration-plan.md.
    /// </summary>
    [Serializable]
    public class ModSessionData
    {
        public Dictionary<string, Player.PlayerSaveData> players = new Dictionary<string, Player.PlayerSaveData>();
        public Dictionary<string, string> kingdomNames = new Dictionary<string, string>();
        public Dictionary<string, int> identity = new Dictionary<string, int>();

        /// <summary>
        /// Who is at war with whom, keyed by the packed team pair PlayerRelations uses.
        ///
        /// Saved because a war was otherwise forgotten the moment anyone loaded: relations live only
        /// in memory, so a session reloaded from disk came back with everyone Neutral, silently
        /// undoing a declaration and re-opening the docks it had closed. That mattered little while
        /// there was nothing to fight over and matters a great deal now that combat exists.
        /// </summary>
        public Dictionary<long, World.Relations> relations = new Dictionary<long, World.Relations>();

        /// <summary>
        /// Wars that have been declared but have not started yet, as seasons still to run.
        ///
        /// Saved for the same reason relations are: a declaration is a promise about the future,
        /// and losing it on load would quietly cancel a war both players had already been warned
        /// about and were busy preparing for. Absent or null simply means nobody has declared.
        /// </summary>
        public Dictionary<long, int> pendingWars = new Dictionary<long, int>();

        /// <summary>
        /// What each kingdom charges for its exports, as three lists read together: team, resource,
        /// price.
        ///
        /// Flat rather than a dictionary of dictionaries, because this has to survive being read
        /// back by a build that may not have the same resource enum, and three parallel lists of
        /// plain ints degrade predictably where a nested structure does not.
        ///
        /// Saved for the same reason relations are: a price list is a decision the player made, and
        /// losing it on load would quietly hand every kingdom's goods back to the default table
        /// without saying so.
        /// </summary>
        public List<int> exportPriceTeams = new List<int>();
        public List<int> exportPriceTypes = new List<int>();
        public List<int> exportPriceValues = new List<int>();
    }

    /// <summary>
    /// Serialises <see cref="ModSessionData"/> to/from the game's mod-data dictionary.
    /// </summary>
    public static class ModSaveData
    {
        /// <summary>Matches info.json's <c>uniqueid</c>; the game namespaces our dictionary keys by it.</summary>
        public const string ModName = "KaCMultiplayer";
        private const string SessionKey = "session";

        /// <summary>
        /// Serialise EVERY instance field (public and private), up the inheritance chain, skipping
        /// <c>[NonSerialized]</c> (i.e. exactly what BinaryFormatter persists), and NEVER a property.
        ///
        /// Both halves matter, and both were verified against the game's own Newtonsoft 8.0 in a
        /// throwaway harness (docs/save-migration-plan.md, spike 1):
        ///  - Newtonsoft's DEFAULT serialises public members only, so it silently drops
        ///    <c>PlayerSaveData.Resources</c> (private; the kingdom's stockpile) and would load every
        ///    kingdom back with zero resources.
        ///  - Ignoring properties forecloses the Unity <c>Vector3.normalized</c>/<c>magnitude</c>
        ///    self-referencing loop, since only the x/y/z FIELDS are ever visited.
        /// </summary>
        private class FieldsResolver : DefaultContractResolver
        {
            protected override List<MemberInfo> GetSerializableMembers(Type t)
            {
                var members = new List<MemberInfo>();
                for (Type cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
                {
                    FieldInfo[] fields = cur.GetFields(
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    for (int i = 0; i < fields.Length; i++)
                        if (!fields[i].IsNotSerialized)   // honour [NonSerialized], like BinaryFormatter
                            members.Add(fields[i]);
                }
                return members;
            }

            protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization ms)
            {
                JsonProperty p = base.CreateProperty(member, ms);
                p.Readable = true;
                p.Writable = true;
                p.Ignored = false;
                return p;
            }
        }

        // TypeNameHandling.Auto is REQUIRED, not optional. BuildingSaveData stores its component and
        // job save-records in `List<object>` fields, and UnpackStage2 reads each element's CONCRETE
        // runtime type (components[i].GetType().DeclaringType) to find the component and invoke its
        // Unpack by reflection. Without type info, Newtonsoft deserialises those elements as generic
        // JObjects, GetType() no longer names the real type, and Unpack hits GetComponent(null) ->
        // "Type cannot be null" (verified in-game, and reproduced in the spike harness). Auto writes a
        // $type only where the actual type differs from the declared one (i.e. the object elements),
        // using the simple assembly name; every such type here lives in Assembly-CSharp, so it resolves
        // without any mod-assembly binding. ReferenceLoopHandling.Ignore is belt-and-suspenders on top
        // of the fields-only resolver.
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            ContractResolver = new FieldsResolver(),
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            TypeNameHandling = TypeNameHandling.Auto,
        };

        /// <summary>
        /// One kingdom as text, with the same settings a save uses.
        ///
        /// JSON rather than a binary formatter because the game's mod security scanner rejects
        /// System.IO and System.Runtime.Serialization in mod code: a build using them fails the
        /// Workshop tool's compile check with "Compilation failed" and a list of the instructions
        /// it objected to. The save block already carries these same objects as JSON, so this is
        /// the proven path rather than a new one. See Net/KingdomMirror.cs.
        /// </summary>
        public static string SerializeKingdom(Player.PlayerSaveData data)
        {
            return JsonConvert.SerializeObject(data, Settings);
        }

        public static Player.PlayerSaveData DeserializeKingdom(string json)
        {
            return JsonConvert.DeserializeObject<Player.PlayerSaveData>(json, Settings);
        }

        public static string Serialize(ModSessionData data)
        {
            return JsonConvert.SerializeObject(data, Settings);
        }

        public static ModSessionData Deserialize(string json)
        {
            return JsonConvert.DeserializeObject<ModSessionData>(json, Settings);
        }

        /// <summary>
        /// Writes the session block into the game's mod-data dictionary (base64-encoded by the game
        /// and serialised as an ordinary entry of a stock container). Call inside an
        /// <c>OnSaveEvent</c> handler. The save routine packs the container, fires OnSaveEvent, then
        /// starts the write thread, so an entry added here rides into the file.
        /// </summary>
        public static void WriteSession(ModSessionData data)
        {
            LoadSave.SaveDataGeneric(ModName, SessionKey, Serialize(data));
        }

        /// <summary>
        /// Reads the session block back from whatever dictionary is currently live in
        /// <c>LoadSave.CustomSaveData_DontAccessDirectly</c>. Returns null if this save has no block
        /// (a vanilla save, or one made before the migration). The load path must have already
        /// pointed that static at the loading container's dictionary; see <see cref="ReadSession(SerializableDictionary{string,string})"/>
        /// for reading straight from a container instead.
        /// </summary>
        public static ModSessionData ReadSession()
        {
            string json = LoadSave.ReadDataGeneric(ModName, SessionKey);
            return json == null ? null : Deserialize(json);
        }

        /// <summary>
        /// Reads the session block directly out of a container's own dictionary, used on load,
        /// where the container has been deserialised but its dictionary has not yet been published to
        /// <c>LoadSave.CustomSaveData_DontAccessDirectly</c>. Returns null if the dictionary has no
        /// block (a plain vanilla / single-player save).
        /// </summary>
        public static ModSessionData ReadSession(SerializableDictionary<string, string> dict)
        {
            if (dict == null) return null;
            string key = ModName + SessionKey;
            if (!dict.ContainsKey(key)) return null;
            string json = ConvertBase64ToString(dict[key]);
            return json == null ? null : Deserialize(json);
        }

        // The game base64-encodes every dictionary value (LoadSave.SaveDataGeneric); mirror its own
        // decode so reading straight from a container matches ReadDataGeneric byte-for-byte.
        private static string ConvertBase64ToString(string b64)
        {
            try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }
            catch { return null; }
        }
    }
}
