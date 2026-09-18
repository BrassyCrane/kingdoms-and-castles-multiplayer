namespace KaCMultiplayer.Net
{
    /// <summary>
    /// Packs an unordered pair of team ids into one key, and takes it apart again.
    ///
    /// Relations belong to a PAIR, not to a sender: teams 5 and 6 have one standing between them,
    /// and (5,6) must be the same entry as (6,5) or a declaration would be recorded twice and read
    /// back inconsistently depending on who asked.
    ///
    /// Its own file because the packing is now needed in three places that have to agree exactly:
    /// storing a relation, restoring a saved set, and sending the set to a joiner. Two of those
    /// take a key apart, and open-coding the shifts at each site is the kind of triplication that
    /// stays correct right up until somebody changes one of them. It is also pure, with no Unity,
    /// no logger and no game types, so the headless tests can hold it to its promises.
    /// </summary>
    public static class TeamPair
    {
        /// <summary>
        /// Order-independent key for two teams. The lower id always takes the high half, which is
        /// what makes (5,6) and (6,5) the same key.
        /// </summary>
        public static long Key(int teamA, int teamB)
        {
            int lo = teamA < teamB ? teamA : teamB;
            int hi = teamA < teamB ? teamB : teamA;
            return ((long)lo << 32) | (uint)hi;
        }

        /// <summary>The lower of the two team ids in a key.</summary>
        public static int Low(long key)
        {
            return (int)(key >> 32);
        }

        /// <summary>The higher of the two team ids in a key.</summary>
        public static int High(long key)
        {
            return (int)(key & 0xFFFFFFFFL);
        }
    }
}
