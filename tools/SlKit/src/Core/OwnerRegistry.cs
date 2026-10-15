using System;
using System.Collections.Generic;

namespace SlKit.Core;

/// <summary>
/// Values registered under a name AND an owner identity, remembering which owner used a name first.
/// </summary>
/// <remarks>
/// Behind <c>Background.ForOwner</c>. Keyed by the name alone, two plugins that pick the same
/// name ("Stats", "Webhook") silently share one worker thread and one queue: exactly the coupling
/// a per-owner worker exists to remove. Keying by (name, owner) gives each owner its own value,
/// and <see cref="FirstOwnerOf"/> lets the caller notice and report the name collision.
/// Owners are compared by reference (an <c>Assembly</c> object in SlKit), names ordinally.
/// Thread affinity: NOT thread-safe; the caller holds its own lock around every call.
/// </remarks>
/// <typeparam name="T">The registered value (a worker).</typeparam>
public sealed class OwnerRegistry<T> where T : class
{
    private readonly Dictionary<string, List<Entry>> _byName = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
    private readonly List<T> _inOrder = new List<T>();

    /// <summary>Number of registered values.</summary>
    public int Count => _inOrder.Count;

    /// <summary>Looks up the value of <paramref name="owner"/> under <paramref name="name"/>.</summary>
    /// <param name="name">The name; compared ordinally.</param>
    /// <param name="owner">The owner identity; compared by reference.</param>
    /// <param name="value">The value, or null when this owner has none under this name.</param>
    /// <returns>True when found.</returns>
    public bool TryGet(string name, object owner, out T value)
    {
        if (name == null)
        {
            throw new ArgumentNullException(nameof(name));
        }

        if (_byName.TryGetValue(name, out List<Entry> entries))
        {
            for (int i = 0; i < entries.Count; i++)
            {
                if (ReferenceEquals(entries[i].Owner, owner))
                {
                    value = entries[i].Value;
                    return true;
                }
            }
        }

        value = null;
        return false;
    }

    /// <summary>The owner that registered <paramref name="name"/> first, or null when the name is unused.</summary>
    /// <param name="name">The name; compared ordinally.</param>
    /// <returns>The first owner, or null.</returns>
    public object FirstOwnerOf(string name)
    {
        if (name == null)
        {
            throw new ArgumentNullException(nameof(name));
        }

        return _byName.TryGetValue(name, out List<Entry> entries) ? entries[0].Owner : null;
    }

    /// <summary>Registers <paramref name="value"/> for (<paramref name="name"/>, <paramref name="owner"/>).</summary>
    /// <param name="name">The name.</param>
    /// <param name="owner">The owner identity (may be null, which is then one owner of its own).</param>
    /// <param name="value">The value; not null.</param>
    /// <exception cref="InvalidOperationException">This owner already has a value under this name.</exception>
    public void Add(string name, object owner, T value)
    {
        if (value == null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        if (TryGet(name, owner, out _))
        {
            throw new InvalidOperationException("'" + name + "' is already registered for this owner.");
        }

        if (!_byName.TryGetValue(name, out List<Entry> entries))
        {
            entries = new List<Entry>(1);
            _byName.Add(name, entries);
        }

        entries.Add(new Entry(owner, value));
        _inOrder.Add(value);
    }

    /// <summary>Copies every value, in registration order, into a new array.</summary>
    /// <returns>The values.</returns>
    public T[] ToArray() => _inOrder.ToArray();

    private readonly struct Entry
    {
        public Entry(object owner, T value)
        {
            Owner = owner;
            Value = value;
        }

        public object Owner { get; }

        public T Value { get; }
    }
}
