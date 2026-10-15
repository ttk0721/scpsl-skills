using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using SlKit;

namespace Examples.LabapiEventsSnippets
{
    /// <summary>
    /// Handles an event that is raised OFF the main thread. <c>PlayerEvents.PreAuthenticating</c> runs on
    /// the single <c>ConnectionRequestThread</c>, so the handler may touch only the args and state that
    /// is safe to share, and hands everything else to the main thread through a queue:
    /// <code>
    /// ConnectionRequestThread                         main thread (SlClock job)
    /// OnPreAuthenticating  -- ConcurrentQueue -->     Drain  -- plain HashSet --> TryConsume (Joined)
    /// reads _blocked (ConcurrentDictionary)
    /// </code>
    /// </summary>
    internal sealed class PreAuthBridge
    {
        private const int MaxQueued = 256;
        private const int DrainPerRun = 64;
        private const int MaxExpected = 1024; // accounts that pre-auth but never join must not grow the set forever

        // Shared between threads: only thread-safe collections.
        private readonly ConcurrentDictionary<string, byte> _blocked = new ConcurrentDictionary<string, byte>();
        private readonly ConcurrentQueue<string> _admitted = new ConcurrentQueue<string>();
        private int _queued; // our own count: bounds the queue without enumerating it

        // Main thread only: a plain collection, because only Drain and Joined-time code touch it.
        private readonly HashSet<string> _expectedJoins = new HashSet<string>();

        private IDisposable _drainJob;
        private bool _subscribed;

        /// <summary>Main thread. The blocklist itself is concurrent, so the pre-auth thread can read it.</summary>
        public void Block(string userId) => _blocked[userId] = 0;

        public void Unblock(string userId)
        {
            byte ignored;
            _blocked.TryRemove(userId, out ignored);
        }

        /// <summary>Fallible setup: call before <see cref="Subscribe"/>.</summary>
        public void Start()
        {
            if (_drainJob == null)
                _drainJob = SlClock.Every(TimeSpan.FromMilliseconds(250), Drain, "snippets.preauth-drain");
        }

        public void Subscribe()
        {
            if (_subscribed)
                return;

            PlayerEvents.PreAuthenticating += OnPreAuthenticating;
            _subscribed = true;
        }

        /// <summary>Atomic event removal and a flag-only dispose: safe from any thread (Shutdown may not be on main).</summary>
        public void Release()
        {
            PlayerEvents.PreAuthenticating -= OnPreAuthenticating;
            _subscribed = false;
            IDisposable job = _drainJob;
            _drainJob = null;
            if (job != null)
                job.Dispose();
        }

        /// <summary>Main thread, e.g. from a <c>Joined</c> handler: was this account seen by pre-auth?</summary>
        public bool TryConsume(string userId) => userId != null && _expectedJoins.Remove(userId);

        /// <summary>Main thread, e.g. at WaitingForPlayers: last round's unmatched accounts are stale.</summary>
        public void ResetRound() => _expectedJoins.Clear();

        public int ExpectedCount => _expectedJoins.Count;

        // Runs on ConnectionRequestThread. Allowed: the args' own strings and bytes, our concurrent
        // collections, Interlocked, RateLimitedLog (ANY thread). Forbidden: Player.Get, Player.List,
        // any UnityEngine.Object, MEC Timing. A single thread serves every join, so keep it short.
        private void OnPreAuthenticating(PlayerPreAuthenticatingEventArgs ev)
        {
            try
            {
                string userId = ev.UserId;
                if (userId == null)
                    return;

                if (_blocked.ContainsKey(userId))
                {
                    ev.RejectCustom("Blocked by server rules."); // writes args only, sets IsAllowed = false
                    return;
                }

                // A hint, not a promise: a later handler can still reject. Bounded, so a flood of
                // connection attempts cannot grow memory while the main thread is stalled.
                if (!ev.IsAllowed || !ev.CanJoin)
                    return;

                if (Interlocked.Increment(ref _queued) > MaxQueued)
                {
                    Interlocked.Decrement(ref _queued);
                    return;
                }

                _admitted.Enqueue(userId);
            }
            catch (Exception e)
            {
                RateLimitedLog.Error("snippets.preauth", () => "PreAuthenticating handler failed: " + e);
            }
        }

        // SlClock job: main thread, wall-clock period. Bounded per run so one burst cannot eat a frame.
        private void Drain()
        {
            string userId;
            for (int i = 0; i < DrainPerRun && _admitted.TryDequeue(out userId); i++)
            {
                Interlocked.Decrement(ref _queued);
                if (_expectedJoins.Count < MaxExpected)
                    _expectedJoins.Add(userId);
            }
        }
    }
}
