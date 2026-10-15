using System;
using System.Collections.Generic;
using SlKit.Core;
using static SlKit.TestSupport.TestKit;

namespace SlKit.Tests;

/// <summary>Tests of RingBuffer: the window of the last N samples.</summary>
internal static class RingBufferTests
{
    internal static void Register()
    {
        // Ring buffer
        Run(nameof(Ring_FillsThenWrapsOverwritingOldest), Ring_FillsThenWrapsOverwritingOldest);
        Run(nameof(Ring_CopyLastSpansTheWrapPoint), Ring_CopyLastSpansTheWrapPoint);
        Run(nameof(Ring_CopyLastClampsAndHonoursOffset), Ring_CopyLastClampsAndHonoursOffset);
        Run(nameof(Ring_LastTracksNewestAcrossWrap), Ring_LastTracksNewestAcrossWrap);
        Run(nameof(Ring_ClearKeepsTotalAddedAndRestartsOrder), Ring_ClearKeepsTotalAddedAndRestartsOrder);
        Run(nameof(Ring_CapacityOneAndValidation), Ring_CapacityOneAndValidation);
        Run(nameof(Ring_ManyWrapsStayConsistent), Ring_ManyWrapsStayConsistent);
    }

    // ---------------------------------------------------------------------------------------
    // Ring buffer
    // ---------------------------------------------------------------------------------------

    private static double[] Dump(RingBuffer ring)
    {
        double[] buffer = new double[ring.Count];
        int copied = ring.CopyTo(buffer);
        Equal(ring.Count, copied, "CopyTo returns Count");
        return buffer;
    }

    private static void Ring_FillsThenWrapsOverwritingOldest()
    {
        RingBuffer ring = new RingBuffer(4);
        Equal(4, ring.Capacity);
        Equal(0, ring.Count);
        SequenceEqual(new double[0], Dump(ring));

        ring.Add(1); ring.Add(2); ring.Add(3);
        Equal(3, ring.Count);
        SequenceEqual(new double[] { 1, 2, 3 }, Dump(ring));

        ring.Add(4);
        Equal(4, ring.Count);
        SequenceEqual(new double[] { 1, 2, 3, 4 }, Dump(ring));

        ring.Add(5); // overwrites 1
        Equal(4, ring.Count, "count never exceeds capacity");
        SequenceEqual(new double[] { 2, 3, 4, 5 }, Dump(ring));

        ring.Add(6); ring.Add(7);
        SequenceEqual(new double[] { 4, 5, 6, 7 }, Dump(ring));
        Equal(7L, ring.TotalAdded, "TotalAdded counts overwritten samples too");
    }

    private static void Ring_CopyLastSpansTheWrapPoint()
    {
        RingBuffer ring = new RingBuffer(5);
        for (int i = 1; i <= 7; i++) // 6 and 7 wrap to the front: storage is [6,7,3,4,5]
        {
            ring.Add(i);
        }

        double[] last3 = new double[3];
        Equal(3, ring.CopyLast(3, last3, 0));
        SequenceEqual(new double[] { 5, 6, 7 }, last3);

        double[] last4 = new double[4];
        Equal(4, ring.CopyLast(4, last4, 0));
        SequenceEqual(new double[] { 4, 5, 6, 7 }, last4);

        double[] all = new double[5];
        Equal(5, ring.CopyLast(5, all, 0));
        SequenceEqual(new double[] { 3, 4, 5, 6, 7 }, all);
    }

    private static void Ring_CopyLastClampsAndHonoursOffset()
    {
        RingBuffer ring = new RingBuffer(5);
        ring.Add(1); ring.Add(2); ring.Add(3);

        double[] dest = new double[8];
        Equal(3, ring.CopyLast(100, dest, 2), "asking for more than held copies what exists");
        SequenceEqual(new double[] { 0, 0, 1, 2, 3, 0, 0, 0 }, dest);

        Equal(0, ring.CopyLast(0, dest, 0), "zero requested");
        Equal(0, ring.CopyLast(-3, dest, 0), "negative requested");
        Equal(0, new RingBuffer(3).CopyLast(2, dest, 0), "empty ring");

        Throws<ArgumentNullException>(() => ring.CopyLast(1, null, 0), "null destination");
        Throws<ArgumentException>(() => ring.CopyLast(3, new double[2], 0), "destination too small");
        Throws<ArgumentException>(() => ring.CopyLast(3, new double[4], 2), "offset leaves too little room");
        Throws<ArgumentException>(() => ring.CopyLast(1, new double[4], -1), "negative offset");
    }

    private static void Ring_LastTracksNewestAcrossWrap()
    {
        RingBuffer ring = new RingBuffer(3);
        Near(0, ring.Last, 0, "empty buffer reports 0");
        ring.Add(10);
        Near(10, ring.Last);
        ring.Add(20); ring.Add(30);
        Near(30, ring.Last, 0, "newest is the last slot");
        ring.Add(40); // wraps to slot 0
        Near(40, ring.Last, 0, "newest after wrapping to slot 0");
        ring.Add(50);
        Near(50, ring.Last);
    }

    private static void Ring_ClearKeepsTotalAddedAndRestartsOrder()
    {
        RingBuffer ring = new RingBuffer(3);
        for (int i = 1; i <= 5; i++)
        {
            ring.Add(i);
        }

        ring.Clear();
        Equal(0, ring.Count);
        Equal(5L, ring.TotalAdded, "history counter survives Clear");
        Near(0, ring.Last, 0);
        SequenceEqual(new double[0], Dump(ring));

        ring.Add(9); ring.Add(8);
        SequenceEqual(new double[] { 9, 8 }, Dump(ring));
        Equal(7L, ring.TotalAdded);
    }

    private static void Ring_CapacityOneAndValidation()
    {
        RingBuffer one = new RingBuffer(1);
        one.Add(1); one.Add(2); one.Add(3);
        Equal(1, one.Count);
        Near(3, one.Last);
        SequenceEqual(new double[] { 3 }, Dump(one));

        Throws<ArgumentOutOfRangeException>(() => new RingBuffer(0), "capacity 0");
        Throws<ArgumentOutOfRangeException>(() => new RingBuffer(-1), "capacity -1");
    }

    private static void Ring_ManyWrapsStayConsistent()
    {
        // Model check against a plain list across many wraps and odd capacities.
        int[] capacities = { 2, 3, 7, 64 };
        foreach (int capacity in capacities)
        {
            RingBuffer ring = new RingBuffer(capacity);
            List<double> model = new List<double>();
            for (int i = 1; i <= capacity * 5 + 3; i++)
            {
                ring.Add(i);
                model.Add(i);
                if (model.Count > capacity)
                {
                    model.RemoveAt(0);
                }

                Equal(model.Count, ring.Count, "count, capacity " + capacity + " step " + i);
                Near(i, ring.Last, 0, "last, capacity " + capacity + " step " + i);
                if (i % 5 == 0)
                {
                    SequenceEqual(model.ToArray(), Dump(ring), "contents, capacity " + capacity + " step " + i);
                }
            }
        }
    }
}
