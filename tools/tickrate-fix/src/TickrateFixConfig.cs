using System.ComponentModel;

namespace TickrateFix
{
    /// <summary>
    /// Configuration, stored by LabAPI as YAML in <c>configs/&lt;port&gt;/TickrateFix/config.yml</c>
    /// (property names become <c>enabled</c> and <c>cap</c>).
    /// </summary>
    /// <remarks>
    /// Both values are read when they are used (at every <c>WaitingForPlayers</c>), not cached, because
    /// <c>labapi reload configs</c> replaces the whole object. A change therefore applies from the next
    /// round's lobby. Thread affinity: plain data; LabAPI creates and fills it on the main thread.
    /// </remarks>
    public sealed class TickrateFixConfig
    {
        /// <summary>
        /// Whether the plugin re-applies <c>server_tickrate</c>. Off by default: the behaviour has been
        /// derived from reading code, not measured, so the server owner turns it on deliberately, after
        /// looking at the <c>targetFrameRate before=... after=...</c> lines it logs while off.
        /// </summary>
        [Description("Re-apply server_tickrate at every WaitingForPlayers. false (default) only logs targetFrameRate and changes nothing.")]
        public bool Enabled { get; set; }

        /// <summary>
        /// Frame-rate target used instead of a <c>server_tickrate</c> of -1 or 0 (unlimited), 1 to 32767;
        /// other values fall back to 60. A <c>server_tickrate</c> above it is NOT lowered: it is applied as
        /// configured, with one warning.
        /// </summary>
        [Description("Frame rate used when server_tickrate is -1 or 0 (unlimited); 1-32767, otherwise 60. A higher server_tickrate is never lowered.")]
        public int Cap { get; set; } = TickratePolicy.DefaultCap;
    }
}
