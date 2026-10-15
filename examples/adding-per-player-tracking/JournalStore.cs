using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Examples.AddingPerPlayerTracking
{
    /// <summary>Everything ever recorded for one account. Outlives hubs, rounds and (through the journal) the process.</summary>
    internal sealed class Totals
    {
        public double Active, Afk, Spectating;
        public double PendingActive, PendingAfk, PendingSpectating; // part of the sums above, not yet taken into a batch
    }

    /// <summary>Seconds earned by one account since the last batch. Plain data, safe to hand to a worker thread.</summary>
    internal readonly struct Delta
    {
        public readonly string UserId;
        public readonly double Active;
        public readonly double Afk;
        public readonly double Spectating;

        public Delta(string userId, double active, double afk, double spectating)
        {
            UserId = userId;
            Active = active;
            Afk = afk;
            Spectating = spectating;
        }
    }

    /// <summary>
    /// A set of deltas with a name. The name is what makes a write repeatable: a retry, or the
    /// shutdown flush of a batch the worker may have finished already, re-sends the batch under the
    /// SAME id, and the reader counts each (batch id, account) line once. Immutable after creation.
    /// </summary>
    internal sealed class Batch
    {
        public readonly string Id;
        public readonly Delta[] Deltas;

        public Batch(string id, Delta[] deltas)
        {
            Id = id;
            Deltas = deltas;
        }
    }

    /// <summary>What boot found in the journal. Anything but <see cref="Applied"/> is a line that did not add seconds.</summary>
    internal sealed class LoadReport
    {
        public int Applied;     // lines whose seconds were added
        public int Duplicates;  // valid lines repeating an earlier (batch id, account): a retried write, ignored
        public int Malformed;   // wrong shape, bad checksum, or no trailing newline: a cut or damaged write, dropped
    }

    /// <summary>
    /// Append-only journal of batches, one line per account per batch. Appending costs O(dirty
    /// accounts), where rewriting a totals file would cost O(all accounts ever seen), and a crash
    /// loses at most one flush interval. Boot sums the lines and compacts the file.
    /// </summary>
    /// <remarks>
    /// Line format (tab separated, '\n' terminated, UTF-8 without BOM):
    ///   J1 TAB batchId TAB userId TAB active TAB afk TAB spectating TAB crc32hex NEWLINE
    /// Why each part exists. The checksum covers everything before it, so a line cut in the middle
    /// of a number ("...TAB 12" instead of "...TAB 123.0") fails instead of parsing as a smaller
    /// value. The newline is required because a cut can land exactly after the checksum: that line
    /// would verify, but "no terminator" is the only sign a write stopped early, so it is dropped.
    /// The batch id makes a repeated write harmless (see <see cref="Batch"/>). The leading version
    /// lets a later format be told apart instead of being misread.
    /// A failed append can leave a half line at the end of the file. The next append starts with a
    /// newline when the file does not end with one, so the retry never glues itself to that fragment.
    /// </remarks>
    internal sealed class JournalStore
    {
        private const string Version = "J1";
        private const int FieldCount = 7;

        // No BOM: it would sit in front of the first line and break that line's checksum.
        private static readonly Encoding Utf8 = new UTF8Encoding(false);
        private static readonly uint[] CrcTable = BuildCrcTable();

        // Shutdown flushes on the main thread while a Background worker may still be mid-append.
        private static readonly object IoLock = new object();
        private readonly string _path;

        public JournalStore(string path)
        {
            _path = path;
            BootId = Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        /// <summary>
        /// Unique per process start. Batch ids are built from it, so ids of different runs cannot
        /// collide while the journal still holds lines of both (it is compacted at every boot).
        /// </summary>
        public string BootId { get; }

        /// <summary>Boot only: blocks for a few ms, which is fine before the first player exists.</summary>
        public LoadReport LoadInto(Dictionary<string, Totals> totals)
        {
            var report = new LoadReport();
            if (!File.Exists(_path))
                return report;

            string text = Utf8.GetString(File.ReadAllBytes(_path)); // not ReadAllLines: it hides whether the last line had its newline
            if (text.Length > 0 && text[0] == '﻿')
                text = text.Substring(1);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            int start = 0;
            while (start < text.Length)
            {
                int end = text.IndexOf('\n', start);
                if (end < 0)
                {
                    report.Malformed++; // text after the last newline: that write was cut short, whatever it looks like
                    break;
                }

                if (end > start) // an empty line is just the separator an append adds after a torn tail
                    ApplyLine(text.Substring(start, end - start), totals, seen, report);
                start = end + 1;
            }

            Compact(totals);
            return report;
        }

        /// <summary>Any thread. Takes plain data only. Waits for the file lock as long as it takes.</summary>
        public void Append(IReadOnlyList<Batch> batches) => TryAppend(batches, Timeout.Infinite);

        /// <summary>
        /// Any thread. False when the file lock was not free within <paramref name="lockWaitMs"/>:
        /// the shutdown flush uses this so a stuck worker cannot hold up the exit forever.
        /// Throws when the write itself fails; part of the text may then be in the file.
        /// </summary>
        public bool TryAppend(IReadOnlyList<Batch> batches, int lockWaitMs)
        {
            string text = Format(batches);
            if (text.Length == 0)
                return true;

            if (!Monitor.TryEnter(IoLock, lockWaitMs))
                return false;
            try
            {
                string prefix = EndsWithNewline() ? string.Empty : "\n";
                File.AppendAllText(_path, prefix + text, Utf8);
                return true;
            }
            finally
            {
                Monitor.Exit(IoLock);
            }
        }

        /// <summary>The text of a batch as it goes into the file. Internal for the tests.</summary>
        internal static string Format(IReadOnlyList<Batch> batches)
        {
            var sb = new StringBuilder(batches.Count * 64);
            foreach (Batch b in batches)
            {
                foreach (Delta d in b.Deltas)
                    AppendLine(sb, b.Id, d.UserId, d.Active, d.Afk, d.Spectating);
            }

            return sb.ToString();
        }

        /// <summary>Called inside the lock. A missing or empty file counts as clean.</summary>
        private bool EndsWithNewline()
        {
            if (!File.Exists(_path))
                return true;

            using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (fs.Length == 0)
                    return true;
                fs.Seek(-1, SeekOrigin.End);
                return fs.ReadByte() == '\n';
            }
        }

        private static void ApplyLine(string line, Dictionary<string, Totals> totals, HashSet<string> seen, LoadReport report)
        {
            if (!TryParse(line, out string batchId, out string userId, out double a, out double b, out double s))
            {
                report.Malformed++;
                return;
            }

            if (!seen.Add(batchId + "\t" + userId))
            {
                report.Duplicates++;
                return;
            }

            if (!totals.TryGetValue(userId, out Totals t))
                totals[userId] = t = new Totals();
            t.Active += a;
            t.Afk += b;
            t.Spectating += s;
            report.Applied++;
        }

        private static bool TryParse(string line, out string batchId, out string userId, out double a, out double b, out double s)
        {
            batchId = userId = null;
            a = b = s = 0;

            string[] f = line.Split('\t');
            if (f.Length != FieldCount || f[0] != Version || f[1].Length == 0 || f[2].Length == 0)
                return false;

            int lastTab = line.LastIndexOf('\t');
            string expected = Crc32(line.Substring(0, lastTab)).ToString("x8", CultureInfo.InvariantCulture);
            if (!string.Equals(f[6], expected, StringComparison.Ordinal))
                return false;

            if (!TrySeconds(f[3], out a) || !TrySeconds(f[4], out b) || !TrySeconds(f[5], out s))
                return false;

            batchId = f[1];
            userId = f[2];
            return true;
        }

        // The checksum already rules out damage; this rules out values nobody could have written.
        private static bool TrySeconds(string text, out double value) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0;

        private void Compact(Dictionary<string, Totals> totals)
        {
            var sb = new StringBuilder(totals.Count * 64);
            string id = BootId + "c"; // one id for the whole compacted file; no batch id of this run can equal it
            foreach (KeyValuePair<string, Totals> kv in totals)
                AppendLine(sb, id, kv.Key, kv.Value.Active, kv.Value.Afk, kv.Value.Spectating);

            // Write beside the journal, then swap: a crash mid-write must not eat the old file.
            string tmp = _path + ".tmp";
            lock (IoLock)
            {
                File.WriteAllText(tmp, sb.ToString(), Utf8);
                if (File.Exists(_path))
                    File.Replace(tmp, _path, null);
                else
                    File.Move(tmp, _path);
            }
        }

        // Invariant culture: a comma decimal separator on a German host would corrupt the file.
        private static void AppendLine(StringBuilder sb, string batchId, string userId, double a, double b, double s)
        {
            string body = Version + "\t" + batchId + "\t" + userId + "\t"
                + a.ToString("F1", CultureInfo.InvariantCulture) + "\t"
                + b.ToString("F1", CultureInfo.InvariantCulture) + "\t"
                + s.ToString("F1", CultureInfo.InvariantCulture);
            sb.Append(body).Append('\t').Append(Crc32(body).ToString("x8", CultureInfo.InvariantCulture)).Append('\n');
        }

        // CRC-32 (the zip/PNG polynomial). A checksum, not a security measure: it only has to notice
        // a line that is cut or damaged. Internal for the tests (the check value of "123456789" is cbf43926).
        internal static uint Crc32(string text)
        {
            uint crc = 0xFFFFFFFFu;
            foreach (byte x in Utf8.GetBytes(text))
                crc = CrcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
            return ~crc;
        }

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }

            return table;
        }
    }
}
