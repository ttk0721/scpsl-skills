using System.ComponentModel;

namespace Examples.PortingExiledPairs
{
    /// <summary>
    /// Pair 7 (config). EXILED: a class implementing IConfig (IsEnabled, Debug) read by Plugin&lt;TConfig&gt;.
    /// LabAPI: any class with a parameterless constructor and public get/set properties; there is no
    /// IConfig and no Debug flag, and [Description] becomes the YAML comment. The file is rewritten on
    /// every load.
    /// </summary>
    public sealed class PairsConfig
    {
        [Description("Kill switch: false = the plugin enables but does nothing.")]
        public bool IsEnabled { get; set; } = true;

        [Description("Damage multiplier between players of the same faction (1 = no change, 0 = no friendly damage).")]
        public float FriendlyDamageMultiplier { get; set; } = 0.5f;

        [Description("true = cancel fall damage completely.")]
        public bool BlockFallDamage { get; set; } = false;

        [Description("Health given when a lethal hit is cancelled, once per player per round. 0 = off.")]
        public int SecondChanceHealth { get; set; } = 0;

        [Description("Extra item for every Class-D at spawn. None = off.")]
        public ItemType ClassDBonusItem { get; set; } = ItemType.None;

        [Description("Seconds between server-wide announcements. 0 = off. Read once in Enable: restart to apply.")]
        public int AnnounceIntervalSeconds { get; set; } = 300;

        /// <summary>Clamps values so a bad edit cannot start a broadcast storm. Runs again on every config reload.</summary>
        public void Normalize()
        {
            if (!(FriendlyDamageMultiplier >= 0f)) FriendlyDamageMultiplier = 0f;   // the negated test also catches NaN
            if (SecondChanceHealth < 0) SecondChanceHealth = 0;
            if (AnnounceIntervalSeconds < 0) AnnounceIntervalSeconds = 0;
            if (AnnounceIntervalSeconds > 0 && AnnounceIntervalSeconds < 30) AnnounceIntervalSeconds = 30;
        }
    }

    /// <summary>
    /// Pair 7 (translations). EXILED has Plugin&lt;TConfig, TTranslation&gt;. LabAPI has no translation
    /// concept: this is a second config class loaded by hand in <see cref="PortingPairsPlugin.LoadConfigs"/>.
    /// Placeholders are filled with Replace, never string.Format: a stray brace in a hand-edited file
    /// would throw FormatException inside an event handler.
    /// </summary>
    public sealed class PairsTranslation
    {
        public string Welcome { get; set; } = "Welcome, {name}!";
        public string Announcement { get; set; } = "Server announcement: have fun and read the rules.";
        public string OnlineHint { get; set; } = "Players online: {count}";
        public string RoundTip { get; set; } = "Round started. Good luck.";
        public string NoPermission { get; set; } = "You need the pairs.heal permission.";
        public string HealUsage { get; set; } = "Usage: pairsheal <playerId|all> [amount]";
        public string Healed { get; set; } = "Healed {count} player(s) by {amount}.";
    }
}
