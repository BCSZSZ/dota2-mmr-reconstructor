using System.Text.Json.Nodes;
using System.Xml.Linq;
using Dota2MmrCollector;

internal static class EndpointGapTests
{
    public static void Run(string root)
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "../../../../fixtures/synthetic-gc-collection.json");
        foreach (var (wins, losses, change, tail) in new[] { (4, 6, 915, false), (2, 0, -100, false), (0, 2, 100, true) })
        {
            var source = JsonNode.Parse(File.ReadAllText(fixture))!;
            var template = source["MatchHistory"]!["Matches"]![0]!.DeepClone();
            var matches = new JsonArray();
            for (var i = 0; i < wins + losses + (tail ? 1 : 2); i++)
            {
                var match = template.DeepClone();
                match["MatchId"]!["Value"] = 2000 + i;
                match["StartTime"]!["Value"] = 1704067200 + i * 86400;
                match["Winner"]!["Value"] = i == 0 || i <= wins;
                match["PreviousRank"]!["Value"] = i == 0 ? 2236 : 2256 + change;
                match["RankChange"]!["Value"] = i == 0 ? 20 : -40;
                if (i > 0 && i <= wins + losses)
                {
                    match["PreviousRank"]!["Present"] = false;
                    match["RankChange"]!["Present"] = false;
                }
                matches.Add(match);
            }
            source["MatchHistory"]!["Matches"] = matches;
            source["CurrentRank"]!["RankValue"]!["Value"] = 2256 + change - (tail ? 0 : 40);
            source["CapturedAtUtc"] = "2024-02-01T00:00:00Z";
            var folder = Path.Combine(root, $"gap-{wins}-{losses}");
            Directory.CreateDirectory(folder);
            var input = Path.Combine(folder, "gc-collection.json");
            File.WriteAllText(input, source.ToJsonString());
            var before = File.ReadAllBytes(input);
            var output = Path.Combine(folder, "mmr-reconstruction");
            var result = MmrReconstructor.Run(input, output, 12345);
            var rows = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "mmr-dataset.json")))!["rows"]!.AsArray();
            Require(rows.Count == matches.Count, "unknown matches retained");
            var unknown = rows.Where(r => r!["modeled_rank_change"] is null).ToArray();
            Require(unknown.Length == wins + losses && unknown.All(r => r!["curve_mmr_after"] is null
                && r["curve_mmr_before"] is null && r["double_down_probability"] is null), "unknown values remain null");
            Require(rows[0]!["actual_start_mmr"]!.GetValue<int>() == 2236
                && rows[0]!["actual_end_mmr"]!.GetValue<int>() == 2256, "left real anchor unchanged");
            Require(unknown[^1]!["segment_endpoint_mmr"]!.GetValue<int>() == 2256 + change, "right real anchor retained");
            if (!tail) Require(rows[^1]!["actual_start_mmr"]!.GetValue<int>() == 2256 + change
                && rows[^1]!["actual_rank_change"]!.GetValue<int>() == -40, "next actual row unchanged");
            var summary = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "model-summary.json")))!;
            Require(!summary["curve_reconstruction"]!["all_hidden_endpoints_exact"]!.GetValue<bool>()
                && summary["hidden_segments"]![0]!["endpoint_residual"] is null, "gap never reported as successful fit");
            var scope = TeammateReport.ReadScope(output, 12345);
            Require(scope.Count == (tail ? 1 : 2) && scope.Sum(m => m.MmrChange) == (tail ? 20 : -20), "no invented teammate contribution");
            if (!tail) Require(scope[^1].CurveBreakBefore, "teammate curve breaks at gap");
            var svg = XDocument.Load(Path.Combine(output, "complete-mmr-curve.svg"));
            XNamespace ns = "http://www.w3.org/2000/svg";
            Require(svg.Descendants(ns + "path").Single().Attribute("d")!.Value.Count(c => c == 'M') >= 2,
                "static SVG does not bridge unknown matches");
            Require(result.Notices.Any(n => n.Contains("端点约束"))
                && File.ReadAllText(Path.Combine(output, "hero-mmr-contribution.txt")).Contains("未计入"), "coverage disclosed");
            Require(before.SequenceEqual(File.ReadAllBytes(input)), "raw input unchanged");
        }
        Console.WriteLine("PASS: infeasible endpoints retain real anchors, null gaps, partial statistics and raw input");
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
    }
}
