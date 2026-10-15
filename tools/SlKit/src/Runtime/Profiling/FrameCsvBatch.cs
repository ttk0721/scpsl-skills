using System;
using System.Collections.Generic;

namespace SlKit;

/// <summary>One second of samples handed from the main thread to the writer. Pooled.</summary>
internal sealed class FrameCsvBatch
{
    internal readonly double[] Period;
    internal readonly double[] Busy;
    internal readonly List<string> Marks = new List<string>(8);
    internal int Count;
    internal DateTime Utc;
    internal string Path;
    internal int MirrorTickRate;
    internal double MirrorFullUpdateMs;
    internal double ServerTps;
    internal int TargetFrameRate;

    internal FrameCsvBatch(int capacity)
    {
        Period = new double[capacity];
        Busy = new double[capacity];
    }
}
