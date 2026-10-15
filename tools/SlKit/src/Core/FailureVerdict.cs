namespace SlKit.Core;

/// <summary>Result of <see cref="FailureBackoff.RecordFailure"/>.</summary>
public enum FailureVerdict
{
    /// <summary>The failure was counted; the job keeps running.</summary>
    Counted = 0,

    /// <summary>The job was disabled and will be re-enabled at <see cref="FailureBackoff.ReenableAt"/>.</summary>
    DisabledWithBackoff = 1,

    /// <summary>The job was disabled until it is re-enabled by hand.</summary>
    DisabledPermanently = 2,
}
