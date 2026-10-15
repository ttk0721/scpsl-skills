// Lexer stress test: every dangerous token below is inside a comment or literal, except where tagged.
using System;
using LabApi.Events.Handlers;
using LabApi.Features.Wrappers;

namespace Fixture
{
    public class Tricky
    {
        public void Lex()
        {
            // GC.Collect(); Timing.KillCoroutines(); Server.Tps
            /* GC.Collect();
               Timing.KillCoroutines();
               PlayerEvents.ValidatedVisibility += X; */
            string a = "GC.Collect(); // not a comment, and \" still inside";
            string b = @"verbatim ""GC.Collect()"" stays a string
            even across lines: Timing.KillCoroutines()";
            string c = $"interp {a} and {{literal braces}} and GC.Collect()";
            string d = $@"verbatim interp {b} Timing.KillCoroutines()";
            string e = """
                raw string with "quotes" and GC.Collect() inside
                """;
            char q = '"';
            char slash = '/';
            string f = "// not a comment" + "/* neither */";
            string g = $"nested {(a.Length > 0 ? "Server.Tps" : "none")} text";
            double h = Server.Tps; // expect: SL004
            string i = $"live {Server.Tps}"; // expect: SL004
            GC.Collect(); // expect: SL010
        }

#region don't lex this apostrophe
        public void AfterRegion()
        {
            Timing.KillCoroutines(); // expect: SL006
        }
#endregion
    }
}
