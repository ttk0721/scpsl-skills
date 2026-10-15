using System;
using System.Threading;
using SlKit.Core;

namespace SlKit;

/// <summary>
/// Handle of one <see cref="SlClock"/> job, returned by <see cref="SlClock.Every"/>,
/// <see cref="SlClock.After"/> and <see cref="SlClock.EveryStaggered{T}"/>. Dispose it to cancel the
/// job; use <see cref="Reenable"/> to bring back a job that was disabled after repeated failures.
/// </summary>
/// <remarks>
/// The failure settings come from the <see cref="ClockJobOptions"/> given at registration and belong
/// to this job alone; nothing another plugin does changes them.
/// Thread affinity: <see cref="Dispose"/>, <see cref="Name"/>, <see cref="Scope"/>,
/// <see cref="IsDisposed"/>, <see cref="MaxConsecutiveFailures"/>, <see cref="ReenableBaseDelay"/> and
/// <see cref="ReenableMaxDelay"/> are safe from any thread (disposing only sets a flag; removal
/// happens on the main thread at the end of the clock's frame). Every other member is MAIN THREAD
/// only. Only SlKit creates handles.
/// </remarks>
public abstract class ClockJobHandle : IDisposable
{
    private int _disposed;

    internal ClockJobHandle(string name, ClockScope scope, JobFailureSettings settings)
    {
        Name = string.IsNullOrEmpty(name) ? "(unnamed)" : name;
        Scope = scope;
        Settings = settings;
        FailurePolicy = settings.Policy;
    }

    /// <summary>The name given at registration ("(unnamed)" when none). Thread affinity: any thread.</summary>
    public string Name { get; }

    /// <summary>The job's scope. Thread affinity: any thread.</summary>
    public ClockScope Scope { get; }

    /// <summary>True once the job was disposed or, for a one-shot job, has run. Thread affinity: any thread.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// True while the job is disabled after <see cref="MaxConsecutiveFailures"/> failures in a row.
    /// Thread affinity: MAIN THREAD.
    /// </summary>
    public bool IsDisabled => Backoff.IsDisabled;

    /// <summary>
    /// What happens when the job fails <see cref="MaxConsecutiveFailures"/> times in a row:
    /// <see cref="JobFailurePolicy.Backoff"/> (re-enable after a growing delay) or
    /// <see cref="JobFailurePolicy.DisablePermanently"/>. Starts as
    /// <see cref="ClockJobOptions.FailurePolicy"/> of the registration (Backoff when none was given);
    /// a change applies to the next disable of this job only. Thread affinity: MAIN THREAD.
    /// </summary>
    public JobFailurePolicy FailurePolicy { get; set; }

    /// <summary>Failures in a row that disable this job (from its <see cref="ClockJobOptions"/>, default 5). Thread affinity: any thread.</summary>
    public int MaxConsecutiveFailures => Settings.MaxConsecutiveFailures;

    /// <summary>This job's first backoff delay (default 30 s). Thread affinity: any thread.</summary>
    public TimeSpan ReenableBaseDelay => StopwatchTime.ToTimeSpan(Settings.BaseDelayTicks);

    /// <summary>This job's longest backoff delay (default 30 min). Thread affinity: any thread.</summary>
    public TimeSpan ReenableMaxDelay => StopwatchTime.ToTimeSpan(Settings.MaxDelayTicks);

    /// <summary>Validated copy of the registration's options; immutable.</summary>
    internal readonly JobFailureSettings Settings;

    /// <summary>Failure bookkeeping; main thread only.</summary>
    internal FailureBackoff Backoff;

    /// <summary>
    /// Re-enables the job now if it is disabled; its failure count starts again from zero. Use it from
    /// an admin command once the cause is fixed. Logged. Thread affinity: MAIN THREAD.
    /// </summary>
    /// <returns>True when the job was disabled and is now enabled; false when there was nothing to do.</returns>
    public bool Reenable()
    {
        MainThread.ThrowIfNotMainThread("ClockJobHandle.Reenable");
        if (IsDisposed || !Backoff.IsDisabled)
        {
            return false;
        }

        Backoff.Reenable();
        SlLog.Info("Job '" + Name + "' re-enabled by request.");
        return true;
    }

    /// <summary>Cancels the job. Idempotent. Thread affinity: any thread.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            SlClock.MarkDirty();
        }
    }
}
