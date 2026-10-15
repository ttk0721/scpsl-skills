using System.Collections.Generic;

namespace SlKit;

/// <summary>Structured outcome of <see cref="HotEventAudit.Scan"/>.</summary>
/// <remarks>Plain immutable data, safe to hand to any thread.</remarks>
public sealed class HotEventAuditResult
{
    internal HotEventAuditResult(
        IReadOnlyList<HotEventSubscriber> subscribers,
        IReadOnlyList<string> eventsWithSubscribers,
        IReadOnlyList<string> notInspected,
        int hotEventCount,
        string error)
    {
        Subscribers = subscribers;
        EventsWithSubscribers = eventsWithSubscribers;
        NotInspected = notInspected;
        HotEventCount = hotEventCount;
        Error = error;
    }

    /// <summary>Every handler found on a hot event, in hot-list order.</summary>
    public IReadOnlyList<HotEventSubscriber> Subscribers { get; }

    /// <summary>Catalog names of the hot events that have at least one subscriber.</summary>
    public IReadOnlyList<string> EventsWithSubscribers { get; }

    /// <summary>
    /// Hot events that could not be inspected: the handler class or the backing field was not
    /// found (a LabAPI other than 1.1.7 renamed or removed it) or reading it failed.
    /// </summary>
    public IReadOnlyList<string> NotInspected { get; }

    /// <summary>Number of events in the built-in hot list.</summary>
    public int HotEventCount { get; }

    /// <summary>Unexpected failure that cut the scan short, or null. The scan itself never throws.</summary>
    public string Error { get; }
}
