using SlKit.Core;
using static SlKit.TestSupport.TestKit;

namespace SlKit.Tests;

/// <summary>Tests of ConnectionLimit: the per-host limit policy of the SlHttp transport.</summary>
internal static class ConnectionLimitTests
{
    internal static void Register()
    {
        // Connection limit policy (SlHttp transport)
        Run(nameof(ConnectionLimit_RaisesButNeverLowers), ConnectionLimit_RaisesButNeverLowers);
        Run(nameof(ConnectionLimit_SumSaturatesInsteadOfWrapping), ConnectionLimit_SumSaturatesInsteadOfWrapping);
    }

    // ---------------------------------------------------------------------------------------
    // Connection limit. The SlHttp transport may raise a per-host limit, never lower one.
    // ---------------------------------------------------------------------------------------

    private static void ConnectionLimit_RaisesButNeverLowers()
    {
        Equal(int.MaxValue, ConnectionLimit.Raise(2, ConnectionLimit.Unlimited), "the WebRequest default of 2 is raised");
        Equal(int.MaxValue, ConnectionLimit.Raise(int.MaxValue, 4), "an unlimited handler is never lowered to what SlKit needs");
        Equal(16, ConnectionLimit.Raise(16, 8), "a limit someone raised higher stays");
        Equal(8, ConnectionLimit.Raise(2, 8));
        Equal(5, ConnectionLimit.Raise(5, 0), "wanting nothing changes nothing");
        Equal(1, ConnectionLimit.Raise(0, -3), "never below one connection");
        Equal("unlimited", ConnectionLimit.Describe(int.MaxValue));
        Equal("2", ConnectionLimit.Describe(2));
    }

    private static void ConnectionLimit_SumSaturatesInsteadOfWrapping()
    {
        Equal(8, ConnectionLimit.SaturatingAdd(4, 4));
        Equal(int.MaxValue, ConnectionLimit.SaturatingAdd(int.MaxValue - 1, 4), "saturates, no negative wrap");
        Equal(int.MaxValue, ConnectionLimit.SaturatingAdd(int.MaxValue, int.MaxValue));
        Equal(4, ConnectionLimit.SaturatingAdd(-10, 4), "a negative total counts as zero");
    }
}
