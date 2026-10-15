using System.ComponentModel;

namespace Examples.AddingPlayerAbilities
{
    /// <summary>
    /// Config: public get/set properties only, defaults as initializers. LabAPI replaces the Config
    /// object on a config reload, so code reads <c>Config.X</c> at use time and never caches the object.
    /// </summary>
    public sealed class AbilitiesConfig
    {
        [Description("Kill switch: false makes the plugin do nothing at all (no settings are sent, no keys react).")]
        public bool IsEnabled { get; set; } = true;

        [Description("LabAPI permission string a player needs to use any ability (permissions.yml). Empty = everyone.")]
        public string RequiredPermission { get; set; } = string.Empty;

        [Description("Let SCPs use abilities. Off by default: the heal and speed numbers are balanced for humans.")]
        public bool AllowScps { get; set; } = false;

        [Description("Short hints for 'used', 'on cooldown' and 'ready again'. Turn off if another plugin owns the hint slot.")]
        public bool ShowHints { get; set; } = true;

        [Description("Per-player key-event allowance: events accepted back to back before throttling.")]
        public int InputBurst { get; set; } = 4;

        [Description("Per-player key events accepted per second once the burst is spent.")]
        public double InputPerSecond { get; set; } = 2;

        public SecondWindConfig SecondWind { get; set; } = new SecondWindConfig();

        public SpeedBurstConfig SpeedBurst { get; set; } = new SpeedBurstConfig();
    }

    public sealed class SecondWindConfig
    {
        [Description("Offer this ability. Changing it needs a plugin reload: the setting entry is built at startup.")]
        public bool Enabled { get; set; } = true;

        [Description("Seconds before it can be used again.")]
        public double CooldownSeconds { get; set; } = 45;

        [Description("Health restored.")]
        public float HealAmount { get; set; } = 40;
    }

    public sealed class SpeedBurstConfig
    {
        [Description("Offer this ability. Changing it needs a plugin reload: the setting entry is built at startup.")]
        public bool Enabled { get; set; } = true;

        [Description("Seconds before it can be used again (counted from the moment of use).")]
        public double CooldownSeconds { get; set; } = 30;

        [Description("How long the speed boost lasts, in seconds.")]
        public float DurationSeconds { get; set; } = 5;

        [Description("Extra movement speed in percent (1-255).")]
        public int SpeedPercent { get; set; } = 25;
    }
}
