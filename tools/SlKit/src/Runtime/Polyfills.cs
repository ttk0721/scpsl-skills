using System;
using System.Collections.Generic;

namespace SlKit.Polyfills;

/// <summary>
/// <c>MinBy</c>, <c>MaxBy</c>, <c>DistinctBy</c> and <c>Chunk</c>, which exist in .NET 6+ but not in
/// the server's .NET Framework 4.8 / Mono BCL (checked by compiling against the game's own
/// <c>System.Core.dll</c>: they do not resolve). Behaviour follows the .NET 6 originals so code
/// moves unchanged if the game ever upgrades.
/// </summary>
/// <remarks>
/// Add <c>using SlKit.Polyfills;</c> to use them. They are LINQ-style: they allocate (an enumerator,
/// a <see cref="HashSet{T}"/>, chunk arrays), so keep them out of per-frame code. Pure, no game
/// calls. Thread affinity: any thread (the sources you pass in follow their own rules).
/// Arguments are validated when the method is called, not on first enumeration.
/// </remarks>
public static class EnumerablePolyfills
{
    /// <summary>Returns the element whose key is smallest; the first one wins ties.</summary>
    /// <typeparam name="TSource">Element type.</typeparam>
    /// <typeparam name="TKey">Key type.</typeparam>
    /// <param name="source">Sequence to search.</param>
    /// <param name="keySelector">Computes each element's key (called once per element).</param>
    /// <returns>
    /// The minimal element. For an empty sequence: <c>default</c> when <typeparamref name="TSource"/> can
    /// be null, otherwise <see cref="InvalidOperationException"/> (as in .NET 6).
    /// </returns>
    /// <remarks>Thread affinity: any thread.</remarks>
    public static TSource MinBy<TSource, TKey>(this IEnumerable<TSource> source, Func<TSource, TKey> keySelector)
    {
        return Extreme(source, keySelector, null, -1);
    }

    /// <summary>Returns the element whose key is smallest under <paramref name="comparer"/>; the first one wins ties.</summary>
    /// <typeparam name="TSource">Element type.</typeparam>
    /// <typeparam name="TKey">Key type.</typeparam>
    /// <param name="source">Sequence to search.</param>
    /// <param name="keySelector">Computes each element's key (called once per element).</param>
    /// <param name="comparer">Key comparer; null means <see cref="Comparer{T}.Default"/>.</param>
    /// <returns>See <see cref="MinBy{TSource, TKey}(IEnumerable{TSource}, Func{TSource, TKey})"/>.</returns>
    /// <remarks>Thread affinity: any thread.</remarks>
    public static TSource MinBy<TSource, TKey>(
        this IEnumerable<TSource> source, Func<TSource, TKey> keySelector, IComparer<TKey> comparer)
    {
        return Extreme(source, keySelector, comparer, -1);
    }

    /// <summary>Returns the element whose key is largest; the first one wins ties.</summary>
    /// <typeparam name="TSource">Element type.</typeparam>
    /// <typeparam name="TKey">Key type.</typeparam>
    /// <param name="source">Sequence to search.</param>
    /// <param name="keySelector">Computes each element's key (called once per element).</param>
    /// <returns>
    /// The maximal element. For an empty sequence: <c>default</c> when <typeparamref name="TSource"/> can
    /// be null, otherwise <see cref="InvalidOperationException"/> (as in .NET 6).
    /// </returns>
    /// <remarks>Thread affinity: any thread.</remarks>
    public static TSource MaxBy<TSource, TKey>(this IEnumerable<TSource> source, Func<TSource, TKey> keySelector)
    {
        return Extreme(source, keySelector, null, 1);
    }

    /// <summary>Returns the element whose key is largest under <paramref name="comparer"/>; the first one wins ties.</summary>
    /// <typeparam name="TSource">Element type.</typeparam>
    /// <typeparam name="TKey">Key type.</typeparam>
    /// <param name="source">Sequence to search.</param>
    /// <param name="keySelector">Computes each element's key (called once per element).</param>
    /// <param name="comparer">Key comparer; null means <see cref="Comparer{T}.Default"/>.</param>
    /// <returns>See <see cref="MaxBy{TSource, TKey}(IEnumerable{TSource}, Func{TSource, TKey})"/>.</returns>
    /// <remarks>Thread affinity: any thread.</remarks>
    public static TSource MaxBy<TSource, TKey>(
        this IEnumerable<TSource> source, Func<TSource, TKey> keySelector, IComparer<TKey> comparer)
    {
        return Extreme(source, keySelector, comparer, 1);
    }

