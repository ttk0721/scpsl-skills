using System;
using System.Collections.Generic;
using System.Linq;
using SlKit.Polyfills;
using static SlKit.TestSupport.TestKit;

// Compares the polyfills with the real System.Linq methods of the test runtime (.NET 10), called
// through their fully qualified static form so the two never compete as extension methods.
internal static class Program
{
    private static int Main()
    {
        Run(nameof(MinMaxMatchLinqAndFirstWinsTies), MinMaxMatchLinqAndFirstWinsTies);
        Run(nameof(EmptyBehaviourMatchesDotNet), EmptyBehaviourMatchesDotNet);
        Run(nameof(NullKeysAreIgnored), NullKeysAreIgnored);
        Run(nameof(CustomComparerIsUsed), CustomComparerIsUsed);
        Run(nameof(DistinctByKeepsFirstAndIsLazy), DistinctByKeepsFirstAndIsLazy);
        Run(nameof(ChunkMatchesLinq), ChunkMatchesLinq);
        Run(nameof(ChunkArraysAreIndependent), ChunkArraysAreIndependent);
        Run(nameof(ArgumentsAreValidatedEagerly), ArgumentsAreValidatedEagerly);

        return Finish();
    }

    private sealed class Item
    {
        public Item(string name, int weight, string tag)
        {
            Name = name;
            Weight = weight;
            Tag = tag;
        }

        public string Name { get; }
        public int Weight { get; }
        public string Tag { get; }
    }

    private static void MinMaxMatchLinqAndFirstWinsTies()
    {
        Item[] items =
        {
            new Item("a", 5, "x"), new Item("b", 2, "x"), new Item("c", 9, "y"),
            new Item("d", 2, "y"), new Item("e", 9, "z"),
        };

        True(ReferenceEquals(EnumerablePolyfills.MinBy(items, i => i.Weight), Enumerable.MinBy(items, i => i.Weight)), "MinBy same element");
        True(ReferenceEquals(EnumerablePolyfills.MaxBy(items, i => i.Weight), Enumerable.MaxBy(items, i => i.Weight)), "MaxBy same element");
        Equal("b", EnumerablePolyfills.MinBy(items, i => i.Weight).Name);
        Equal("c", EnumerablePolyfills.MaxBy(items, i => i.Weight).Name);
    }

    private static void EmptyBehaviourMatchesDotNet()
    {
        True(EnumerablePolyfills.MinBy(new string[0], s => s.Length) == null, "empty ref type gives null");
        True(EnumerablePolyfills.MaxBy(new int?[0], v => v) == null, "empty Nullable<int> gives null");
        Throws<InvalidOperationException>(() => EnumerablePolyfills.MinBy(new int[0], v => v));
        Throws<InvalidOperationException>(() => EnumerablePolyfills.MaxBy(new int[0], v => v));
    }

    private static void NullKeysAreIgnored()
    {
        Item[] items = { new Item("a", 1, null), new Item("b", 1, "m"), new Item("c", 1, null), new Item("d", 1, "k") };

        Equal(Enumerable.MinBy(items, i => i.Tag).Name, EnumerablePolyfills.MinBy(items, i => i.Tag).Name);
        Equal(Enumerable.MaxBy(items, i => i.Tag).Name, EnumerablePolyfills.MaxBy(items, i => i.Tag).Name);
        Equal("d", EnumerablePolyfills.MinBy(items, i => i.Tag).Name);
        Equal("b", EnumerablePolyfills.MaxBy(items, i => i.Tag).Name);

        Item[] allNull = { new Item("p", 1, null), new Item("q", 1, null) };
        Equal(Enumerable.MinBy(allNull, i => i.Tag).Name, EnumerablePolyfills.MinBy(allNull, i => i.Tag).Name);
    }

    private static void CustomComparerIsUsed()
    {
        int[] values = { 3, 1, 2 };
        IComparer<int> reversed = Comparer<int>.Create((x, y) => y.CompareTo(x));
        Equal(3, EnumerablePolyfills.MinBy(values, v => v, reversed));
        Equal(1, EnumerablePolyfills.MaxBy(values, v => v, reversed));
    }

    private static void DistinctByKeepsFirstAndIsLazy()
    {
        int calls = 0;
        Item[] items = { new Item("a", 1, "x"), new Item("b", 2, "x"), new Item("c", 3, "y"), new Item("d", 4, "X") };

        IEnumerable<Item> query = EnumerablePolyfills.DistinctBy(items, i => { calls++; return i.Tag; });
        Equal(0, calls);

        string[] names = query.Select(i => i.Name).ToArray();
        Equal("a,c,d", string.Join(",", names));
        Equal(4, calls);

        string[] ignoreCase = EnumerablePolyfills.DistinctBy(items, i => i.Tag, StringComparer.OrdinalIgnoreCase)
            .Select(i => i.Name).ToArray();
        Equal("a,c", string.Join(",", ignoreCase));

        Equal(
            string.Join(",", Enumerable.DistinctBy(items, i => i.Tag).Select(i => i.Name)),
            string.Join(",", names));
    }

    private static void ChunkMatchesLinq()
    {
        foreach (int count in new[] { 0, 1, 2, 3, 5, 6, 7 })
        {
            int[] source = Enumerable.Range(0, count).ToArray();
            string expected = Render(Enumerable.Chunk(source, 3));
            string actual = Render(EnumerablePolyfills.Chunk(source, 3));
            Equal(expected, actual);
        }

        Equal("[0][1][2]", Render(EnumerablePolyfills.Chunk(new[] { 0, 1, 2 }, 1)));
        Equal("[0,1,2]", Render(EnumerablePolyfills.Chunk(new[] { 0, 1, 2 }, 10)));
    }

    private static void ChunkArraysAreIndependent()
    {
        int[][] chunks = EnumerablePolyfills.Chunk(new[] { 1, 2, 3, 4 }, 2).ToArray();
        chunks[0][0] = 99;
        Equal(3, chunks[1][0]);
        True(!ReferenceEquals(chunks[0], chunks[1]), "distinct arrays");
    }

    private static void ArgumentsAreValidatedEagerly()
    {
        Throws<ArgumentNullException>(() => EnumerablePolyfills.Chunk<int>(null, 2));
        Throws<ArgumentOutOfRangeException>(() => EnumerablePolyfills.Chunk(new[] { 1 }, 0));
        Throws<ArgumentNullException>(() => EnumerablePolyfills.DistinctBy<int, int>(null, v => v));
        Throws<ArgumentNullException>(() => EnumerablePolyfills.DistinctBy(new[] { 1 }, (Func<int, int>)null));
        Throws<ArgumentNullException>(() => EnumerablePolyfills.MinBy<int, int>(null, v => v));
        Throws<ArgumentNullException>(() => EnumerablePolyfills.MaxBy(new[] { 1 }, (Func<int, int>)null));
    }

    private static string Render(IEnumerable<int[]> chunks)
    {
        return string.Concat(chunks.Select(c => "[" + string.Join(",", c) + "]"));
    }
}
