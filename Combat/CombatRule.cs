namespace KaCMultiplayer.Combat
{
    /// <summary>
    /// Who arbitrates a fight, expressed as pure logic over facts the caller has already looked up.
    ///
    /// The model is LANDMASS AUTHORITY: whoever owns the ground a blow lands on decides what that
    /// blow did. It suits this game specifically, because multiplayer forces island maps with one
    /// kingdom per island, so "whose ground is this" is nearly always a real answer rather than an
    /// arbitrary tie-break.
    ///
    /// The property that makes it work across machines is that it is derived from POSITION ALONE.
    /// Every machine looks at the same cell, reads the same owner, and reaches the same verdict with
    /// no negotiation, no election and no message. A model needing agreement about who is in charge
    /// would need that agreement to arrive before the first hit lands, which it cannot.
    ///
    /// It is also even-handed in a way a single host is not. You are the authority on your own
    /// island and your attacker is the authority on theirs, so neither side decides both halves of
    /// a war, and no one machine carries every fight in the world.
    ///
    /// Split out as pure logic so the rule itself can be tested headlessly, the same reason
    /// Trade/TradeMath.cs exists. Nothing here touches World, Unity or the logger; the lookups
    /// happen in CombatAuthority and the answers are passed in.
    /// </summary>
    public static class CombatRule
    {
        /// <summary>No team owns the ground, so no owner can arbitrate.</summary>
        public const int NoTeam = int.MinValue;

        /// <summary>
        /// The team that should resolve damage landing on this ground.
        ///
        /// Ground with no owner is the interesting case, and there is a lot of it: open water, where
        /// naval fights happen, and neutral islands, where an invasion force lands before it has
        /// taken anything. An absent owner counts as no owner too, because a departed player's
        /// kingdom is frozen and their machine is not simulating anything for anyone. Both fall to
        /// the host, which is the one participant guaranteed to be present.
        /// </summary>
        public static int Arbiter(int landOwnerTeam, bool ownerPresent, int hostTeam)
        {
            if (landOwnerTeam == NoTeam) return hostTeam;   // unowned ground, the host decides
            if (!ownerPresent) return hostTeam;             // owner has left, their kingdom is frozen
            return landOwnerTeam;
        }

        /// <summary>
        /// Whether THIS machine is the one that should resolve the damage.
        ///
        /// Deliberately not "am I the owner": when the arbiter is absent or the ground is unowned the
        /// answer falls to the host, and the host has to recognise itself in that case even though it
        /// owns nothing where the fighting is.
        /// </summary>
        public static bool ResolvedHere(int landOwnerTeam, bool ownerPresent, int hostTeam,
                                        int localTeam, bool isHost)
        {
            int arbiter = Arbiter(landOwnerTeam, ownerPresent, hostTeam);

            // The host stands in whenever the verdict fell through to it, including when it has no
            // team of its own yet, which is true for a moment early in a session.
            if (arbiter == hostTeam) return isHost;

            return arbiter == localTeam;
        }
    }
}
