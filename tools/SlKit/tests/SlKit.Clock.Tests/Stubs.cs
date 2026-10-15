using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

// Minimal stand-ins for the Unity and LabAPI members that SlClock, MainThread and SlLog touch, so the
// real sources compile and run on plain .NET. They mimic only the documented behaviour the clock
// relies on (a PlayerLoop tree that can be read and written, a static LabAPI event, and
// Unity's "am I on the main thread" answer); nothing here is copied from the engine.

namespace UnityEngine
{
    /// <summary>Only its assembly is used (to look up Unity's synchronization context type by name).</summary>
    public class Object
    {
    }

    /// <summary>The test advances the frame number by hand, as the engine does once per frame.</summary>
    public static class Time
    {
        public static int frameCount { get; internal set; }
    }

    /// <summary>Same name as Unity's own context type, so the fallback check can find it.</summary>
    internal sealed class UnitySynchronizationContext : System.Threading.SynchronizationContext
    {
    }

    /// <summary>
    /// Stand-in for Unity 6's public <c>Awaitable.MainThreadAsync()</c>. Its awaitable reports
    /// <c>IsCompleted</c> when the caller is on the thread Unity treats as the main thread, which is
    /// the documented behaviour ("completes immediately when already on the main thread").
    /// </summary>
    public class Awaitable
    {
        /// <summary>Managed id of the thread the test treats as Unity's main thread (0 = not set).</summary>
        internal static int MainThreadId;

        public static MainThreadAwaitable MainThreadAsync() => new MainThreadAwaitable(Thread.CurrentThread.ManagedThreadId == MainThreadId);
    }

    public struct MainThreadAwaitable
    {
        private readonly bool _onTestMainThread;

        internal MainThreadAwaitable(bool onTestMainThread)
        {
            _onTestMainThread = onTestMainThread;
        }

        public bool IsCompleted => _onTestMainThread;
    }
}

namespace UnityEngine.PlayerLoop
{
    public struct Initialization
    {
    }

    public struct Update
    {
        public struct ScriptRunBehaviourUpdate
        {
        }
    }

    public struct PostLateUpdate
    {
    }
}

namespace UnityEngine.LowLevel
{
    public struct PlayerLoopSystem
    {
        public delegate void UpdateFunction();

        public Type type;
        public PlayerLoopSystem[] subSystemList;
        public UpdateFunction updateDelegate;
    }

    /// <summary>
    /// A PlayerLoop tree with the phases the clock looks for. <see cref="RunFrame"/> plays the part of
    /// the engine: it calls every update delegate in tree order on the calling thread.
    /// </summary>
    public static class PlayerLoop
    {
        private static PlayerLoopSystem _current = Fresh();

        /// <summary>Managed id of the thread that last wrote the loop (0 = never written).</summary>
        internal static int LastWriterThreadId;

        /// <summary>Number of writes so far.</summary>
        internal static int Writes;

        public static PlayerLoopSystem GetCurrentPlayerLoop() => _current;

        public static void SetPlayerLoop(PlayerLoopSystem loop)
        {
            _current = loop;
            LastWriterThreadId = Thread.CurrentThread.ManagedThreadId;
            Writes++;
        }

        /// <summary>Drops every system that is not part of the default tree (another plugin rebuilding the loop).</summary>
        internal static void ResetToFresh() => _current = Fresh();

        internal static void RunFrame() => Run(_current);

        private static void Run(PlayerLoopSystem node)
        {
            node.updateDelegate?.Invoke();
            if (node.subSystemList == null)
            {
                return;
            }

            for (int i = 0; i < node.subSystemList.Length; i++)
            {
                Run(node.subSystemList[i]);
            }
        }

        private static PlayerLoopSystem Fresh()
        {
            return new PlayerLoopSystem
            {
                subSystemList = new[]
                {
                    new PlayerLoopSystem { type = typeof(UnityEngine.PlayerLoop.Initialization), subSystemList = new PlayerLoopSystem[0] },
                    new PlayerLoopSystem
                    {
                        type = typeof(UnityEngine.PlayerLoop.Update),
                        subSystemList = new[]
                        {
                            new PlayerLoopSystem { type = typeof(UnityEngine.PlayerLoop.Update.ScriptRunBehaviourUpdate) },
                        },
                    },
                    new PlayerLoopSystem { type = typeof(UnityEngine.PlayerLoop.PostLateUpdate), subSystemList = new PlayerLoopSystem[0] },
                },
            };
        }
    }
}

namespace LabApi.Events
{
    public delegate void LabEventHandler();
}

namespace LabApi.Events.Handlers
{
    public static class ServerEvents
    {
        public static event LabApi.Events.LabEventHandler RoundRestarted;

        /// <summary>Number of handlers currently subscribed to <see cref="RoundRestarted"/>.</summary>
        internal static int RoundRestartedSubscribers => RoundRestarted?.GetInvocationList().Length ?? 0;

        /// <summary>Raises the event the way LabAPI does: synchronously, in subscription order.</summary>
        internal static void RaiseRoundRestarted() => RoundRestarted?.Invoke();
    }
}

namespace LabApi.Loader.Features.Plugins
{
    public abstract class Plugin
    {
        public string FilePath { get; set; }
    }
}

namespace LabApi.Loader
{
    /// <summary>Empty plugin and dependency lists: the copy check only reads them for log text.</summary>
    public static class PluginLoader
    {
        public static readonly Dictionary<LabApi.Loader.Features.Plugins.Plugin, System.Reflection.Assembly> Plugins =
            new Dictionary<LabApi.Loader.Features.Plugins.Plugin, System.Reflection.Assembly>();

        public static readonly HashSet<System.Reflection.Assembly> Dependencies = new HashSet<System.Reflection.Assembly>();
    }
}
