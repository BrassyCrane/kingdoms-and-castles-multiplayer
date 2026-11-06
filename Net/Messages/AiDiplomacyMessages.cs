// Kingdoms and Castles Multiplayer. Copyright (c) BrassyCrane and contributors.
// All rights reserved, not open source: see LICENSE.md for what you may do with this code.

using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    public class DiplomacyRefreshRequestMessage : INetMessage
    {
        public NetMessageId Id { get { return NetMessageId.DiplomacyRefresh; } }
        public void Serialize(Message m) { }
        public void Deserialize(Message m) { }
    }

    /// <summary>What a guest asks of an AI kingdom. The values travel on the wire; keep them.</summary>
    public enum AiAction
    {
        Ally = 0,
        BreakAlliance = 1,
        War = 2,
        Peace = 3,
        Gift = 4,
        Demand = 5,
    }

    /// <summary>What an AI kingdom proposes to a guest on its own. Values travel on the wire.</summary>
    public enum AiProposalKind
    {
        GoldDemand = 0,
        PeaceOffer = 1,
    }

    /// <summary>
    /// Host to everyone: the AI kingdoms the host is running, team, flag, name, and each one's
    /// opinion of every human team. A guest has no AI kingdoms of its own, so this is how its
    /// diplomacy window knows who they are and what they think of it.
    /// </summary>
    public class AiRosterMessage : INetMessage
    {
        public NetMessageId Id { get { return NetMessageId.AiRoster; } }

        public int[] Teams = new int[0];
        public int[] Banners = new int[0];
        public string[] Names = new string[0];

        /// <summary>Each kingdom's home island, -1 if it has none, so a guest can give it the island.</summary>
        public int[] Landmasses = new int[0];

        // Flattened (ai, human) -> standing 0..4.
        public int[] StandingAi = new int[0];
        public int[] StandingHuman = new int[0];
        public int[] StandingValue = new int[0];

        public void Serialize(Message m)
        {
            m.AddInt(Teams.Length);
            for (int i = 0; i < Teams.Length; i++)
            {
                m.AddInt(Teams[i]);
                m.AddInt(Banners[i]);
                m.AddString(Names[i] ?? string.Empty);
                m.AddInt(i < Landmasses.Length ? Landmasses[i] : -1);
            }

            m.AddInt(StandingAi.Length);
            for (int i = 0; i < StandingAi.Length; i++)
            {
                m.AddInt(StandingAi[i]);
                m.AddInt(StandingHuman[i]);
                m.AddInt(StandingValue[i]);
            }
        }

        public void Deserialize(Message m)
        {
            int n = m.GetInt();
            Teams = new int[n];
            Banners = new int[n];
            Names = new string[n];
            Landmasses = new int[n];
            for (int i = 0; i < n; i++)
            {
                Teams[i] = m.GetInt();
                Banners[i] = m.GetInt();
                Names[i] = m.GetString();
                Landmasses[i] = m.GetInt();
            }

            int s = m.GetInt();
            StandingAi = new int[s];
            StandingHuman = new int[s];
            StandingValue = new int[s];
            for (int i = 0; i < s; i++)
            {
                StandingAi[i] = m.GetInt();
                StandingHuman[i] = m.GetInt();
                StandingValue[i] = m.GetInt();
            }
        }
    }

    /// <summary>
    /// Guest to host: "ask this AI kingdom for this". For a Gift the guest has ALREADY taken
    /// <see cref="Amount"/> out of its own stores (only its own machine holds them for real);
    /// the host delivers it to the AI, or sends it back if the AI refuses.
    /// </summary>
    public class AiRequestMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.AiRequest; } }
        public ushort Origin { get; set; }

        public int FromTeam;
        public int AiTeam;
        public int Action;
        public int Resource;
        public int Amount;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddInt(FromTeam);
            m.AddInt(AiTeam);
            m.AddInt(Action);
            m.AddInt(Resource);
            m.AddInt(Amount);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            FromTeam = m.GetInt();
            AiTeam = m.GetInt();
            Action = m.GetInt();
            Resource = m.GetInt();
            Amount = m.GetInt();
        }
    }

    /// <summary>
    /// Host to one guest: an AI kingdom's answer, or a notice from it (a warning, a gift), plus
    /// anything the guest should now put into its OWN stores (tribute paid, a refused gift sent
    /// back, a gift from the AI), which only the guest's machine can do for real.
    /// </summary>
    public class AiResultMessage : INetMessage
    {
        public NetMessageId Id { get { return NetMessageId.AiResult; } }

        public int AiTeam;
        public string Title = "";
        public string Body = "";
        public int[] DepositResource = new int[0];
        public int[] DepositAmount = new int[0];

        public void Serialize(Message m)
        {
            m.AddInt(AiTeam);
            m.AddString(Title ?? string.Empty);
            m.AddString(Body ?? string.Empty);
            m.AddInt(DepositResource.Length);
            for (int i = 0; i < DepositResource.Length; i++)
            {
                m.AddInt(DepositResource[i]);
                m.AddInt(DepositAmount[i]);
            }
        }

        public void Deserialize(Message m)
        {
            AiTeam = m.GetInt();
            Title = m.GetString();
            Body = m.GetString();
            int n = m.GetInt();
            DepositResource = new int[n];
            DepositAmount = new int[n];
            for (int i = 0; i < n; i++)
            {
                DepositResource[i] = m.GetInt();
                DepositAmount[i] = m.GetInt();
            }
        }
    }

    /// <summary>Host to one guest: an AI kingdom proposes something and waits for an answer.</summary>
    public class AiProposalMessage : INetMessage
    {
        public NetMessageId Id { get { return NetMessageId.AiProposal; } }

        public int ProposalId;
        public int AiTeam;
        public int Kind;
        public int Amount;
        public string Title = "";
        public string Body = "";

        public void Serialize(Message m)
        {
            m.AddInt(ProposalId);
            m.AddInt(AiTeam);
            m.AddInt(Kind);
            m.AddInt(Amount);
            m.AddString(Title ?? string.Empty);
            m.AddString(Body ?? string.Empty);
        }

        public void Deserialize(Message m)
        {
            ProposalId = m.GetInt();
            AiTeam = m.GetInt();
            Kind = m.GetInt();
            Amount = m.GetInt();
            Title = m.GetString();
            Body = m.GetString();
        }
    }

    /// <summary>
    /// Guest to host: the answer to an AI proposal. For a gold demand the guest has already
    /// taken <see cref="Paid"/> out of its own treasury.
    /// </summary>
    public class AiProposalAnswerMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.AiProposalAnswer; } }
        public ushort Origin { get; set; }

        public int ProposalId;
        public bool Accept;
        public int Paid;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddInt(ProposalId);
            m.AddBool(Accept);
            m.AddInt(Paid);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            ProposalId = m.GetInt();
            Accept = m.GetBool();
            Paid = m.GetInt();
        }
    }
}
