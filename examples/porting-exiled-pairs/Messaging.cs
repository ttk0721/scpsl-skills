using System;
using LabApi.Features.Wrappers;

namespace Examples.PortingExiledPairs
{
    /// <summary>
    /// Pair 3 (broadcasts and hints). The EXILED original:
    /// <code>
    /// player.Broadcast(5, "text");          // duration FIRST
    /// player.ShowHint("text", 5f);
    /// Map.Broadcast(10, "text");            // everyone
    /// foreach (Player p in Player.List) p.ShowHint("text", 5f);   // Map.ShowHint equivalent
    /// </code>
    /// LabAPI: <c>player.SendBroadcast(text, duration)</c> takes the MESSAGE first and a <c>ushort</c> duration,
    /// <c>player.SendHint(text, seconds)</c>, <c>Server.SendBroadcast(text, duration)</c> for everyone. There is
    /// no map-wide hint: loop over <c>Player.ReadyList</c>. Swapped arguments are the classic silent bug:
    /// a literal pair like <c>(5, "x")</c> fails to compile, but values passed through variables may not.
    /// </summary>
    internal sealed class Messaging
    {
        private readonly Func<PairsTranslation> _text;

        public Messaging(Func<PairsTranslation> text)
        {
            _text = text;
        }

        public PairsTranslation Text => _text();

        /// <summary>Welcome for one player. The player's name is hand-controlled data, so it is only inserted, never parsed.</summary>
        public void Welcome(Player player)
        {
            if (player == null || player.IsDestroyed || player.IsHost) return;   // never message the host (SendHint throws there)
            string text = Text.Welcome.Replace("{name}", player.Nickname ?? string.Empty);
            player.SendBroadcast(text, 5);
        }

        /// <summary>EXILED Map.Broadcast. One call, the game sends it to every connection.</summary>
        public void BroadcastAll(string text, int seconds)
        {
            if (string.IsNullOrEmpty(text)) return;
            Server.SendBroadcast(text, ToDuration(seconds));
        }

        /// <summary>
        /// EXILED Map.ShowHint / a foreach over Player.List. ReadyList = authenticated players plus dummies:
        /// the host and half-connected hubs are excluded, which is what a hint needs (HintDisplay.Show throws for the host).
        /// If another feature owns a HUD hint, route this through it (scpsl-hints-and-hud): hints from different features compete for one client slot (newest-wins is unverified).
        /// </summary>
        public void HintAll(string text, float seconds)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (Player p in Player.ReadyList)
                p.SendHint(text, seconds);
        }

        /// <summary>Broadcast duration is a ushort: clamp instead of letting a config value wrap around.</summary>
        private static ushort ToDuration(int seconds)
        {
            if (seconds < 1) return 1;
            if (seconds > ushort.MaxValue) return ushort.MaxValue;
            return (ushort)seconds;
        }
    }
}
