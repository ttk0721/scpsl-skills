using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Examples.AddingPerPlayerTracking.Tests
{
    /// <summary>
    /// Tests for the journal format and for the batch bookkeeping that makes a retried or repeated
    /// write safe. Every test uses its own temp folder. Exit code 0 means every test passed.
    /// </summary>
    internal static class Program
    {
        private static int _failed;
        private static int _passed;

        private static int Main()
        {
            Run(nameof(Crc32MatchesTheStandardCheckValue), Crc32MatchesTheStandardCheckValue);
            Run(nameof(AppendedBatchesLoadBackAsSums), AppendedBatchesLoadBackAsSums);
            Run(nameof(NumberCutShortInTheLastLineIsDropped), NumberCutShortInTheLastLineIsDropped);
            Run(nameof(LineCutAnywhereIsNeverApplied), LineCutAnywhereIsNeverApplied);
            Run(nameof(CorruptedChecksumIsDropped), CorruptedChecksumIsDropped);
            Run(nameof(CorruptedFieldWithOldChecksumIsDropped), CorruptedFieldWithOldChecksumIsDropped);
            Run(nameof(LineWithoutTrailingNewlineIsDropped), LineWithoutTrailingNewlineIsDropped);
            Run(nameof(WrongFieldCountAndUnknownVersionAreDropped), WrongFieldCountAndUnknownVersionAreDropped);
            Run(nameof(ValuesNobodyCouldHaveWrittenAreDropped), ValuesNobodyCouldHaveWrittenAreDropped);
            Run(nameof(DuplicateBatchIsCountedOnce), DuplicateBatchIsCountedOnce);
            Run(nameof(SameBatchIdWithAnotherAccountIsNotADuplicate), SameBatchIdWithAnotherAccountIsNotADuplicate);
            Run(nameof(BlankLinesAreNotMalformed), BlankLinesAreNotMalformed);
            Run(nameof(PartialAppendThenRetryDoesNotDoubleCount), PartialAppendThenRetryDoesNotDoubleCount);
            Run(nameof(RetryAfterATornTailDoesNotGlueToTheFragment), RetryAfterATornTailDoesNotGlueToTheFragment);
            Run(nameof(BootCompactsAndLaterBootsReadTheSameTotals), BootCompactsAndLaterBootsReadTheSameTotals);
            Run(nameof(FailedBatchIsResentUnderTheSameIdAndContent), FailedBatchIsResentUnderTheSameIdAndContent);
            Run(nameof(FailedBatchIsNeverMergedIntoNewSeconds), FailedBatchIsNeverMergedIntoNewSeconds);
            Run(nameof(PartialWriteThenRetryThroughTheBookCountsExactlyOnce), PartialWriteThenRetryThroughTheBookCountsExactlyOnce);
            Run(nameof(ShutdownFlushIncludesBatchesStillInFlight), ShutdownFlushIncludesBatchesStillInFlight);
            Run(nameof(InFlightBatchWrittenByBothWorkerAndShutdownCountsOnce), InFlightBatchWrittenByBothWorkerAndShutdownCountsOnce);
            Run(nameof(SettledBatchIsNotWrittenAgainAtShutdown), SettledBatchIsNotWrittenAgainAtShutdown);
            Run(nameof(RetryListIsBoundedAndCountsWhatItDropped), RetryListIsBoundedAndCountsWhatItDropped);
            Run(nameof(BatchIdsDifferBetweenBoots), BatchIdsDifferBetweenBoots);
            Run(nameof(TryAppendWithAFreeLockWrites), TryAppendWithAFreeLockWrites);

            Console.WriteLine(_failed == 0 ? $"ALL {_passed} TESTS PASSED" : $"{_failed} FAILED, {_passed} passed");
            return _failed == 0 ? 0 : 1;
        }

        // ---- format and reader -------------------------------------------------------------

        private static void Crc32MatchesTheStandardCheckValue()
        {
            // The published check value of CRC-32 for the ASCII text "123456789".
            True(JournalStore.Crc32("123456789") == 0xCBF43926u, "crc32 of 123456789 is cbf43926");
        }

        private static void AppendedBatchesLoadBackAsSums()
        {
            string path = NewPath();
            var store = new JournalStore(path);
            store.Append(new[] { B("b1", D("a", 10, 1, 0), D("b", 5, 0, 2)) });
            store.Append(new[] { B("b2", D("a", 20.5, 0, 0)) });

            var totals = new Dictionary<string, Totals>();
            LoadReport r = new JournalStore(path).LoadInto(totals);

            Eq(3, r.Applied, "applied");
            Eq(0, r.Malformed, "malformed");
            Eq(0, r.Duplicates, "duplicates");
            Near(30.5, totals["a"].Active);
            Near(1, totals["a"].Afk);
            Near(5, totals["b"].Active);
            Near(2, totals["b"].Spectating);
        }

        private static void NumberCutShortInTheLastLineIsDropped()
        {
            // Case: a line cut mid-number ("...TAB 12" instead of "...TAB 123.0") must not parse as a valid, smaller value.
            string good = Line("b1", "a", 100, 0, 0);
            string full = Line("b2", "b", 123.0, 0, 0);
            string cut = full.Substring(0, full.IndexOf("123.0", StringComparison.Ordinal)) + "12"; // ends mid-number, no checksum, no newline
            string path = NewPath();
            File.WriteAllText(path, good + cut);

            var totals = new Dictionary<string, Totals>();
            LoadReport r = new JournalStore(path).LoadInto(totals);

            Eq(1, r.Applied, "only the complete line is applied");
            Eq(1, r.Malformed, "the cut line is counted");
            True(!totals.ContainsKey("b"), "the 12 s that were never written must not appear");
            Near(100, totals["a"].Active);
        }

        private static void LineCutAnywhereIsNeverApplied()
        {
            // Every possible cut point of one line, with and without the cut line being last: none may add seconds.
            string full = Line("b1", "76561198000000000@steam", 123.0, 45.0, 6.0);
            for (int len = 1; len < full.Length; len++) // len == full.Length is the intact line
            {
                string path = NewPath();
                File.WriteAllText(path, full.Substring(0, len));
                var totals = new Dictionary<string, Totals>();
                LoadReport r = new JournalStore(path).LoadInto(totals);
                True(r.Applied == 0 && totals.Count == 0, "a line cut after " + len + " chars was applied");
                Eq(1, r.Malformed, "cut after " + len);
            }
        }

        private static void CorruptedChecksumIsDropped()
        {
            string line = Line("b1", "a", 10, 0, 0);
            string bad = line.Substring(0, line.Length - 2) + (line[line.Length - 2] == '0' ? "1" : "0") + "\n"; // last checksum digit changed
            string path = NewPath();
            File.WriteAllText(path, bad + Line("b2", "a", 5, 0, 0));

            var totals = new Dictionary<string, Totals>();
            LoadReport r = new JournalStore(path).LoadInto(totals);

            Eq(1, r.Applied, "applied");
            Eq(1, r.Malformed, "malformed");
            Near(5, totals["a"].Active);
        }

        private static void CorruptedFieldWithOldChecksumIsDropped()
        {
            string line = Line("b1", "a", 10, 0, 0);
            string bad = line.Replace("\t10.0\t", "\t90.0\t"); // a flipped digit, checksum left as written
            True(bad != line, "test setup changed the line");
            string path = NewPath();
            File.WriteAllText(path, bad);

            var totals = new Dictionary<string, Totals>();
            LoadReport r = new JournalStore(path).LoadInto(totals);

            Eq(0, r.Applied, "applied");
            Eq(1, r.Malformed, "malformed");
            True(totals.Count == 0, "nothing applied");
        }

        private static void LineWithoutTrailingNewlineIsDropped()
        {
            // The cut can land exactly after the checksum: the line verifies, but it has no terminator,
            // which is the only sign the write stopped. It must not be applied.
            string intact = Line("b1", "a", 10, 0, 0);
            string path = NewPath();
            File.WriteAllText(path, Line("b0", "z", 1, 0, 0) + intact.TrimEnd('\n'));

            var totals = new Dictionary<string, Totals>();
            LoadReport r = new JournalStore(path).LoadInto(totals);

            Eq(1, r.Applied, "applied");
            Eq(1, r.Malformed, "malformed");
            True(!totals.ContainsKey("a"), "the unterminated line was not applied");
        }

        private static void WrongFieldCountAndUnknownVersionAreDropped()
        {
            string body5 = "J1\tb1\ta\t1.0\t2.0"; // missing a field
            string wrongVersion = Sealed("J2\tb1\ta\t1.0\t2.0\t3.0"); // valid checksum, version this code does not know
            string legacy = "a\t10.0\t0.0\t0.0\n";  // the old unchecked 4-field format
            string path = NewPath();
            File.WriteAllText(path, Sealed(body5) + wrongVersion + legacy);

            var totals = new Dictionary<string, Totals>();
            LoadReport r = new JournalStore(path).LoadInto(totals);

            Eq(0, r.Applied, "applied");
            Eq(3, r.Malformed, "malformed");
        }

        private static void ValuesNobodyCouldHaveWrittenAreDropped()
        {
            string path = NewPath();
            File.WriteAllText(path,
                Sealed("J1\tb1\ta\tNaN\t0.0\t0.0")
                + Sealed("J1\tb2\ta\t-5.0\t0.0\t0.0")
                + Sealed("J1\tb3\ta\tInfinity\t0.0\t0.0")
                + Sealed("J1\tb4\ta\t1.0\t0.0\t0.0"));

            var totals = new Dictionary<string, Totals>();
            LoadReport r = new JournalStore(path).LoadInto(totals);

            Eq(1, r.Applied, "applied");
            Eq(3, r.Malformed, "malformed");
            Near(1, totals["a"].Active);
        }

        private static void DuplicateBatchIsCountedOnce()
        {
            string path = NewPath();
            var store = new JournalStore(path);
            Batch b = B("b1", D("a", 10, 0, 0), D("b", 3, 0, 0));
            store.Append(new[] { b });
            store.Append(new[] { b }); // a retry that reached the file twice

            var totals = new Dictionary<string, Totals>();
            LoadReport r = new JournalStore(path).LoadInto(totals);

            Eq(2, r.Applied, "applied");
            Eq(2, r.Duplicates, "duplicates");
            Near(10, totals["a"].Active);
            Near(3, totals["b"].Active);
        }

        private static void SameBatchIdWithAnotherAccountIsNotADuplicate()
        {
            string path = NewPath();
            new JournalStore(path).Append(new[] { B("b1", D("a", 1, 0, 0), D("b", 2, 0, 0)) });

            var totals = new Dictionary<string, Totals>();
            LoadReport r = new JournalStore(path).LoadInto(totals);

            Eq(2, r.Applied, "both accounts of one batch count");
            Eq(0, r.Duplicates, "duplicates");
        }

        private static void BlankLinesAreNotMalformed()
        {
            string path = NewPath();
            File.WriteAllText(path, "\n" + Line("b1", "a", 4, 0, 0) + "\n\n");

            var totals = new Dictionary<string, Totals>();
            LoadReport r = new JournalStore(path).LoadInto(totals);

            Eq(1, r.Applied, "applied");
            Eq(0, r.Malformed, "malformed");
        }

        // ---- append after a failure --------------------------------------------------------

        private static void PartialAppendThenRetryDoesNotDoubleCount()
        {
            // Case: a write that stops halfway (disk full), then the whole batch is written again.
            Batch batch = B("b1", D("a", 10, 0, 0), D("b", 20, 0, 0), D("c", 30, 0, 0));
            string all = JournalStore.Format(new[] { batch });
            int secondLine = all.IndexOf('\n') + 1;
            string path = NewPath();
            File.WriteAllText(path, all.Substring(0, secondLine + 8)); // line one complete, line two cut inside

            new JournalStore(path).Append(new[] { batch }); // the retry, same id, same content

            var totals = new Dictionary<string, Totals>();
            LoadReport r = new JournalStore(path).LoadInto(totals);

            Near(10, totals["a"].Active); // written by both attempts, counted once
            Near(20, totals["b"].Active); // only the retry has it
            Near(30, totals["c"].Active);
            Eq(3, r.Applied, "applied");
            Eq(1, r.Duplicates, "the first line was written twice");
            Eq(1, r.Malformed, "the cut fragment");
        }

        private static void RetryAfterATornTailDoesNotGlueToTheFragment()
        {
            // Without the newline Append inserts, the first retried line would continue the fragment,
            // fail its checksum, and that account's seconds would be lost.
            string path = NewPath();
            File.WriteAllText(path, Line("b0", "z", 1, 0, 0) + "J1\tb1\ta\t10"); // fragment, no newline

            new JournalStore(path).Append(new[] { B("b1", D("a", 10, 0, 0)) });

            var totals = new Dictionary<string, Totals>();
            LoadReport r = new JournalStore(path).LoadInto(totals);

            Near(10, totals["a"].Active);
            Eq(2, r.Applied, "z and a");
            Eq(1, r.Malformed, "only the fragment");
        }

        private static void BootCompactsAndLaterBootsReadTheSameTotals()
        {
            string path = NewPath();
            var store = new JournalStore(path);
            Batch b = B("b1", D("a", 10, 1, 2));
            store.Append(new[] { b });
            store.Append(new[] { b });

            new JournalStore(path).LoadInto(new Dictionary<string, Totals>());
            string compacted = File.ReadAllText(path);
            Eq(1, compacted.Split('\n').Length - 1, "one line per account after compaction");

            var totals = new Dictionary<string, Totals>();
            LoadReport r = new JournalStore(path).LoadInto(totals);
            Near(10, totals["a"].Active);
            Near(1, totals["a"].Afk);
            Near(2, totals["a"].Spectating);
            Eq(0, r.Malformed + r.Duplicates, "the compacted file is clean");
        }

        // ---- the book ------------------------------------------------------------------------

        private static void FailedBatchIsResentUnderTheSameIdAndContent()
        {
            var totals = new Dictionary<string, Totals>();
            FlushBook book = NewBook(totals);
            Credit(book, totals, "a", 10);

            Batch[] first = book.CollectDue();
            Eq(1, first.Length, "one batch");
            book.Failed(first);

            Batch[] second = book.CollectDue();
            Eq(1, second.Length, "the failed batch only");
            True(ReferenceEquals(first[0], second[0]), "the very same batch object: same id, same deltas");
            Eq(1, book.InFlightCount, "back in flight");
        }

        private static void FailedBatchIsNeverMergedIntoNewSeconds()
        {
            var totals = new Dictionary<string, Totals>();
            FlushBook book = NewBook(totals);
            Credit(book, totals, "a", 10);
            Batch[] first = book.CollectDue();
            book.Failed(first);
            Credit(book, totals, "a", 7); // earned while the first batch was failing

            Batch[] due = book.CollectDue();
            Eq(2, due.Length, "the old batch and a new one");
            True(ReferenceEquals(first[0], due[0]), "old batch first, unchanged");
            True(due[0].Id != due[1].Id, "the new seconds got their own id");
            Near(10, due[0].Deltas[0].Active);
            Near(7, due[1].Deltas[0].Active);
        }

        private static void PartialWriteThenRetryThroughTheBookCountsExactlyOnce()
        {
            string path = NewPath();
            var store = new JournalStore(path);
            var live = new Dictionary<string, Totals>();
            FlushBook book = new FlushBook(live, store.BootId);
            Credit(book, live, "a", 10);
            Credit(book, live, "b", 20);

            Batch[] due = book.CollectDue();
            string text = JournalStore.Format(due);
            File.WriteAllText(path, text.Substring(0, text.IndexOf('\n') + 1 + 5)); // the write died inside line two
            book.Failed(due);                                                       // what the worker's error callback does

            Credit(book, live, "a", 4); // meanwhile
            Batch[] again = book.CollectDue();
            store.Append(again);
            book.Settled(again);

            var loaded = new Dictionary<string, Totals>();
            new JournalStore(path).LoadInto(loaded);
            Near(14, loaded["a"].Active); // 10 + 4, the 10 not twice
            Near(20, loaded["b"].Active);
            Eq(0, book.InFlightCount + book.RetryCount, "nothing left unconfirmed");
        }

        private static void ShutdownFlushIncludesBatchesStillInFlight()
        {
            var totals = new Dictionary<string, Totals>();
            FlushBook book = NewBook(totals);
            Credit(book, totals, "a", 10);
            Batch[] inFlight = book.CollectDue(); // handed to the worker; its confirmation never reaches the main thread
            Credit(book, totals, "a", 5);          // earned after that

            List<Batch> all = book.CollectForShutdown();
            Eq(2, all.Count, "the unconfirmed batch and the newest seconds");
            True(all.Contains(inFlight[0]), "the in-flight batch is written again by the main thread");
        }

        private static void InFlightBatchWrittenByBothWorkerAndShutdownCountsOnce()
        {
            string path = NewPath();
            var store = new JournalStore(path);
            var live = new Dictionary<string, Totals>();
            FlushBook book = new FlushBook(live, store.BootId);
            Credit(book, live, "a", 10);

            Batch[] due = book.CollectDue();
            store.Append(due);               // the worker finished the write ...
            Credit(book, live, "a", 5);      // ... but its confirmation had not been drained when Shutdown fired
            store.TryAppend(book.CollectForShutdown(), 1000);

            var loaded = new Dictionary<string, Totals>();
            LoadReport r = new JournalStore(path).LoadInto(loaded);
            Near(15, loaded["a"].Active);
            Eq(1, r.Duplicates, "the in-flight batch was written twice");
        }

        private static void SettledBatchIsNotWrittenAgainAtShutdown()
        {
            var totals = new Dictionary<string, Totals>();
            FlushBook book = NewBook(totals);
            Credit(book, totals, "a", 10);
            book.Settled(book.CollectDue());

            Eq(0, book.CollectForShutdown().Count, "nothing unconfirmed, nothing new");
        }

        private static void RetryListIsBoundedAndCountsWhatItDropped()
        {
            var totals = new Dictionary<string, Totals>();
            FlushBook book = NewBook(totals);
            for (int i = 0; i < FlushBook.MaxRetained + 10; i++)
            {
                Credit(book, totals, "a", 1);
                book.Failed(book.CollectDue()); // the disk never comes back
            }

            Eq(FlushBook.MaxRetained, book.RetryCount, "retained");
            Eq(10, (int)book.DroppedBatches, "dropped, and visible");
        }

        private static void BatchIdsDifferBetweenBoots()
        {
            var t1 = new Dictionary<string, Totals>();
            var t2 = new Dictionary<string, Totals>();
            FlushBook b1 = new FlushBook(t1, new JournalStore(NewPath()).BootId);
            FlushBook b2 = new FlushBook(t2, new JournalStore(NewPath()).BootId);
            Credit(b1, t1, "a", 1);
            Credit(b2, t2, "a", 1);

            True(b1.CollectDue()[0].Id != b2.CollectDue()[0].Id, "first batch of two boots must not share an id");
        }

        private static void TryAppendWithAFreeLockWrites()
        {
            string path = NewPath();
            True(new JournalStore(path).TryAppend(new[] { B("b1", D("a", 3, 0, 0)) }, 100), "a free lock is acquired");
            var totals = new Dictionary<string, Totals>();
            new JournalStore(path).LoadInto(totals);
            Near(3, totals["a"].Active);
        }

        // ---- helpers -------------------------------------------------------------------------

        private static FlushBook NewBook(Dictionary<string, Totals> totals) => new FlushBook(totals, "t");

        // Credits like ActivityTracker.Credit does: into the totals, the pending fields, and the dirty set.
        private static void Credit(FlushBook book, Dictionary<string, Totals> totals, string id, double active)
        {
            if (!totals.TryGetValue(id, out Totals t))
                totals[id] = t = new Totals();
            t.Active += active;
            t.PendingActive += active;
            book.MarkDirty(id);
        }

        private static Delta D(string id, double a, double b, double s) => new Delta(id, a, b, s);

        private static Batch B(string id, params Delta[] deltas) => new Batch(id, deltas);

        // One well-formed journal line, produced by the code under test.
        private static string Line(string batchId, string user, double a, double b, double s) =>
            JournalStore.Format(new[] { B(batchId, D(user, a, b, s)) });

        // A body with a correct checksum and newline, for lines the writer would never produce.
        private static string Sealed(string body) =>
            body + "\t" + JournalStore.Crc32(body).ToString("x8", CultureInfo.InvariantCulture) + "\n";

        private static string NewPath()
        {
            string dir = Path.Combine(Path.GetTempPath(), "perplayer-journal-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "journal.tsv");
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                _passed++;
                Console.WriteLine("PASS " + name);
            }
            catch (Exception e)
            {
                _failed++;
                Console.WriteLine("FAIL " + name + ": " + e.Message);
            }
        }

        private static void Eq(int expected, int actual, string what)
        {
            if (expected != actual)
                throw new Exception(what + ": expected " + expected + ", got " + actual);
        }

        private static void Near(double expected, double actual)
        {
            if (Math.Abs(expected - actual) > 1e-9)
                throw new Exception($"expected {expected}, got {actual}");
        }

        private static void True(bool condition, string what)
        {
            if (!condition)
                throw new Exception("expected: " + what);
        }
    }
}
