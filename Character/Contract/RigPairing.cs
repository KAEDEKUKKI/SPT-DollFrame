using System;

namespace DollFrame
{
    /// <summary>
    /// The retarget pairing rules over a pmx bone table (pure, parser-safe; the Unity rig
    /// build and the offline zerostretch pairing pin share them): the game-side direction
    /// partner of a mapped rig bone is the SAME pmx pairing on both sides - the character
    /// side picks the FIRST MAPPED DESCENDANT (contract rows, ascending pmx index order);
    /// a leaf with none uses its own parent-to-bone direction and pairs the nearest mapped
    /// ANCESTOR's anchor. Both sides therefore always share one pairing key: a mismatch is
    /// structurally impossible.
    /// </summary>
    internal static class RigPairing
    {
        /// <summary>
        /// First mapped PROPER descendant per pmx bone (index or -1, the bone itself never
        /// qualifies): descending sweep over pmx parent-first indices with the recurrence
        ///   desc[i] = min over direct children c of (mapped(c) ? c : desc[c]).
        /// parents[i] is the pmx parent index (-1 root); mapped(i) = has a direct BoneMap
        /// row whose game anchor resolved.
        /// </summary>
        internal static int[] FirstMappedDescendant(int[] parents, Func<int, bool> mapped)
        {
            int n = parents.Length;
            var firstChild = new int[n];
            var nextSib = new int[n];
            for (int i = 0; i < n; i++) { firstChild[i] = -1; nextSib[i] = -1; }
            // adjacency lists as (firstChild, nextSib) pairs: children always have larger indices
            for (int i = 0; i < n; i++)
            {
                int p = parents[i];
                if (p < 0 || p >= n) continue;
                nextSib[i] = firstChild[p];
                firstChild[p] = i;
            }
            var desc = new int[n];
            for (int i = n - 1; i >= 0; i--)
            {
                int best = -1;
                for (int c = firstChild[i]; c >= 0; c = nextSib[c])
                {
                    int cand = mapped(c) ? c : desc[c];
                    if (cand >= 0 && (best < 0 || cand < best)) best = cand;
                }
                desc[i] = best;
            }
            return desc;
        }

    /// <summary>Nearest mapped ANCESTOR index of pmx bone i (i itself excluded, or -1) walking pmx parents with a cycle guard.</summary>
    internal static int NearestMappedAncestor(int[] parents, int i, Func<int, bool> mapped)
    {
        int cur = i, guard = 0;
        while (guard++ < 64)
        {
            var p = cur >= 0 && cur < parents.Length ? parents[cur] : -1;
            if (p < 0) return -1;
            if (mapped(p)) return p;
            cur = p;
        }
        return -1;
    }
    }
}
