using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using LabApi.Events.Arguments.Interfaces;
using LabApi.Events.Handlers;
using LabApi.Features.Console;
using SlKit;

namespace Examples.DebugDoctor
{
    /// <summary>
    /// Resolves a LabAPI event by its catalog name (<c>PlayerEvents.Joined</c>) through reflection and
    /// reads its subscriber list. LabAPI exposes no public list of subscribers, but every event is a
    /// field-like static event, so the private backing field holds the multicast delegate.
    /// </summary>
    internal static class EventLookup
    {
        private const string HandlersNamespace = "LabApi.Events.Handlers.";

        public static bool TryResolve(string name, out EventInfo ev, out FieldInfo field, out string error)
        {
            ev = null;
            field = null;
            error = null;

            int dot = name.IndexOf('.');
            if (dot <= 0 || dot == name.Length - 1)
            {
                error = "Use Class.Event, for example PlayerEvents.Joined (names are case-sensitive).";
                return false;
            }

            string className = name.Substring(0, dot);
            string eventName = name.Substring(dot + 1);

            // ServerEvents lives in the same assembly as every other handler class.
            Type handlers = typeof(ServerEvents).Assembly.GetType(HandlersNamespace + className, false);
            if (handlers == null)
            {
                error = "No class " + HandlersNamespace + className + " in this LabApi.dll.";
                return false;
            }

            ev = handlers.GetEvent(eventName, BindingFlags.Public | BindingFlags.Static);
            field = handlers.GetField(
                eventName,
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);

            if (ev == null || field == null)
            {
                error = className + " has no event '" + eventName + "' (or its backing field is not readable). "
                    + "Check the spelling against the event catalog.";
                return false;
            }

            return true;
        }

        public static IReadOnlyList<string> Subscribers(FieldInfo field)
        {
            List<string> lines = new List<string>();
            Delegate multicast = field.GetValue(null) as Delegate;
            if (multicast == null)
                return lines;

            // Invocation order is registration order: the last line runs last and wins a contested setter.
            Delegate[] list = multicast.GetInvocationList();
            for (int i = 0; i < list.Length; i++)
                lines.Add("#" + (i + 1).ToString(CultureInfo.InvariantCulture) + " " + Describe(list[i]));

            return lines;
        }

        private static string Describe(Delegate handler)
        {
            MethodInfo method = handler.Method;
            Type declaring = method.DeclaringType;

            // Lambdas live in a hidden nested class whose name contains '<'. Walk up to the class the author wrote.
            Type owner = declaring;
            bool lambda = method.Name.IndexOf('<') >= 0;
            while (owner != null && owner.Name.IndexOf('<') >= 0)
            {
                lambda = true;
                owner = owner.DeclaringType;
            }

            string assembly = "?";
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
                // Dynamic assemblies can refuse GetName; keep the placeholder.
            }

            // Target is the object the delegate is bound to (null for static handlers). For handlers registered
            // through a CustomEventsHandler it may help to name the plugin's handler class; not verified on a live server.
            string target = handler.Target == null ? string.Empty : " target=" + handler.Target.GetType().Name;

