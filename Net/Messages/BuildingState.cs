using System;
using Riptide;
using UnityEngine;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// The building fields that both placement and state-sync need to carry. Mirrors the
    /// game's own <c>Building.BuildingSaveData</c>, which is what the receiver ultimately
    /// unpacks these into.
    ///
    /// Shared by the two messages that need it rather than declared in each. Two copies of a
    /// seventeen-field codec is two chances to get the field order wrong, and the second copy is
    /// the one nobody remembers to update.
    /// </summary>
    public class BuildingState
    {
        public Guid Guid;
        public string UniqueName;
        public string CustomName;

        public Quaternion Rotation;
        public Vector3 GlobalPosition;
        public Vector3 LocalPosition;

        public bool Built;
        public bool Placed;
        public bool Open;
        public bool DoBuildAnimation;
        public bool ConstructionPaused;
        public bool SeenByPlayer;

        public float ConstructionProgress;
        public float Life;
        public float ModifiedMaxLife;
        public float DecayProtection;

        public int YearBuilt;

        /// <summary>
        /// Writes every field. Grouped by type so the mirror in <see cref="Read"/> can be
        /// compared against this at a glance.
        /// </summary>
        public void Write(Message m)
        {
            m.AddGuid(Guid);
            m.AddString(UniqueName ?? string.Empty);
            m.AddString(CustomName ?? string.Empty);

            m.AddQuaternion(Rotation);
            m.AddVector3(GlobalPosition);
            m.AddVector3(LocalPosition);

            m.AddBool(Built);
            m.AddBool(Placed);
            m.AddBool(Open);
            m.AddBool(DoBuildAnimation);
            m.AddBool(ConstructionPaused);
            m.AddBool(SeenByPlayer);

            m.AddFloat(ConstructionProgress);
            m.AddFloat(Life);
            m.AddFloat(ModifiedMaxLife);
            m.AddFloat(DecayProtection);

            m.AddInt(YearBuilt);
        }

        public void Read(Message m)
        {
            Guid = m.GetGuid();
            UniqueName = m.GetString();
            CustomName = m.GetString();

            Rotation = m.GetQuaternion();
            GlobalPosition = m.GetVector3();
            LocalPosition = m.GetVector3();

            Built = m.GetBool();
            Placed = m.GetBool();
            Open = m.GetBool();
            DoBuildAnimation = m.GetBool();
            ConstructionPaused = m.GetBool();
            SeenByPlayer = m.GetBool();

            ConstructionProgress = m.GetFloat();
            Life = m.GetFloat();
            ModifiedMaxLife = m.GetFloat();
            DecayProtection = m.GetFloat();

            YearBuilt = m.GetInt();
        }

        /// <summary>
        /// Copies these fields into the game's save-data struct, which is what actually
        /// applies them to a Building via Unpack.
        /// </summary>
        public Building.BuildingSaveData ToSaveData()
        {
            return new Building.BuildingSaveData
            {
                guid = Guid,
                uniqueName = UniqueName,
                customName = CustomName,
                rotation = Rotation,
                globalPosition = GlobalPosition,
                localPosition = LocalPosition,
                built = Built,
                placed = Placed,
                open = Open,
                doBuildAnimation = DoBuildAnimation,
                constructionPaused = ConstructionPaused,
                constructionProgress = ConstructionProgress,
                life = Life,
                ModifiedMaxLife = ModifiedMaxLife,
                yearBuilt = YearBuilt,
                decayProtection = DecayProtection,
                seenByPlayer = SeenByPlayer
            };
        }

        /// <summary>Captures a live building's state for sending.</summary>
        public static BuildingState From(Building.BuildingSaveData d)
        {
            return new BuildingState
            {
                Guid = d.guid,
                UniqueName = d.uniqueName,
                CustomName = d.customName,
                Rotation = d.rotation,
                GlobalPosition = d.globalPosition,
                LocalPosition = d.localPosition,
                Built = d.built,
                Placed = d.placed,
                Open = d.open,
                DoBuildAnimation = d.doBuildAnimation,
                ConstructionPaused = d.constructionPaused,
                ConstructionProgress = d.constructionProgress,
                Life = d.life,
                ModifiedMaxLife = d.ModifiedMaxLife,
                YearBuilt = d.yearBuilt,
                DecayProtection = d.decayProtection,
                SeenByPlayer = d.seenByPlayer
            };
        }
    }
}
