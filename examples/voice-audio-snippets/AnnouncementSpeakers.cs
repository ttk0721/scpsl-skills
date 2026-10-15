using System;
using System.Collections.Generic;
using LabApi.Features.Wrappers;
using SlKit;
using UnityEngine;

namespace Examples.VoiceAudioSnippets
{
    /// <summary>
    /// A small pool of speakers for one-shot sounds (chimes, announcements) played to a chosen audience.
    /// </summary>
    /// <remarks>
    /// <para>What this encodes, each from the audio rules in the skill: one speaker per stream and a pool
    /// instead of one per effect; a unique <c>ControllerId</c> per slot; <c>networkSpawn: false</c> then
    /// <c>Spawn()</c>; <c>MaxDistance</c> set on purpose; an <c>O(1)</c>, allocation-free
    /// <c>ValidPlayers</c> predicate backed by a set that is rebuilt at 10 Hz and not per frame; clips
    /// padded to whole frames; <c>Stop()</c> after the clip duration as a guard.</para>
    /// <para>Main thread only (Play, Spawn, transmitters and Mirror are not thread-safe).</para>
    /// </remarks>
    internal sealed class AnnouncementSpeakers
    {
        // 201.. is only an example range: ids are shared by every plugin on the server (default 0 is
        // shared by everything left at default), so pick yours and document it.
        private const byte FirstControllerId = 201;
        private const int MaxSlots = 4;
        private static readonly TimeSpan AudienceRefresh = TimeSpan.FromMilliseconds(100);

        private sealed class Slot
        {
            public byte ControllerId;
            public SpeakerToy Toy;
            public bool Busy;
            public Func<Player, bool> Include;           // evaluated at refresh time, not per packet
            public Func<Player, bool> Valid;              // what the transmitter calls, built once
            public readonly HashSet<int> Audience = new HashSet<int>();
            public IDisposable Refresh;
            public IDisposable Guard;
        }

        private readonly List<Slot> _slots = new List<Slot>(MaxSlots);

        /// <summary>Plays to everyone within <paramref name="radius"/> metres of <paramref name="position"/>.</summary>
        public bool PlayNear(Vector3 position, float[] clip, float radius, float volume = 1f)
        {
            float sqr = radius * radius;
            // One closure per announcement, captured by the slot; never runs per packet.
            return Play(position, clip, radius, volume, p => (p.Position - position).sqrMagnitude <= sqr);
        }

        /// <summary>Plays to every real player (the "global" case). Note the MaxDistance in the argument.</summary>
        public bool PlayEverywhere(Vector3 position, float[] clip, float maxDistance, float volume = 1f)
            => Play(position, clip, maxDistance, volume, null);

        /// <summary>
        /// Plays <paramref name="clip"/> from <paramref name="position"/> to the players for whom
        /// <paramref name="include"/> is true (null = every real player). Returns false when every slot
        /// is busy: the announcement is dropped, which beats allocating a speaker per sound.
        /// </summary>
        /// <param name="maxDistance">Clients cull by it even for a non-spatial speaker, so a "global"
        /// sound needs a big value (the default is 15).</param>
        public bool Play(Vector3 position, float[] clip, float maxDistance, float volume, Func<Player, bool> include)
        {
            if (clip == null || clip.Length == 0)
                return false;

            Slot slot = Rent();
            if (slot == null)
                return false;

            float[] frames = AudioClips.PadToFrames(clip);

            slot.Include = include;
            slot.Toy.Position = position;
            slot.Toy.MaxDistance = maxDistance;
            slot.Toy.Volume = volume;
            RebuildAudience(slot);

            slot.Busy = true;
            slot.Toy.Play(frames, queue: false); // interrupts whatever this controller was doing

            // The audience follows people walking in and out, at 10 Hz, only while the clip plays. Round
            // scope: the toys die with the scene, so the jobs must not outlive the round either.
            slot.Refresh = SlClock.Every(AudienceRefresh, () => OnRefresh(slot), "pa.audience", ClockScope.Round);

            // Guard: even if IsPlaying never turns false, the slot is freed after the clip plus a tail.
            TimeSpan guard = TimeSpan.FromSeconds(AudioClips.Seconds(frames) + 0.25);
            slot.Guard = SlClock.After(guard, () => Finish(slot), "pa.release");
            return true;
        }

