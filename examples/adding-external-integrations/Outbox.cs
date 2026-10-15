using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Examples.AddingExternalIntegrations
{
    /// <summary>One queued report. Immutable plain data, so it may cross threads.</summary>
    internal sealed class OutboxEntry
    {
        public OutboxEntry(string key, long unixSeconds, string text)
        {
            Key = key;
            UnixSeconds = unixSeconds;
            Text = text;
        }

        /// <summary>Stable id, <c>{run}-{seq}</c>. Survives restarts, so a receiver can deduplicate on it.</summary>
        public string Key { get; }

        /// <summary>When it happened (UTC unix seconds), not when it was sent.</summary>
        public long UnixSeconds { get; }

        /// <summary>One sanitised line.</summary>
        public string Text { get; }
    }

    /// <summary>
    /// The pending reports. Owned by the main thread: only the main thread calls members of this class.
    /// Other threads only ever receive the arrays returned by <see cref="Snapshot"/> and <see cref="Peek"/>.
    /// Entries leave only after the receiver confirmed them (or were counted as dropped), never on "sent".
    /// </summary>
    internal sealed class Outbox
    {
        private readonly List<OutboxEntry> _items = new List<OutboxEntry>(64);
        private readonly string _run = Guid.NewGuid().ToString("N").Substring(0, 8);
        private int _seq;
        private long _generation;

        /// <summary>True when memory differs from the file on disk.</summary>
        public bool Dirty { get; set; }

        /// <summary>Entries refused because the queue was full.</summary>
        public long Overflowed { get; private set; }

        /// <summary>Entries removed because they were older than the age limit.</summary>
        public long Expired { get; private set; }

        public int Count => _items.Count;

        /// <summary>
        /// Appends an entry. When the queue is full the NEW entry is refused and counted: the head may be
        /// pinned as the batch that is in flight, and removing it would make the post-success removal
        /// delete the wrong entries.
        /// </summary>
        /// <param name="unixSeconds">Event time.</param>
        /// <param name="text">Sanitised line.</param>
        /// <param name="maxQueued">Capacity.</param>
        /// <returns>False when the entry was refused.</returns>
        public bool TryAdd(long unixSeconds, string text, int maxQueued)
        {
            if (_items.Count >= maxQueued)
            {
                Overflowed++;
                return false;
            }

            _seq++;
            _items.Add(new OutboxEntry(_run + "-" + _seq, unixSeconds, text));
            Dirty = true;
            return true;
        }

        /// <summary>Copy of up to <paramref name="max"/> oldest entries.</summary>
        /// <param name="max">Upper bound.</param>
        /// <returns>A new array.</returns>
        public OutboxEntry[] Peek(int max)
        {
            int n = Math.Min(max, _items.Count);
            OutboxEntry[] copy = new OutboxEntry[n];
            _items.CopyTo(0, copy, 0, n);
            return copy;
        }

        /// <summary>Copy of everything, for the writer.</summary>
        /// <returns>A new array.</returns>
        public OutboxEntry[] Snapshot()
        {
            return _items.ToArray();
        }

        /// <summary>Removes the oldest <paramref name="count"/> entries (after delivery or a drop).</summary>
        /// <param name="count">How many.</param>
        public void RemoveFirst(int count)
        {
            if (count <= 0)
                return;

            _items.RemoveRange(0, Math.Min(count, _items.Count));
            Dirty = true;
        }

        /// <summary>Removes entries older than the limit. Only call while no batch is pinned.</summary>
        /// <param name="minUnixSeconds">Oldest event time that is still worth reporting.</param>
        /// <returns>How many were removed.</returns>
        public int PruneOlderThan(long minUnixSeconds)
        {
            int removed = _items.RemoveAll(e => e.UnixSeconds < minUnixSeconds);
            if (removed > 0)
            {
                Expired += removed;
                Dirty = true;
            }

            return removed;
        }

        /// <summary>Adds entries loaded from disk at startup, keeping their original keys.</summary>
        /// <param name="loaded">Entries from <see cref="OutboxStore.Read"/>.</param>
        /// <param name="maxQueued">Capacity.</param>
        public void Restore(IEnumerable<OutboxEntry> loaded, int maxQueued)
        {
            foreach (OutboxEntry entry in loaded)
            {
                if (_items.Count >= maxQueued)
                {
                    Overflowed++;
                    continue;
                }

                _items.Add(entry);
            }
        }

        /// <summary>Number for the next write. Later writes win over earlier ones, see <see cref="OutboxStore.Write"/>.</summary>
        /// <returns>A strictly increasing number.</returns>
        public long NextGeneration()
        {
            return ++_generation;
        }
    }

    /// <summary>
    /// The outbox file: one line per entry, <c>key TAB unix TAB text</c>, with backslash escapes. Plain text
    /// so an operator can read and repair it. Thread-safe: it is written from the background worker and,
    /// at shutdown, from the main thread.
    /// </summary>
    internal sealed class OutboxStore
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        private readonly object _lock = new object();
        private readonly string _path;
        private long _lastGeneration;

        public OutboxStore(string path)
        {
            _path = path;
        }

        /// <summary>Turns a snapshot into the file text. Pure, safe on any thread.</summary>
        /// <param name="entries">The snapshot.</param>
        /// <returns>File contents.</returns>
        public static string Serialize(OutboxEntry[] entries)
        {
            StringBuilder sb = new StringBuilder(entries.Length * 96);
            foreach (OutboxEntry e in entries)
            {
                sb.Append(e.Key).Append('\t').Append(e.UnixSeconds).Append('\t');
                Escape(sb, e.Text);
                sb.Append('\n');
            }

            return sb.ToString();
        }

        /// <summary>Parses file text. Lines that do not parse are skipped, not fatal.</summary>
        /// <param name="content">File contents.</param>
        /// <returns>The entries in file order.</returns>
        public static List<OutboxEntry> Parse(string content)
        {
            List<OutboxEntry> result = new List<OutboxEntry>();
            if (string.IsNullOrEmpty(content))
                return result;

            foreach (string line in content.Split('\n'))
            {
                string[] parts = line.Split(new[] { '\t' }, 3);
                long unix;
                if (parts.Length != 3 || parts[0].Length == 0 || parts[0].Length > 40 || !long.TryParse(parts[1], out unix))
                    continue;

                result.Add(new OutboxEntry(parts[0], unix, Unescape(parts[2])));
            }

            return result;
        }

        /// <summary>Reads the file. A missing or unreadable file is an empty outbox, reported through <paramref name="error"/>.</summary>
        /// <param name="error">The failure, or null.</param>
        /// <returns>The entries.</returns>
        public List<OutboxEntry> Read(out Exception error)
        {
            error = null;
            try
            {
                return File.Exists(_path) ? Parse(File.ReadAllText(_path, Utf8)) : new List<OutboxEntry>();
            }
            catch (Exception e)
            {
                error = e;
                return new List<OutboxEntry>();
            }
        }

        /// <summary>
        /// Writes the file atomically (temp file, then replace), unless a newer generation was already
        /// written. The generation check matters at shutdown: the main thread writes the final state
        /// synchronously, and an older snapshot still waiting in the worker queue must not overwrite it.
        /// </summary>
        /// <param name="generation">From <see cref="Outbox.NextGeneration"/> when the snapshot was taken.</param>
        /// <param name="content">From <see cref="Serialize"/>.</param>
        public void Write(long generation, string content)
        {
            lock (_lock)
            {
                if (generation <= _lastGeneration)
                    return;

                string temp = _path + ".tmp";
                File.WriteAllText(temp, content, Utf8);
                if (File.Exists(_path))
                    File.Replace(temp, _path, null);
                else
                    File.Move(temp, _path);

                _lastGeneration = generation;
            }
        }

        private static void Escape(StringBuilder sb, string text)
        {
            foreach (char c in text ?? string.Empty)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
        }

        private static string Unescape(string text)
        {
            if (text.IndexOf('\\') < 0)
                return text;

            StringBuilder sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c != '\\' || i + 1 >= text.Length)
                {
                    sb.Append(c);
                    continue;
                }

                i++;
                switch (text[i])
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    default: sb.Append(text[i]); break;
                }
            }

            return sb.ToString();
        }
    }
}
