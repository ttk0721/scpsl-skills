using SlKit.Core;
using static SlKit.TestSupport.TestKit;

namespace SlKit.Tests;

/// <summary>Tests of CopyReport: the text about other SlKit copies in the process.</summary>
internal static class CopyReportTests
{
    internal static void Register()
    {
        // Report of other SlKit copies
        Run(nameof(CopyReport_NothingToReportForASingleCopy), CopyReport_NothingToReportForASingleCopy);
        Run(nameof(CopyReport_NamesVersionsAndLocationsOfEveryCopy), CopyReport_NamesVersionsAndLocationsOfEveryCopy);
        Run(nameof(CopyReport_DistinguishesMismatchDuplicateAndSourceInclude), CopyReport_DistinguishesMismatchDuplicateAndSourceInclude);
    }

    // ---------------------------------------------------------------------------------------
    // Copy report. Regression for "two SlKit copies run two clocks silently".
    // ---------------------------------------------------------------------------------------

    private static void CopyReport_NothingToReportForASingleCopy()
    {
        SlKitCopy self = new SlKitCopy("SlKit", "0.2.0.0", null, true);
        True(CopyReport.Describe(self, new SlKitCopy[0]) == null, "no others, no warning");
        True(CopyReport.Describe(self, null) == null, "null list, no warning");
    }

    private static void CopyReport_NamesVersionsAndLocationsOfEveryCopy()
    {
        SlKitCopy self = new SlKitCopy("SlKit", "0.2.0.0", "", true);
        string text = CopyReport.Describe(self, new[]
        {
            new SlKitCopy("SlKit", "0.1.0.0", "/srv/LabAPI/dependencies/7777/old/SlKit.dll", true),
            new SlKitCopy("MyPlugin", "1.4.0.0", "", false),
        });

        True(text != null, "a warning is produced");
        True(text.StartsWith("3 copies of SlKit"), "counts every copy: " + text);
        True(text.Contains("SlKit 0.2.0.0 (SlKit.dll), loaded from bytes"), "this copy with version and location");
        True(text.Contains("SlKit 0.1.0.0 (SlKit.dll), /srv/LabAPI/dependencies/7777/old/SlKit.dll"), "other shared copy with its path");
        True(text.Contains("MyPlugin 1.4.0.0 (SlKit compiled into this assembly)"), "source-included copy named");
    }

    private static void CopyReport_DistinguishesMismatchDuplicateAndSourceInclude()
    {
        SlKitCopy shared020 = new SlKitCopy("SlKit", "0.2.0.0", null, true);

        string mismatch = CopyReport.Describe(shared020, new[] { new SlKitCopy("SlKit", "0.1.0.0", null, true) });
        True(mismatch.Contains("Different versions of SlKit.dll"), "version mismatch called out");

        string duplicate = CopyReport.Describe(shared020, new[] { new SlKitCopy("SlKit", "0.2.0.0", null, true) });
        False(duplicate.Contains("Different versions"), "same version is not a mismatch");
        True(duplicate.Contains("present more than once"), "duplicate file called out");

        string embedded = CopyReport.Describe(shared020, new[] { new SlKitCopy("MyPlugin", "1.0.0.0", null, false) });
        False(embedded.Contains("Different versions") || embedded.Contains("more than once"), "a source include is not a duplicate DLL");
        True(embedded.Contains("its own namespace"), "source include advice");

        string fromEmbedded = CopyReport.Describe(new SlKitCopy("MyPlugin", "1.0.0.0", null, false), new[] { shared020 });
        True(fromEmbedded.Contains("its own namespace"), "also when the reporting copy is the embedded one");
        False(fromEmbedded.Contains("Different versions"), "one shared copy cannot mismatch");
    }
}