    /// <summary>Returns the elements whose key has not been seen before, in source order (lazy).</summary>
    /// <typeparam name="TSource">Element type.</typeparam>
    /// <typeparam name="TKey">Key type.</typeparam>
    /// <param name="source">Sequence to filter.</param>
    /// <param name="keySelector">Computes each element's key.</param>
    /// <returns>The first element for each distinct key.</returns>
    /// <remarks>Thread affinity: any thread. Allocates a <see cref="HashSet{T}"/> per enumeration.</remarks>
    public static IEnumerable<TSource> DistinctBy<TSource, TKey>(
        this IEnumerable<TSource> source, Func<TSource, TKey> keySelector)
    {
        return DistinctBy(source, keySelector, null);
    }

    /// <summary>Returns the elements whose key has not been seen before under <paramref name="comparer"/>, in source order (lazy).</summary>
    /// <typeparam name="TSource">Element type.</typeparam>
    /// <typeparam name="TKey">Key type.</typeparam>
    /// <param name="source">Sequence to filter.</param>
    /// <param name="keySelector">Computes each element's key.</param>
    /// <param name="comparer">Key equality; null means the default comparer.</param>
    /// <returns>The first element for each distinct key.</returns>
    /// <remarks>Thread affinity: any thread. Allocates a <see cref="HashSet{T}"/> per enumeration.</remarks>
    public static IEnumerable<TSource> DistinctBy<TSource, TKey>(
        this IEnumerable<TSource> source, Func<TSource, TKey> keySelector, IEqualityComparer<TKey> comparer)
    {
        if (source == null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (keySelector == null)
        {
            throw new ArgumentNullException(nameof(keySelector));
        }

        return DistinctByIterator(source, keySelector, comparer);
    }

    /// <summary>Splits a sequence into arrays of at most <paramref name="size"/> elements (lazy).</summary>
    /// <typeparam name="TSource">Element type.</typeparam>
    /// <param name="source">Sequence to split.</param>
    /// <param name="size">Chunk length, at least 1.</param>
    /// <returns>
    /// Consecutive chunks in order. Every chunk but possibly the last has exactly
    /// <paramref name="size"/> elements, and each is a fresh array the caller may keep.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="size"/> is below 1.</exception>
    /// <remarks>Thread affinity: any thread. Allocates one array per chunk.</remarks>
    public static IEnumerable<TSource[]> Chunk<TSource>(this IEnumerable<TSource> source, int size)
    {
        if (source == null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (size < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Chunk size must be at least 1.");
        }

        return ChunkIterator(source, size);
    }

    private static TSource Extreme<TSource, TKey>(
        IEnumerable<TSource> source, Func<TSource, TKey> keySelector, IComparer<TKey> comparer, int sign)
    {
        if (source == null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (keySelector == null)
        {
            throw new ArgumentNullException(nameof(keySelector));
        }

        if (comparer == null)
        {
            comparer = Comparer<TKey>.Default;
        }

        bool keyCanBeNull = default(TKey) == null;

        using (IEnumerator<TSource> e = source.GetEnumerator())
        {
            if (!e.MoveNext())
            {
                // Same split as .NET 6: only types without a null value must throw on empty input.
                if (default(TSource) == null)
                {
                    return default(TSource);
                }

                throw new InvalidOperationException("Sequence contains no elements");
            }

            TSource best = e.Current;
            TKey bestKey = keySelector(best);

            // .NET 6 ignores null keys while a non-null one exists (and returns the first
            // element if every key is null); Comparer.Default alone would rank null lowest.
            if (keyCanBeNull)
            {
                TSource first = best;
                while (bestKey == null)
                {
                    if (!e.MoveNext())
                    {
                        return first;
                    }

                    best = e.Current;
                    bestKey = keySelector(best);
                }
            }

            while (e.MoveNext())
            {
                TSource item = e.Current;
                TKey key = keySelector(item);
                if (keyCanBeNull && key == null)
                {
                    continue;
                }

                if (comparer.Compare(key, bestKey) * sign > 0)
                {
                    best = item;
                    bestKey = key;
                }
            }

            return best;
        }
    }

    private static IEnumerable<TSource> DistinctByIterator<TSource, TKey>(
        IEnumerable<TSource> source, Func<TSource, TKey> keySelector, IEqualityComparer<TKey> comparer)
    {
        HashSet<TKey> seen = new HashSet<TKey>(comparer);
        foreach (TSource item in source)
        {
            if (seen.Add(keySelector(item)))
            {
                yield return item;
            }
        }
    }

    private static IEnumerable<TSource[]> ChunkIterator<TSource>(IEnumerable<TSource> source, int size)
    {
        TSource[] buffer = null;
        int count = 0;

        foreach (TSource item in source)
        {
            if (buffer == null)
            {
                buffer = new TSource[size];
            }

            buffer[count++] = item;
            if (count == size)
            {
                // Hand the full array over and start a new one: the caller may keep it.
                yield return buffer;
                buffer = null;
                count = 0;
            }
        }

        if (count > 0)
        {
            TSource[] tail = new TSource[count];
            Array.Copy(buffer, tail, count);
            yield return tail;
        }
    }
}
