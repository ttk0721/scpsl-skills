using System.Collections.Generic;
using System.Text;

namespace SlKit.Core;

/// <summary>Builds the warning SlClock logs when more than one copy of SlKit is loaded.</summary>
/// <remarks>
/// Every copy has its own statics, so N copies mean N PlayerLoop clocks, N main-thread queues
/// (each with its own per-frame budget), N default workers and N rate limiters. That works, but it
/// is rarely intended, and two different versions of a shared <c>SlKit.dll</c> are a binding
/// hazard. Pure formatting, kept here so it is unit-tested. Thread affinity: any thread.
/// </remarks>
public static class CopyReport
{
    /// <summary>
    /// Returns the warning text, or null when <paramref name="others"/> is empty.
    /// </summary>
    /// <param name="self">This copy (its <see cref="SlKitCopy.Location"/> may be null to omit it).</param>
    /// <param name="others">Every other assembly that carries SlKit.</param>
    /// <returns>A multi-line message, or null.</returns>
    public static string Describe(SlKitCopy self, IReadOnlyList<SlKitCopy> others)
    {
        if (others == null || others.Count == 0)
        {
            return null;
        }

        int shared = self.IsSharedLibrary ? 1 : 0;
        bool sourceIncluded = !self.IsSharedLibrary;
        string firstSharedVersion = self.IsSharedLibrary ? self.Version : null;
        bool versionMismatch = false;
        for (int i = 0; i < others.Count; i++)
        {
            SlKitCopy c = others[i];
            if (!c.IsSharedLibrary)
            {
                sourceIncluded = true;
                continue;
            }

            shared++;
            if (firstSharedVersion == null)
            {
                firstSharedVersion = c.Version;
            }
            else if (c.Version != firstSharedVersion)
            {
                versionMismatch = true;
            }
        }

        StringBuilder sb = new StringBuilder(256);
        sb.Append(others.Count + 1).Append(" copies of SlKit are loaded in this process. Each copy has its own clock, ")
          .Append("main-thread queue, background workers and rate limits; nothing is shared between them.");
        sb.Append("\n  this copy: ").Append(Line(self));
        for (int i = 0; i < others.Count; i++)
        {
            sb.Append("\n  other:     ").Append(Line(others[i]));
        }

        if (versionMismatch)
        {
            sb.Append("\n  Different versions of SlKit.dll are loaded: which copy a plugin binds to is decided by the runtime, ")
              .Append("so a plugin may run against a version it was not built for. Keep exactly one SlKit.dll, in LabAPI/dependencies/global.");
        }
        else if (shared >= 2)
        {
            sb.Append("\n  SlKit.dll is present more than once: keep exactly one, in LabAPI/dependencies/global (dependencies are scanned recursively).");
        }

        if (sourceIncluded)
        {
            sb.Append("\n  A plugin compiles SlKit in. That is supported, but give that copy its own namespace and make it internal, ")
              .Append("so it is not confused with the shared SlKit.dll.");
        }

        return sb.ToString();
    }

    private static string Line(SlKitCopy c)
    {
        string where = string.IsNullOrEmpty(c.Location) ? "loaded from bytes" : c.Location;
        return (c.AssemblyName ?? "?") + " " + (c.Version ?? "?")
               + (c.IsSharedLibrary ? " (SlKit.dll)" : " (SlKit compiled into this assembly)")
               + (c.Location == null ? string.Empty : ", " + where);
    }
}
