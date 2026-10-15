using LabApi.Features.Wrappers;

namespace Fixture
{
    public class TpsReaders
    {
        public void Poll()
        {
            double tps = Server.Tps; // expect: SL004
            if (LabApi.Features.Wrappers.Server.Tps < 30) { } // expect: SL004
            string line = $"TPS is {Server.Tps}"; // expect: SL004
            Server.RunCommand("tps"); // expect: SL004
        }

        public void NotFindings()
        {
            // Server.Tps in a comment
            string s = "Server.Tps";
            string v = @"Server.Tps";
            var cap = Server.MaxTps;          // different property
            var cfg = config.Server.Tps;      // a field named Server on some config object
            Server.RunCommand("list");
        }
    }
}
