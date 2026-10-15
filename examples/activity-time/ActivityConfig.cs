using System.Collections.Generic;
using System.ComponentModel;

namespace Examples.ActivityTime
{
    public sealed class ActivityConfig
    {
        [Description("Kill switch. Read at use time, so 'labapi reload configs' applies it.")]
        public bool Enabled { get; set; } = true;

        [Description("Seconds between two samples of the same player. Applied at server start.")]
        public float SamplePeriodSeconds { get; set; } = 1f;

        [Description("Meters a player must move to count as moving again.")]
        public float MinMoveMeters { get; set; } = 0.5f;

        [Description("Seconds without moving after which a player stops earning time.")]
        public float IdleWindowSeconds { get; set; } = 30f;

        [Description("Permission groups (config_remoteadmin keys) that earn nothing. A list in the file replaces this default.")]
        public List<string> ExcludedGroups { get; set; } = new List<string> { "owner", "admin", "moderator" };
    }
}
