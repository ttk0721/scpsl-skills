using System;
using System.Collections.Generic;
using System.Reflection;
using LabApi.Features.Console;

namespace SlKit;

/// <summary>
/// Lists who is subscribed to the LabAPI events that fire at high rate, so a plugin owner can see
/// which installed plugin pays the per-frame or per-packet cost.
/// </summary>
/// <remarks>
/// Why reflection: LabAPI raises each event through a static field-like <c>event</c> on classes such as
/// <c>PlayerEvents</c>. The C# compiler backs it with a private static delegate field of the
/// same name, and the delegate's invocation list is exactly the set of subscribers. LabAPI offers
/// no public way to enumerate them. Only the events in the built-in hot list (<see cref="HotEvents"/>)
/// are inspected, because a subscriber on a cold event costs nothing worth hunting for; a handler
/// on, say, voice-message or visibility events runs up to hundreds of times per second.
/// The audit is read-only and never throws: a missing class or field (other LabAPI version) is
/// reported in <see cref="HotEventAuditResult.NotInspected"/>. Call it from
/// <c>ServerEvents.WaitingForPlayers</c>, after every plugin had its <c>Enable</c>, and again
/// later if plugins subscribe lazily.
/// Thread affinity: MAIN. Reading a delegate field is atomic, but the event list belongs to the
/// game's main thread and plugins change it there.
/// </remarks>
public static partial class HotEventAudit
{
    private const string HandlersNamespace = "LabApi.Events.Handlers.";

    // Event backing fields are private statics; declared-only keeps a base class from hiding them.
    private const BindingFlags BackingFieldFlags =
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly;

    /// <summary>Catalog names of the hot events this audit inspects, for example <c>PlayerEvents.SendingVoiceMessage</c>.</summary>
    /// <remarks>Thread affinity: any thread. The array is a copy.</remarks>
    public static IReadOnlyList<string> HotEvents => (string[])HotEventNames.Clone();

    /// <summary>Reflects over the hot events and returns every subscriber found.</summary>
    /// <returns>The structured result. Never null, never throws.</returns>
    /// <remarks>Thread affinity: MAIN. Allocates freely; it is a diagnostic, not a hot path.</remarks>
    public static HotEventAuditResult Scan()
    {
        List<HotEventSubscriber> subscribers = new List<HotEventSubscriber>();
        List<string> withSubscribers = new List<string>();
        List<string> notInspected = new List<string>();
        string error = null;

        try
        {
            // Anchor on a type we compile against, so the handlers assembly is the one the game loaded.
            Assembly labApi = typeof(LabApi.Events.Handlers.PlayerEvents).Assembly;

            foreach (string name in HotEventNames)
            {
                try
                {
                    if (!TryReadInvocationList(labApi, name, out Delegate[] list))
                    {
                        notInspected.Add(name);
                        continue;
                    }

                    if (list.Length == 0)
                    {
                        continue;
                    }

                    withSubscribers.Add(name);
                    foreach (Delegate handler in list)
                    {
                        subscribers.Add(Describe(name, handler));
                    }
                }
                catch (Exception)
                {
                    // One odd event must not hide the others.
                    notInspected.Add(name);
                }
            }
        }
        catch (Exception e)
        {
            error = e.GetType().Name + ": " + e.Message;
        }

        return new HotEventAuditResult(subscribers, withSubscribers, notInspected, HotEventNames.Length, error);
    }

    /// <summary>Runs <see cref="Scan"/> and formats it as log lines.</summary>
    /// <returns>
    /// Header, then per event with subscribers one line plus one indented line per handler,
    /// then a per-assembly summary. Never null, never throws.
    /// </returns>
    /// <remarks>Thread affinity: MAIN.</remarks>
    public static IReadOnlyList<string> Report()
    {
        List<string> lines = new List<string>();
        try
        {
            Format(Scan(), lines);
        }
        catch (Exception e)
        {
            lines.Add("HotEventAudit failed: " + e.GetType().Name + ": " + e.Message);
        }

        return lines;
    }

