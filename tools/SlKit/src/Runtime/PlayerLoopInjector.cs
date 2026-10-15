using System;
using UnityEngine.LowLevel;

namespace SlKit;

/// <summary>Where a system is placed inside a PlayerLoop phase.</summary>
internal enum LoopPosition
{
    /// <summary>First subsystem of the phase.</summary>
    First,

    /// <summary>Last subsystem of the phase.</summary>
    Last,

    /// <summary>Right after a given anchor subsystem (falls back to <see cref="Last"/> if the anchor is missing).</summary>
    After,
}

/// <summary>
/// Idempotent insertion and removal of marker-typed systems in Unity's CURRENT PlayerLoop.
/// </summary>
/// <remarks>
/// Mirror registers <c>NetworkEarlyUpdate</c>/<c>NetworkLateUpdate</c> into
/// <c>PlayerLoop.GetCurrentPlayerLoop()</c> before the first scene loads. Starting from
/// <c>GetDefaultPlayerLoop()</c> or a hand-built tree would silently drop them and with them all
/// networking, so this helper only ever edits a copy of the current loop and writes it back.
/// A system is identified by its marker <see cref="Type"/> (exact Type identity, not name), which
/// makes insertion idempotent per loaded SlKit assembly.
/// Thread affinity: MAIN THREAD ONLY (PlayerLoop is a Unity engine API).
/// </remarks>
internal static class PlayerLoopInjector
{
    /// <summary>Inserts <paramref name="system"/> unless a system with the same marker type is already present.</summary>
    /// <param name="phase">Top-level phase type, e.g. <c>typeof(UnityEngine.PlayerLoop.Update)</c>.</param>
    /// <param name="system">The system to insert; its <c>type</c> is the marker.</param>
    /// <param name="position">Placement inside the phase.</param>
    /// <param name="anchor">Anchor for <see cref="LoopPosition.After"/>; ignored otherwise.</param>
    /// <param name="anchorFound">False when <see cref="LoopPosition.After"/> fell back to the end of the phase.</param>
    /// <returns>True when inserted now, false when it was already present or the phase was not found.</returns>
    internal static bool TryInsert(Type phase, PlayerLoopSystem system, LoopPosition position, Type anchor, out bool anchorFound)
    {
        anchorFound = position != LoopPosition.After;
        PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
        if (Contains(root, system.type))
        {
            return false;
        }

        if (!InsertInto(ref root, phase, system, position, anchor, ref anchorFound))
        {
            return false;
        }

        PlayerLoop.SetPlayerLoop(root);
        return true;
    }

    /// <summary>True when the current loop contains a system with the given marker type.</summary>
    /// <param name="marker">Marker type to look for.</param>
    /// <returns>Whether the marker is present.</returns>
    internal static bool IsPresent(Type marker) => Contains(PlayerLoop.GetCurrentPlayerLoop(), marker);

    /// <summary>Removes every system with the given marker types from the current loop.</summary>
    /// <param name="markers">Marker types to remove.</param>
    /// <returns>The number of systems removed.</returns>
    internal static int Remove(params Type[] markers)
    {
        PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
        int removed = RemoveFrom(ref root, markers);
        if (removed > 0)
        {
            PlayerLoop.SetPlayerLoop(root);
        }

        return removed;
    }

    private static bool Contains(PlayerLoopSystem node, Type marker)
    {
        if (node.type == marker)
        {
            return true;
        }

        PlayerLoopSystem[] children = node.subSystemList;
        if (children == null)
        {
            return false;
        }

        for (int i = 0; i < children.Length; i++)
        {
            if (Contains(children[i], marker))
            {
                return true;
            }
        }

        return false;
    }

    private static bool InsertInto(ref PlayerLoopSystem node, Type phase, PlayerLoopSystem system, LoopPosition position, Type anchor, ref bool anchorFound)
    {
        if (node.type == phase)
        {
            PlayerLoopSystem[] old = node.subSystemList ?? Array.Empty<PlayerLoopSystem>();
            int index = old.Length;
            if (position == LoopPosition.First)
            {
                index = 0;
            }
            else if (position == LoopPosition.After)
            {
                for (int i = 0; i < old.Length; i++)
                {
                    if (old[i].type == anchor)
                    {
                        index = i + 1;
                        anchorFound = true;
                        break;
                    }
                }
            }

            PlayerLoopSystem[] updated = new PlayerLoopSystem[old.Length + 1];
            Array.Copy(old, 0, updated, 0, index);
            updated[index] = system;
            Array.Copy(old, index, updated, index + 1, old.Length - index);
            node.subSystemList = updated;
            return true;
        }

        PlayerLoopSystem[] children = node.subSystemList;
        if (children == null)
        {
            return false;
        }

        for (int i = 0; i < children.Length; i++)
        {
            if (InsertInto(ref children[i], phase, system, position, anchor, ref anchorFound))
            {
                return true;
            }
        }

        return false;
    }

    private static int RemoveFrom(ref PlayerLoopSystem node, Type[] markers)
    {
        PlayerLoopSystem[] children = node.subSystemList;
        if (children == null)
        {
            return 0;
        }

        int removed = 0;
        int kept = 0;
        for (int i = 0; i < children.Length; i++)
        {
            if (Array.IndexOf(markers, children[i].type) < 0)
            {
                kept++;
            }
        }

        if (kept != children.Length)
        {
            PlayerLoopSystem[] filtered = new PlayerLoopSystem[kept];
            int j = 0;
            for (int i = 0; i < children.Length; i++)
            {
                if (Array.IndexOf(markers, children[i].type) < 0)
                {
                    filtered[j++] = children[i];
                }
            }

            removed = children.Length - kept;
            node.subSystemList = filtered;
            children = filtered;
        }

        for (int i = 0; i < children.Length; i++)
        {
            removed += RemoveFrom(ref children[i], markers);
        }

        return removed;
    }
}
