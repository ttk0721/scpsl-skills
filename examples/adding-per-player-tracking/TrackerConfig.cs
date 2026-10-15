using System.ComponentModel;

namespace Examples.AddingPerPlayerTracking
{
    /// <summary>
    /// Public get/set properties only (fields are ignored). Everything except SampleSeconds,
    /// FlushSeconds and FileName is read at use, so "labapi reload configs" applies it without a restart.
    /// </summary>
    public sealed class TrackerConfig
    {
        [Description("Kill switch. false at boot registers nothing; false after a config reload stops sampling.")]
        public bool IsEnabled { get; set; } = true;

        [Description("Seconds between samples of one player. Read once at boot. 5 s means about one player per 5 frames at 60 players.")]
        public int SampleSeconds { get; set; } = 5;

        [Description("Seconds without movement, aiming or speaking before time counts as AFK.")]
        public int AfkAfterSeconds { get; set; } = 120;

        [Description("Position change between two samples that counts as moving, in metres.")]
        public float MoveThresholdMeters { get; set; } = 0.25f;

        [Description("Look change between two samples that counts as aiming, in degrees (pitch plus yaw).")]
        public float LookThresholdDegrees { get; set; } = 2f;

        [Description("Seconds an account may be unseen (leave, restart, reconnect) before its idle clock starts over. Shorter than a restart plus reconnect would reset the clock every round.")]
        public int IdleCarryOverSeconds { get; set; } = 300;

        [Description("Count a player who is transmitting voice as active. Polled in the sample, no voice event is subscribed.")]
        public bool VoiceCountsAsActivity { get; set; } = true;

        [Description("Seconds between journal writes. Read once at boot.")]
        public int FlushSeconds { get; set; } = 120;

        [Description("Journal file name inside this plugin's per-port config folder. Read once at boot.")]
        public string FileName { get; set; } = "playtime-journal.tsv";
    }
}