            return (owner != null ? owner.FullName : "?") + "." + method.Name
                + (lambda ? " (lambda)" : string.Empty) + " [" + assembly + "]" + target;
        }
    }

    /// <summary>
    /// One counting probe: a temporary subscriber that only counts, then reports and removes itself.
    /// </summary>
    internal sealed class ProbeSession
    {
        private long _raised;
        private long _offMainThread;
        private long _cancellable;
        private long _alreadyCancelled;

        public string Name;
        public EventInfo Event;
        public FieldInfo Field;
        public Delegate Handler;
        public IDisposable Timer;
        public long StartTimestamp;
        public int SubscribersAtStart;
        public bool IdleAtStart;

        public long Raised => Interlocked.Read(ref _raised);
        public long OffMainThread => Interlocked.Read(ref _offMainThread);
        public long Cancellable => Interlocked.Read(ref _cancellable);
        public long AlreadyCancelled => Interlocked.Read(ref _alreadyCancelled);

        // Both entry points are invoked by the game inside the event raise. They never throw, never log
        // and never allocate: a probe on a busy event must stay close to free.
        public void OnRaised<T>(T args) where T : EventArgs
        {
            Interlocked.Increment(ref _raised);
            if (!MainThread.IsMainThread)
                Interlocked.Increment(ref _offMainThread);

            ICancellableEvent cancellable = args as ICancellableEvent;
            if (cancellable != null)
            {
                Interlocked.Increment(ref _cancellable);

                // Subscribed last, so this sees every cancel written by handlers registered earlier.
                if (!cancellable.IsAllowed)
                    Interlocked.Increment(ref _alreadyCancelled);
            }
        }

        public void OnRaisedNoArgs()
        {
            Interlocked.Increment(ref _raised);
            if (!MainThread.IsMainThread)
                Interlocked.Increment(ref _offMainThread);
        }
    }

    /// <summary>Starts, finishes and reports counting probes. Main thread only.</summary>
    internal static class EventProbe
    {
        public const int MaxActive = 4;
        public const int MaxSeconds = 300;

        private static readonly Dictionary<string, ProbeSession> Active =
            new Dictionary<string, ProbeSession>(StringComparer.Ordinal);

        public static bool Start(string name, int seconds, bool force, out string response)
        {
            if (Active.ContainsKey(name))
            {
                response = name + " is already being counted.";
                return false;
            }

            if (Active.Count >= MaxActive)
            {
                response = "At most " + MaxActive + " probes at once.";
                return false;
            }

            if (!EventLookup.TryResolve(name, out EventInfo ev, out FieldInfo field, out string error))
            {
                response = error;
                return false;
            }

            // The catalog marks hot events (per frame, per pair, per packet). A counter is cheap, but the
            // game builds the args for every raise anyway, so adding a subscriber to the hottest ones on a full
            // server is exactly what a measurement should not do by accident.
            if (!force && IsHot(name))
            {
                response = name + " is a hot event (see the event catalog). Add 'force' to count it anyway.";
                return false;
            }

            ProbeSession session = new ProbeSession
            {
                Name = name,
                Event = ev,
                Field = field,
                SubscribersAtStart = EventLookup.Subscribers(field).Count,
                IdleAtStart = LabApi.Features.Wrappers.Server.IdleModeActive,
                StartTimestamp = Stopwatch.GetTimestamp(),
            };

            if (!TryCreateHandler(session, out string handlerError))
            {
                response = handlerError;
                return false;
            }

            ev.AddEventHandler(null, session.Handler);
            try
            {
                // ClockScope.Server: a Round-scoped job would be cancelled by a restart and leave the probe subscribed.
                session.Timer = SlClock.After(
                    TimeSpan.FromSeconds(seconds),
                    () => Finish(session),
                    "doctor.count." + name,
                    ClockScope.Server);
            }
            catch (Exception e)
            {
                // No timer means nobody would ever remove the handler: take it back out now.
                ev.RemoveEventHandler(null, session.Handler);
                response = "Could not schedule the probe: " + e.GetType().Name + " " + e.Message;
                return false;
            }

            Active[name] = session;

            response = "Counting " + name + " for " + seconds + " s. The result goes to the server log.";
            return true;
        }

        private static bool IsHot(string name)
        {
            foreach (string hot in HotEventAudit.HotEvents)
            {
                if (string.Equals(hot, name, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static bool TryCreateHandler(ProbeSession session, out string error)
        {
            error = null;
            try
            {
                Type handlerType = session.Event.EventHandlerType;
                ParameterInfo[] parameters = handlerType.GetMethod("Invoke").GetParameters();

                MethodInfo target;
                if (parameters.Length == 0)
                {
                    target = typeof(ProbeSession).GetMethod(nameof(ProbeSession.OnRaisedNoArgs));
                }
                else if (parameters.Length == 1)
                {
                    target = typeof(ProbeSession).GetMethod(nameof(ProbeSession.OnRaised))
                        .MakeGenericMethod(parameters[0].ParameterType);
                }
                else
                {
                    error = "Unsupported handler shape (" + parameters.Length + " parameters).";
                    return false;
                }

                session.Handler = Delegate.CreateDelegate(handlerType, session, target);
                return true;
            }
            catch (Exception e)
            {
                error = "Could not build a handler for " + session.Name + ": " + e.GetType().Name + " " + e.Message;
                return false;
            }
        }

        private static void Finish(ProbeSession session)
        {
            // Forget the session first: if the removal below throws, the name must not stay "already being counted".
            Active.Remove(session.Name);
            try
            {
                session.Event.RemoveEventHandler(null, session.Handler);
                Logger.Info(Report(session));
            }
            catch (Exception e)
            {
                Logger.Error("DebugDoctor: finishing " + session.Name + " failed: " + e);
            }
        }

        private static string Report(ProbeSession s)
        {
            double elapsed = (Stopwatch.GetTimestamp() - s.StartTimestamp) / (double)Stopwatch.Frequency;
            long raised = s.Raised;
            StringBuilder sb = new StringBuilder();

            sb.Append("DebugDoctor count ").Append(s.Name).Append(": raised ").Append(raised)
                .Append(" times in ").Append(elapsed.ToString("F1", CultureInfo.InvariantCulture)).Append(" s (")
                .Append((raised / Math.Max(elapsed, 0.001)).ToString("F2", CultureInfo.InvariantCulture)).Append("/s). ")
                .Append("Subscribers when started: ").Append(s.SubscribersAtStart).Append(". ");

            if (s.OffMainThread > 0)
                sb.Append("RAISED OFF THE MAIN THREAD ").Append(s.OffMainThread).Append(" times: handlers must not touch game objects. ");

            if (s.Cancellable > 0)
            {
                sb.Append(s.AlreadyCancelled).Append(" of ").Append(s.Cancellable)
                    .Append(" raises were already cancelled by an earlier handler when the probe saw them. ");
            }

            if (s.IdleAtStart || LabApi.Features.Wrappers.Server.IdleModeActive)
                sb.Append("Server was in idle mode during the window: few events, slow frames. ");

            if (raised == 0)
            {
                sb.Append("Never raised: either the action did not happen in this window, the event is declared but never "
                    + "raised by the game (catalog), or a patch skipped the code that raises it.");
            }

            return sb.ToString();
        }
    }
}
