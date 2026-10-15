using System.ComponentModel;

namespace Examples.AddingPlayerHud
{
    /// <summary>Public get/set properties only; [Description] becomes the YAML comment.</summary>
    public sealed class HudConfig
    {
        [Description("Kill switch. false = the HUD sends nothing (read on every visit, so a config reload also stops a running HUD).")]
        public bool IsEnabled { get; set; } = true;

        [Description("Seconds between visits to ONE player (0.5 = at most 2 hints/s each). A policy, not a measured limit. Read once when the plugin enables: a reload does not change the period.")]
        public float RefreshSeconds { get; set; } = 0.5f;

        [Description("Resend an unchanged HUD after this many seconds, so a pickup warning (which replaces it: one hint slot per player) cannot cover it for long. An identical resend every 0.5 s did not flicker on a real client. Must stay below HintDurationSeconds.")]
        public float KeepAliveSeconds { get; set; } = 2f;

        [Description("Client-side lifetime of one hint. The server keeps no timer.")]
        public float HintDurationSeconds { get; set; } = 3f;

        [Description("Show a round clock that the client keeps ticking (no traffic between sends).")]
        public bool ShowRoundTimer { get; set; } = true;

        [Description("Show the player's kills and deaths this round.")]
        public bool ShowKills { get; set; } = true;

        [Description("Count kills between members of the same faction.")]
        public bool CountTeamKills { get; set; } = false;

        [Description("Assume another plugin also writes hints and may replace ours on spawn, role change or round start. true = after such an event the HUD is re-sent ONCE to everyone, ReassertDelaySeconds later, by the normal visits (no per-event send). Set false only when the owner confirms no other plugin writes hints. See hud-coexistence.md.")]
        public bool ReassertAfterSpawn { get; set; } = true;

        [Description("Wait this long after the first spawn, role change or round start of a burst before the re-send. It must outlast the other plugin's own refresh and the game's short hint-suppression window after a role change (both lengths UNVERIFIED: measure on a client). Latency is this plus up to one RefreshSeconds. Clamped to KeepAliveSeconds.")]
        public float ReassertDelaySeconds { get; set; } = 0.5f;

        [Description("Rich-text size in percent (30-150).")]
        public int TextSizePercent { get; set; } = 70;

        [Description("none | left | right | center. Aligns inside a centred 1200-unit container (at 1080p, 16:9: x 360..1560), so left and right stop 360 px short of the screen edges. The vertical position stays the default, near the screen centre. Measured on one client at 1920x1080 only; other resolutions and aspect ratios are UNVERIFIED.")]
        public string Align { get; set; } = "none";

        [Description("Add a 'Stats HUD' show/hide switch to the players' Settings tab (SSS).")]
        public bool OfferToggle { get; set; } = true;

        /// <summary>Clamps values so a bad edit cannot start a hint storm or leave a HUD that is never refreshed.</summary>
        public void Normalize()
        {
            if (!(RefreshSeconds >= 0.5f)) RefreshSeconds = 0.5f;               // the negated test also catches NaN
            if (!(HintDurationSeconds >= 1f)) HintDurationSeconds = 3f;
            // Room for keep-alive (>= one visit period) + one more period + margin, even at the floors.
            float minDuration = RefreshSeconds * 2f + 0.25f;
            if (HintDurationSeconds < minDuration) HintDurationSeconds = minDuration;

            // The keep-alive is only checked at a visit, so the duration has to cover
            // keep-alive + one visit period + a margin, or the HUD blinks out between sends.
            float max = HintDurationSeconds - RefreshSeconds - 0.25f;
            if (!(KeepAliveSeconds >= RefreshSeconds)) KeepAliveSeconds = RefreshSeconds;
            if (KeepAliveSeconds > max) KeepAliveSeconds = max > RefreshSeconds ? max : RefreshSeconds;

            // A re-assert later than the next keep-alive would add nothing.
            if (!(ReassertDelaySeconds >= 0f)) ReassertDelaySeconds = 0.5f;
            if (ReassertDelaySeconds > KeepAliveSeconds) ReassertDelaySeconds = KeepAliveSeconds;

            if (TextSizePercent < 30) TextSizePercent = 30;
            if (TextSizePercent > 150) TextSizePercent = 150;
            if (Align == null) Align = "none";
        }
    }
}
