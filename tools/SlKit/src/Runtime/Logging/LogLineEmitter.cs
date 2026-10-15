using System;
using LabApi.Features.Console;

namespace SlKit;

/// <summary>Writes one line to LabAPI's logger at the level of a <see cref="RateLimitedLogger"/> call, and never throws.</summary>
internal static class LogLineEmitter
{
    internal static void Emit(RateLimitedLogger.Level level, string line)
    {
        try
        {
            switch (level)
            {
                case RateLimitedLogger.Level.Error:
                    Logger.Error(line);
                    break;
                case RateLimitedLogger.Level.Warn:
                    Logger.Warn(line);
                    break;
                default:
                    Logger.Info(line);
                    break;
            }
        }
        catch (Exception)
        {
            // A log call must never take the caller down.
        }
    }
}
