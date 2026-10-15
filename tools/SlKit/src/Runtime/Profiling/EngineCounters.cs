using System;

namespace SlKit;

/// <summary>Reads the engine's own frame and network numbers (Mirror, LabAPI, Unity) that the profiler reports next to its measurement. Main thread.</summary>
internal static class EngineCounters
{
    internal static void Read(out int mirrorTickRate, out double mirrorFullUpdateMs, out double serverTps, out int targetFrameRate)
    {
        mirrorTickRate = 0;
        mirrorFullUpdateMs = 0;
        serverTps = 0;
        targetFrameRate = 0;
        try
        {
            if (Mirror.NetworkServer.active)
            {
                mirrorTickRate = Mirror.NetworkServer.actualTickRate;
                mirrorFullUpdateMs = Mirror.NetworkServer.fullUpdateDuration.average * 1000.0;
            }

            // Deliberate: the snapshot reports Server.Tps next to the real frame times so the gap
            // between the smoothed value and the measurement is visible. It is never used as a metric.
            // scpsl-lint: ignore SL004
            serverTps = LabApi.Features.Wrappers.Server.Tps;
            targetFrameRate = UnityEngine.Application.targetFrameRate;
        }
        catch (Exception)
        {
            // Engine counters are informational; a missing one must not break measurement.
        }
    }
}
