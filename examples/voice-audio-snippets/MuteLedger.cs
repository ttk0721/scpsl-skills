using System.Collections.Generic;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Features.Wrappers;
using PlayerRoles.Voice;
using VoiceChat;

namespace Examples.VoiceAudioSnippets
{
    /// <summary>
    /// Muting that undoes only what it did, with the right pairing of the two mute lifetimes.
    /// </summary>
    /// <remarks>
    /// <para>Pairs: <c>Mute(true)</c> with <c>Unmute(false)</c> (flag only, gone at disconnect) and
    /// <c>Mute(false)</c> with <c>Unmute(true)</c> (entry in <c>mutes.txt</c>, shared with Remote Admin).
    /// A crossed pair is a silent no-op: <c>Unmute(true)</c> does nothing for a flag-only mute.</para>
    /// <para>Both lifetimes share one flag bit, so unmuting a player somebody else muted (an admin, the
    /// file) would lift THEIR mute. The ledger therefore refuses to record players that are already
    /// muted, and releases only ids it recorded.</para>
    /// <para>Main thread only. The wrappers raise no Muting/Muted event, so nothing else learns about
    /// these calls; the ledger is the only record.</para>
    /// </remarks>
    internal sealed class MuteLedger
    {
        // Keyed by UserId, not PlayerId: a reconnect makes a new hub and loses the flag, and the
        // Joined handler below re-applies it. PlayerId would be a different number after rejoin.
        private readonly HashSet<string> _temporary = new HashSet<string>();
        private readonly HashSet<string> _persistent = new HashSet<string>();
        private bool _subscribed;

        public int Count => _temporary.Count + _persistent.Count;

        /// <summary>
        /// Mutes <paramref name="player"/> until disconnect (or until rejoin, see <c>OnJoined</c>).
        /// Returns false when the player was already muted by someone else, so the caller can tell.
        /// </summary>
        public bool MuteTemporarily(Player player)
        {
            if (player == null || player.IsHost || player.IsMuted)
                return false;

            player.Mute(true);
            _temporary.Add(player.UserId);
            return true;
        }

        /// <summary>
        /// Mutes <paramref name="player"/> in <c>mutes.txt</c>: survives rejoin and restart. The write
        /// is synchronous on the main thread, so keep it to human-speed actions, never a loop.
        /// </summary>
        public bool MutePersistently(Player player)
        {
            if (player == null || player.IsHost || player.IsMuted)
                return false;

            player.Mute(false);
            _persistent.Add(player.UserId);
            return true;
        }

        /// <summary>Lifts only a mute this ledger applied. Anything else is left alone.</summary>
        public bool Release(Player player)
        {
            if (player == null)
                return false;

            string id = player.UserId;
            if (_temporary.Remove(id))
            {
                player.Unmute(false);
                return true;
            }

            if (_persistent.Remove(id))
            {
                player.Unmute(true);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Round-scoped policy: drops the temporary mutes (their flags died with the hubs). Persistent
        /// ones stay in the file, and in the ledger so they can still be released.
        /// </summary>
        public void ResetRound() => _temporary.Clear();

        public void Subscribe()
        {
            if (_subscribed)
                return;

            LabApi.Events.Handlers.PlayerEvents.Joined += OnJoined;
            _subscribed = true;
        }

        public void Unsubscribe()
        {
            LabApi.Events.Handlers.PlayerEvents.Joined -= OnJoined;
            _subscribed = false;
        }

        // Joined is raised AFTER authentication recomputes the mute flags from scratch, so a flag set
        // here survives. A flag set earlier (for example in a pre-auth hook) would be overwritten.
        private void OnJoined(PlayerJoinedEventArgs ev)
        {
            Player player = ev.Player;
            if (player != null && _temporary.Contains(player.UserId))
                player.Mute(true);
        }

        /// <summary>
        /// "Is this player audibly talking right now", the safe way. Not <c>Player.IsSpeaking</c> (reads
        /// a client-side buffer the server does not fill) and not <c>Player.VoiceChannel</c> (the last
        /// accepted channel, never reset). A muted talker holding push-to-talk keeps
        /// <c>ServerIsSending</c> true, so the mute test is part of the answer.
        /// </summary>
        public static bool IsAudiblyTalking(Player player, out VoiceChatChannel channel)
        {
            channel = VoiceChatChannel.None;

            VoiceModuleBase module = player.VoiceModule;
            if (module == null || player.IsMuted || !module.ServerIsSending)
                return false;

            channel = module.CurrentChannel;
            return channel != VoiceChatChannel.None;
        }
    }
}