        /// <summary>Stops every slot now (for example at round end).</summary>
        public void StopAll()
        {
            foreach (Slot slot in _slots)
                if (slot.Busy)
                    Finish(slot);
        }

        /// <summary>
        /// Call at <c>WaitingForPlayers</c>. A round restart reloads the scene and destroys the toys, but
        /// the static per-id transmitters survive, so they are stopped by id and the pool starts empty.
        /// </summary>
        public void ResetRound()
        {
            foreach (Slot slot in _slots)
            {
                SpeakerToy.Stop(slot.ControllerId);
                DisposeJobs(slot);
            }

            _slots.Clear();
        }

        private Slot Rent()
        {
            // Drop slots whose toy was destroyed under us (a scene reload ResetRound did not see), so
            // they neither get rented nor count against MaxSlots.
            for (int i = _slots.Count - 1; i >= 0; i--)
            {
                if (!_slots[i].Toy.IsDestroyed)
                    continue;

                SpeakerToy.Stop(_slots[i].ControllerId);
                DisposeJobs(_slots[i]);
                _slots.RemoveAt(i);
            }

            foreach (Slot slot in _slots)
                if (!slot.Busy)
                    return slot;

            if (_slots.Count >= MaxSlots)
                return null;

            // Lowest id in our range that no live slot uses (slots may have been dropped above).
            for (int offset = 0; offset < MaxSlots; offset++)
            {
                byte id = (byte)(FirstControllerId + offset);
                if (!_slots.Exists(s => s.ControllerId == id))
                    return CreateSlot(id);
            }

            return null;
        }

        private Slot CreateSlot(byte controllerId)
        {
            var slot = new Slot { ControllerId = controllerId };

            // Configure first, spawn once: one spawn message instead of spawn plus a state update.
            SpeakerToy toy = SpeakerToy.Create(Vector3.zero, networkSpawn: false);
            toy.ControllerId = controllerId;
            toy.IsSpatial = true;
            toy.MinDistance = 2f;
            toy.MaxDistance = 25f;

            // Built once per slot. The transmitter calls it for every hub every 10 ms, so it is a null
            // check and one hash lookup: no allocation, no LINQ, no throw. The host and dummies are never
            // in the set, so they are skipped without a separate test.
            slot.Valid = p => p != null && slot.Audience.Contains(p.PlayerId);
            toy.ValidPlayers = slot.Valid;

            toy.Spawn();
            slot.Toy = toy;
            _slots.Add(slot);
            return slot;
        }

        // Runs at 10 Hz per busy slot. Cheap: one pass over the player list into a reused set.
        private void OnRefresh(Slot slot)
        {
            if (!slot.Busy)
                return;

            // IsPlaying is false when the clip ended (or an exception aborted the loop). Both mean done.
            if (!slot.Toy.IsPlaying)
            {
                Finish(slot);
                return;
            }

            RebuildAudience(slot);
        }

        private static void RebuildAudience(Slot slot)
        {
            slot.Audience.Clear();
            foreach (Player player in Player.List)
            {
                if (!player.IsPlayer || !player.IsReady)
                    continue;

                if (slot.Include == null || slot.Include(player))
                    slot.Audience.Add(player.PlayerId);
            }
        }

        private static void Finish(Slot slot)
        {
            if (!slot.Busy)
                return;

            slot.Busy = false;
            if (!slot.Toy.IsDestroyed)
                slot.Toy.Stop();          // clears the queue and kills the coroutine
            DisposeJobs(slot);
            slot.Audience.Clear();
            slot.Include = null;
        }

        private static void DisposeJobs(Slot slot)
        {
            if (slot.Refresh != null)
                slot.Refresh.Dispose();
            if (slot.Guard != null)
                slot.Guard.Dispose();
            slot.Refresh = null;
            slot.Guard = null;
        }
    }
}
