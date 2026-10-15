namespace SlKit;

/// <summary>How long a <see cref="SlClock"/> job lives when nobody disposes it.</summary>
/// <remarks>
/// The game never calls <c>Plugin.Disable()</c> and the PlayerLoop survives scene loads, so a
/// job is never cleaned up implicitly. <see cref="Round"/> exists because per-round work that
/// keeps running into the next round touches destroyed objects and stale players.
/// </remarks>
public enum ClockScope
{
    /// <summary>
    /// Lives until its handle is disposed. A job disabled after repeated failures stays registered
    /// (see <see cref="SlKit.Core.ClockJobOptions.FailurePolicy"/>).
    /// </summary>
    Server = 0,

    /// <summary>
    /// Like <see cref="Server"/>, and additionally cancelled by <c>SlClock.OnRoundRestart</c>,
    /// which <see cref="SlClock.Install"/> subscribes to <c>ServerEvents.RoundRestarted</c>. Every
    /// registration runs <c>Install</c>, so no further wiring is needed. <c>RoundRestarted</c> is not
    /// raised by <c>softrestart</c>, which ends the process anyway.
    /// </summary>
    Round = 1,
}
