using System.ComponentModel;

namespace Examples.NetworkingSnippets
{
    /// <summary>
    /// Public get/set properties only (fields are ignored). Read <c>Config.X</c> at use time, never
    /// cache the object: a config reload replaces it, and an empty file gives null.
    /// </summary>
    public sealed class NetworkingConfig
    {
        [Description("Master kill switch. false: the plugin does nothing (read on every tick and command).")]
        public bool IsEnabled { get; set; } = true;

        [Description("Hard cap on toy identities this plugin may spawn per round. Every spawned toy is a recurring per-tick cost for every player, static or not.")]
        public int MaxToys { get; set; } = 64;

        [Description("Spawn byte budget per 100 ms tick. One primitive is about 134 B per ready player: toys x 134 x players must fit.")]
        public int SpawnBytesPerTick { get; set; } = 150000;

        [Description("Seconds between network updates of a moving toy. 0.1 or more for many movers, 0 only for a single hero object.")]
        public float MoveSyncIntervalSeconds { get; set; } = 0.1f;

        [Description("Client smoothing of moving toys: 0 snaps, 255 is the slowest chase. Larger values lengthen the time a toy must stay non-static.")]
        public byte MoveSmoothing { get; set; } = 0;

        [Description("Round-trip allowance (seconds) added before a moved toy is frozen again. An estimate: raise it for players with high ping.")]
        public float MoveNetworkAllowanceSeconds { get; set; } = 0.25f;

        [Description("Ghost: seconds between the request and the player leaving the position stream.")]
        public float GhostHideDelaySeconds { get; set; } = 1f;

        [Description("Ghost: seconds the voice and shots of a revealed player stay blocked (the first voice packets after a reveal are lost anyway).")]
        public float GhostVoiceLeadSeconds { get; set; } = 0.04f;

        [Description("Ghost: block the ghost's shots. A hidden shooter's shot plays silently at the parked transform; allowing it needs a Harmony patch.")]
        public bool GhostBlockShots { get; set; } = true;

        [Description("Ghost: cancel the ghost's voice (radio voice is not range-gated and would still be heard).")]
        public bool GhostMuteVoice { get; set; } = true;

        [Description("Ghost: SCP receivers keep seeing ghosts (939, 096 and 106 have their own perception checks).")]
        public bool GhostExemptScps { get; set; } = true;
    }
}
