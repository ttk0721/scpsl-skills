using System;
using System.Collections.Generic;
using LabApi.Features.Wrappers;

namespace Examples.LabapiWrappersSnippets
{
    /// <summary>
    /// Operation 1: find a player. Keep a stable key (UserId), re-resolve when you need the wrapper,
    /// and never store the wrapper itself.
    /// </summary>
    internal static class PlayerLookup
    {
        /// <summary>
        /// Resolves a stored UserId to a live, authenticated human. Returns null for "gone",
        /// "not yet ready", the host and dummies.
        /// </summary>
        /// <remarks>
        /// Player.TryGet(string) is O(1) on a cache hit. A miss, and any lookup of an offline id, is an
        /// O(N) scan (negative results are never cached), so call this per event or at a few Hz, not
        /// per frame. Dummies all share the UserId "ID_Dummy", so the IsPlayer test is what keeps a
        /// stored key from resolving to "some dummy".
        /// </remarks>
        public static Player ByUserId(string userId)
        {
            if (string.IsNullOrEmpty(userId))
                return null;
            if (!Player.TryGet(userId, out Player player))
                return null;

            // Order matters: IsDestroyed is a cheap managed check; IsPlayer and IsReady read the hub.
            if (player.IsDestroyed || !player.IsPlayer || !player.IsReady)
                return null;
            return player;
        }

        /// <summary>
        /// Per-pair and per-frame code works on ReferenceHub and resolves the wrapper only when it
        /// has to act. Player.Get(hub) is O(1) but hashes through a native call, and returns null
        /// for a destroyed hub.
        /// </summary>
        public static Player FromHub(ReferenceHub hub) => hub == null ? null : Player.Get(hub);

        /// <summary>
        /// Prefix search over authenticated humans only. Player.GetByName and friends sort the whole
        /// list with a culture-sensitive comparer, return the first prefix match (not the best), and
        /// include the host, dummies and unauthenticated hubs. TryGetPlayersByName throws while any
        /// client is still connecting. Filtering ReadyList yourself avoids all three.
        /// </summary>
        public static Player FirstByNicknamePrefix(string prefix)
        {
            if (string.IsNullOrEmpty(prefix))
                return null;

            // Nickname is set for every ReadyList human. DisplayName is not compared: a custom
            // display name gets a coloured '*' suffix appended, so exact matches on it fail.
            foreach (Player player in Player.ReadyList)
            {
                if (player.IsPlayer && player.Nickname.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return player;
            }
            return null;
        }
    }

    /// <summary>Your own per-player data, keyed by UserId. Never holds a Player.</summary>
    internal sealed class PlayerSession
    {
        public PlayerSession(string userId) { UserId = userId; }
        public string UserId { get; }
        public int Score { get; set; }
    }

    /// <summary>
    /// Many identical lookups per tick: maintain your own dictionary from Joined and Left instead of
    /// asking Player.Get(string) every time. UserId allocates for authenticated players (it strips a
    /// salt suffix), so it is read once per event here.
    /// </summary>
    internal sealed class SessionIndex
    {
        private readonly Dictionary<string, PlayerSession> _byUserId = new Dictionary<string, PlayerSession>();

        public int Count => _byUserId.Count;

        // Joined fires only for authenticated humans, after the hub is ready. It never fires for the
        // host or for dummies.
        public void OnJoined(Player player)
        {
            string userId = player.UserId;
            if (userId != null)
                _byUserId[userId] = new PlayerSession(userId);
        }

        // Left fires for every non-host hub, including connections that never authenticated and
        // dummies. UserId and Nickname are managed fields and stay readable inside it; IpAddress may
        // throw there (the transport peer is already gone), so capture an IP at Joined. UserId can be
        // null for a connection that never authenticated; Remove of an unknown key is a no-op.
        public void OnLeft(Player player)
        {
            string userId = player.UserId;
            if (userId != null)
                _byUserId.Remove(userId);
        }

        public bool TryGet(string userId, out PlayerSession session) => _byUserId.TryGetValue(userId, out session);

        // Not called per round: sessions are keyed by UserId, so they survive a restart, and clearing
        // at WaitingForPlayers could wipe players who already re-joined (event order not verified).
        // Use it only to reset your own score data. Never keep wrappers in a session.
        public void Clear() => _byUserId.Clear();
    }
}
