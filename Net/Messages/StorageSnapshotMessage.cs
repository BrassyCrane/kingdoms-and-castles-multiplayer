// Kingdoms and Castles Multiplayer. Copyright (c) BrassyCrane and contributors.
// All rights reserved, not open source: see LICENSE.md for what you may do with this code.

using System;
using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    public class StorageSnapshotMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.StorageSnapshot; } }
        public ushort Origin { get; set; }
        public Guid Building;
        public byte Component;
        public int Wheat, Tree, Stone, Charcoal, Gold, Iron, Tools, Armament, Fish, Apple, Pork;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin).AddGuid(Building).AddByte(Component);
            m.AddInt(Wheat).AddInt(Tree).AddInt(Stone).AddInt(Charcoal).AddInt(Gold).AddInt(Iron);
            m.AddInt(Tools).AddInt(Armament).AddInt(Fish).AddInt(Apple).AddInt(Pork);
        }

        public void Deserialize(Message m)
        {
            Origin=m.GetUShort(); Building=m.GetGuid(); Component=m.GetByte();
            Wheat=m.GetInt(); Tree=m.GetInt(); Stone=m.GetInt(); Charcoal=m.GetInt(); Gold=m.GetInt();
            Iron=m.GetInt(); Tools=m.GetInt(); Armament=m.GetInt(); Fish=m.GetInt(); Apple=m.GetInt(); Pork=m.GetInt();
        }
    }
}