    /// <summary>Runs <see cref="Report"/> and writes each line with LabAPI's logger at Info level.</summary>
    /// <remarks>Thread affinity: MAIN (the scan is; the logger itself is safe anywhere).</remarks>
    public static void Log()
    {
        // One report per call, meant for WaitingForPlayers: a few dozen lines once, not a per-frame path.
        foreach (string line in Report())
        {
            Logger.Info(line); // scpsl-lint: ignore SL008
        }
    }

    private static bool TryReadInvocationList(Assembly labApi, string catalogName, out Delegate[] list)
    {
        list = null;

        int dot = catalogName.IndexOf('.');
        if (dot <= 0 || dot == catalogName.Length - 1)
        {
            return false;
        }

        string className = catalogName.Substring(0, dot);
        string eventName = catalogName.Substring(dot + 1);

        Type handlers = labApi.GetType(HandlersNamespace + className, false);
        FieldInfo field = handlers?.GetField(eventName, BackingFieldFlags);
        if (field == null || !typeof(Delegate).IsAssignableFrom(field.FieldType))
        {
            return false;
        }

        Delegate multicast = (Delegate)field.GetValue(null);
        list = multicast == null ? new Delegate[0] : multicast.GetInvocationList();
        return true;
    }

    private static HotEventSubscriber Describe(string eventName, Delegate handler)
    {
        MethodInfo method = handler.Method;
        Type declaring = method.DeclaringType;

        string assembly = "(dynamic)";
        try
        {
            if (declaring != null)
            {
                AssemblyName an = declaring.Assembly.GetName();
                assembly = an.Name + " " + an.Version;
            }
        }
        catch (Exception)
        {
            // Dynamic or collectible assemblies can refuse GetName; keep the placeholder.
        }

        // The C# compiler puts lambdas in a hidden nested closure class whose name starts with '<';
        // the author wants the outer class they wrote, so walk up past such names.
        Type owner = declaring;
        bool generated = IsGeneratedName(method.Name);
        while (owner != null && IsGeneratedName(owner.Name))
        {
            generated = true;
            owner = owner.DeclaringType;
        }

        return new HotEventSubscriber(
            eventName,
            method.Name,
            declaring?.FullName ?? string.Empty,
            owner?.FullName ?? string.Empty,
            assembly,
            generated);
    }

    private static bool IsGeneratedName(string name)
    {
        return name.IndexOf('<') >= 0;
    }

    private static void Format(HotEventAuditResult result, List<string> lines)
    {
        lines.Add("HotEventAudit (catalog LabAPI " + CatalogLabApiVersion + " / game " + CatalogGameVersion + "): "
            + result.HotEventCount + " hot events, "
            + result.EventsWithSubscribers.Count + " with subscribers, "
            + result.Subscribers.Count + " handlers total");

        if (result.Error != null)
        {
            lines.Add("scan stopped early: " + result.Error);
        }

        if (result.NotInspected.Count > 0)
        {
            lines.Add(result.NotInspected.Count + " hot event(s) not inspected (field not found; other LabAPI version?): "
                + string.Join(", ", result.NotInspected));
        }

        Dictionary<string, int> perAssembly = new Dictionary<string, int>(StringComparer.Ordinal);
        string current = null;
        foreach (HotEventSubscriber s in result.Subscribers)
        {
            if (!string.Equals(current, s.EventName, StringComparison.Ordinal))
            {
                current = s.EventName;
                int count = CountFor(result.Subscribers, current);
                lines.Add(current + ": " + count + (count == 1 ? " handler" : " handlers"));
            }

            lines.Add("  " + s);

            perAssembly.TryGetValue(s.Assembly, out int n);
            perAssembly[s.Assembly] = n + 1;
        }

        if (perAssembly.Count > 0)
        {
            List<string> parts = new List<string>(perAssembly.Count);
            foreach (KeyValuePair<string, int> kv in perAssembly)
            {
                parts.Add(kv.Key + " = " + kv.Value);
            }

            lines.Add("handlers per assembly: " + string.Join("; ", parts));
        }
        else if (result.Error == null)
        {
            lines.Add("no handlers on hot events");
        }
    }

    private static int CountFor(IReadOnlyList<HotEventSubscriber> all, string eventName)
    {
        int n = 0;
        foreach (HotEventSubscriber s in all)
        {
            if (string.Equals(s.EventName, eventName, StringComparison.Ordinal))
            {
                n++;
            }
        }

        return n;
    }
}
