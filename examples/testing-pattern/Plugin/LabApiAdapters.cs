using System;
using System.Collections.Generic;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;

namespace Examples.TestingPattern
{
    /// <summary>
    /// The only place that touches Player. Everything it returns is a plain copy, so the tested
    /// code never sees a game object. It is deliberately thin: nothing here is worth a unit test,
    /// and that is only true while it stays free of decisions. A decision (which players count,
    /// what "staff" means) belongs in Core where a test can reach it.
    /// </summary>
    internal sealed class LabApiPlayerSource : IPlayerSource
    {
        private readonly Func<bool> _treatRemoteAdminAsStaff;

        public LabApiPlayerSource(Func<bool> treatRemoteAdminAsStaff)
        {
            _treatRemoteAdminAsStaff = treatRemoteAdminAsStaff;
        }

        public void Fill(List<PlayerSnapshot> into)
        {
            // ReadyList is a LINQ iterator (one small allocation per call) and UserId does string work
            // (Contains, Substring) on every read. Fine at 1 Hz; never do this per frame (scpsl-main-thread-and-timing).
            // The allocation test in Tests/ covers Core only, not this adapter.
            bool remoteAdminIsStaff = _treatRemoteAdminAsStaff(); // read at use time: a config reload applies
            foreach (Player p in Player.ReadyList)
            {
                if (p.IsHost || p.IsNpc || p.IsDummy)
                    continue;

                into.Add(new PlayerSnapshot(p.UserId, remoteAdminIsStaff && p.RemoteAdminAccess, p.IsAlive));
            }
        }
    }

    /// <summary>
    /// Stand-in for the real delivery (an HTTP API, a database). It logs, which is cheap
    /// here because a grant happens once per player per threshold, not per tick.
    /// </summary>
    internal sealed class LogRewardSink : IRewardSink
    {
        public void Grant(string userId, int amount)
        {
            Logger.Info("Reward " + amount + " -> " + userId);
        }
    }
}
