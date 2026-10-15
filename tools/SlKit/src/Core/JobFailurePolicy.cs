namespace SlKit.Core;

/// <summary>What a job does once it has failed too many times in a row.</summary>
public enum JobFailurePolicy
{
    /// <summary>
    /// Disable the job, then re-enable it after a delay that doubles with every disable since the
    /// last successful run (default). A transient outage (a database restart, a full disk) heals by
    /// itself, while a permanently broken job costs one log line per backoff step.
    /// </summary>
    Backoff = 0,

    /// <summary>Disable the job until something re-enables it by hand.</summary>
    DisablePermanently = 1,
}
