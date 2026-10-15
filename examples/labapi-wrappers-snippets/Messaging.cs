using System.Collections.Generic;
using LabApi.Features.Wrappers;

namespace Examples.LabapiWrappersSnippets
{
    /// <summary>
    /// Operation 10: broadcasts, hints and console messages. Every call is a network message, so the
    /// rules are: only to ready humans, never to the host, never the same text twice, and build the
    /// text before the call.
    /// </summary>
    internal sealed class Messaging
    {
        // Last hint text per UserId, so an unchanged text sends nothing.
        private readonly Dictionary<string, string> _lastHint = new Dictionary<string, string>();

        /// <summary>
        /// Server-wide broadcast: ONE RPC to all ready observers, so it is cheap. Do not loop
        /// Player.SendBroadcast over everyone to get the same result.
        /// </summary>
        public static void Announce(string text, ushort seconds)
        {
            if (string.IsNullOrEmpty(text))
                return;
            Server.SendBroadcast(text, seconds);
        }

        /// <summary>
        /// A targeted broadcast. Skips the host and anyone not ready yet: an unauthenticated client
        /// may not have the handlers (a precaution; a crash is unverified).
        /// </summary>
        public static bool Tell(Player player, string text, ushort seconds, bool replacePrevious = false)
        {
            if (!IsReachable(player) || string.IsNullOrEmpty(text))
                return false;
            player.SendBroadcast(text, seconds, shouldClearPrevious: replacePrevious);
            return true;
        }

        /// <summary>
        /// A hint, sent only when the text changed. SendHint throws InvalidOperationException for the
        /// host, allocates a parameter array, and every hint replaces the previous one on the client
        /// (one slot per player). An empty text is refused here: sent, it would clear the slot, other
        /// plugins' hints included. For HUD-grade output (keep-alive, layout) use scpsl-hints-and-hud
        /// instead of this: the change check below would never repaint a hint something else replaced.
        /// </summary>
        public bool Hint(Player player, string text, float seconds = 3f)
        {
            if (!IsReachable(player) || string.IsNullOrEmpty(text))
                return false;

            string userId = player.UserId;
            if (userId == null)
                return false;
            if (_lastHint.TryGetValue(userId, out string last) && last == text)
                return false;

            player.SendHint(text, seconds);
            _lastHint[userId] = text;
            return true;
        }

        /// <summary>Forget the dedupe state on Left, or the dictionary grows with every visitor.</summary>
        public void OnLeft(Player player)
        {
            string userId = player.UserId;
            if (userId != null)
                _lastHint.Remove(userId);
        }

        public void Clear() => _lastHint.Clear();

        /// <summary>
        /// One encrypted message per call, never batched by the game. Build the whole text first and
        /// send it once.
        /// </summary>
        public static bool Console(Player player, IReadOnlyList<string> lines)
        {
            if (!IsReachable(player) || lines == null || lines.Count == 0)
                return false;
            player.SendConsoleMessage(string.Join("\n", lines));
            return true;
        }

        private static bool IsReachable(Player player)
            => player != null && !player.IsDestroyed && player.IsPlayer && player.IsReady;
    }
}
