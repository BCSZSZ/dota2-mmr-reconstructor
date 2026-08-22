using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dota2MmrCollector;

internal static class MmrReconstructor
{
    public const string ModelVersion = "endpoint-constrained-glicko-dd-v2-csharp";

    private static readonly DateTimeOffset SingleRankStart =
        new(2020, 3, 2, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset GlickoStart =
        new(2023, 4, 20, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
    private static readonly string[] CurveFields =
    [
        "date_utc", "unix_time", "match_id", "result", "hero_id", "average_rank",
        "party_size", "mmr_fields_visible", "confidence_regime", "uncertainty_proxy",
        "confidence_proxy", "confidence_used", "segment", "actual_start_mmr",
        "actual_rank_change", "actual_end_mmr", "likely_double_down",
        "double_down_probability", "expected_double_down_multiplier", "double_down_status",
        "normal_rank_change_prior", "unconstrained_rank_change", "endpoint_correction",
        "modeled_rank_change", "curve_mmr_before", "curve_mmr_after",
        "segment_endpoint_mmr", "segment_endpoint_source", "anchor_jump_before", "curve_source",
    ];

    public static ReconstructionOutput Run(
        string rawCollectionPath,
        string outputDirectory,
        uint? expectedAccountId = null)
    {
        var source = LoadSource(rawCollectionPath, expectedAccountId);
        var segments = FindHiddenSegments(source.Timeline);
        var confidence = FitConfidenceProxy(source.Timeline, source.Anchor.BaseUncertainty);
        var doubleDown = FitDoubleDownModel(source.Timeline, confidence);
        var rows = BuildCurve(
            source.Timeline,
            segments,
            confidence,
            source.Anchor.CurrentMmr,
            doubleDown);
        if (rows.Count == 0)
        {
            throw new InvalidDataException("没有带绝对 MMR 锚点的天梯记录可供绘图。");
        }

        Directory.CreateDirectory(outputDirectory);
        var summaryPath = Path.Combine(outputDirectory, "model-summary.json");
        var estimatesPath = Path.Combine(outputDirectory, "match-estimates.csv");
        var curvePath = Path.Combine(outputDirectory, "complete-mmr-curve.csv");
        var svgPath = Path.Combine(outputDirectory, "complete-mmr-curve.svg");
        var pngPath = Path.Combine(outputDirectory, "complete-mmr-curve.png");
        var heroReportPath = Path.Combine(outputDirectory, "hero-mmr-contribution.txt");
        var heroMarkdownPath = Path.Combine(outputDirectory, "hero-mmr-contribution.md");
        var heroWorkbookPath = Path.Combine(outputDirectory, "hero-mmr-contribution.xlsx");
        var segmentsPath = Path.Combine(outputDirectory, "hidden-segments.csv");
        var datasetPath = Path.Combine(outputDirectory, "mmr-dataset.json");
        var htmlPath = Path.Combine(outputDirectory, "mmr-history.html");

        var hiddenRows = rows.Where(row => GetBoolean(row, "mmr_fields_visible") is false).ToList();
        var actualRows = rows.Count - hiddenRows.Count;
        var segmentSummaries = BuildSegmentSummaries(segments, source.Anchor.CurrentMmr, rows);
        var endpointResiduals = segmentSummaries
            .Where(item => item["reconstructed_end_mmr"] is not null)
            .Select(item => new Dictionary<string, object?>
            {
                ["segment"] = item["number"],
                ["endpoint_mmr"] = item["endpoint_mmr"],
                ["endpoint_source"] = item["endpoint_source"],
                ["reconstructed_end_mmr"] = item["reconstructed_end_mmr"],
                ["residual"] = item["endpoint_residual"],
            })
            .ToList();

        var summary = new Dictionary<string, object?>
        {
            ["account_id"] = source.AccountId,
            ["model_version"] = ModelVersion,
            ["input_source"] = "authenticated_gc_match_history",
            ["generated_at_utc"] = DateTimeOffset.UtcNow,
            ["curve_start_utc"] = SingleRankStart,
            ["glicko_start_utc"] = GlickoStart,
            ["timeline_matches"] = source.Timeline.Count,
            ["visible_matches"] = source.Timeline.Count(match => match.Reported is not null),
            ["hidden_matches"] = source.Timeline.Count(match => match.Reported is null),
            ["gc_probe_anchor"] = new Dictionary<string, object?>
            {
                ["current_mmr"] = source.Anchor.CurrentMmr,
                ["base_uncertainty"] = source.Anchor.BaseUncertainty,
                ["projected_uncertainty"] = source.Anchor.ProjectedUncertainty,
                ["confidence_percent"] = source.Anchor.ConfidencePercent,
                ["time_base_unix"] = source.Anchor.TimeBaseUnix,
                ["observed_at_unix"] = source.Anchor.ObservedAtUnix,
            },
            ["confidence_proxy"] = new Dictionary<string, object?>
            {
                ["information_gain_per_match"] = confidence.InformationGain,
                ["mismatches"] = confidence.Mismatches,
                ["start_index"] = confidence.StartIndex,
                ["current_endpoint_target_base_uncertainty"] = source.Anchor.BaseUncertainty,
                ["current_endpoint_modeled_base_uncertainty"] = confidence.EndingBaseUncertainty,
                ["current_endpoint_residual"] = confidence.EndingBaseUncertainty - source.Anchor.BaseUncertainty,
                ["match_update_model"] = "U_after=round(1/sqrt(1/U_before^2+information_gain))",
                ["display_mapping"] = "client.dll build 6907 piecewise quadratic U-to-Confidence mapping",
            },
            ["double_down_mixture"] = new Dictionary<string, object?>
            {
                ["double_down_rate"] = doubleDown.Rate,
                ["residual_sigma"] = doubleDown.Sigma,
                ["observations"] = doubleDown.Observations,
                ["effective_double_downs"] = doubleDown.EffectiveDoubleDowns,
                ["probable_double_downs"] = doubleDown.ProbableDoubleDowns,
                ["fallback_used"] = doubleDown.FallbackUsed,
                ["model"] = "Normal(base,sigma) vs Normal(2*base,sigma) fitted by EM",
            },
            ["hidden_segments"] = segmentSummaries,
            ["curve_reconstruction"] = new Dictionary<string, object?>
            {
                ["matches"] = rows.Count,
                ["actual_gc_matches"] = actualRows,
                ["endpoint_constrained_matches"] = hiddenRows.Count,
                ["hidden_segments"] = endpointResiduals.Count,
                ["endpoint_residuals"] = endpointResiduals,
                ["all_hidden_endpoints_exact"] = endpointResiduals.All(item => Convert.ToInt32(item["residual"], CultureInfo.InvariantCulture) == 0),
                ["method"] = "Glicko-shaped prior + latent Double Down probabilities + sign-preserving integer endpoint projection",
            },
            ["caveat"] = "低 Rank Confidence 区间逐局变化是端点约束拟合，不是 Valve 服务器逐局真值。",
        };

        var dataset = new Dictionary<string, object?>
        {
            ["schema_version"] = 1,
            ["account_id"] = source.AccountId,
            ["model_version"] = ModelVersion,
            ["input_source"] = "authenticated_gc_match_history",
            ["generated_at_utc"] = DateTimeOffset.UtcNow,
            ["curve_start_utc"] = SingleRankStart,
            ["glicko_start_utc"] = GlickoStart,
            ["rows"] = rows,
        };

        WriteJsonAtomically(summaryPath, summary, PrettyJson);
        WriteCsv(estimatesPath, CurveFields, rows);
        WriteCsv(curvePath, CurveFields, rows);
        WriteHiddenSegmentsCsv(segmentsPath, segmentSummaries);
        WriteJsonAtomically(datasetPath, dataset, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        });
        WriteSvg(svgPath, source.AccountId, rows);
        WritePng(pngPath, source.AccountId, rows);
        var heroReport = BuildHeroContributionReport(source.AccountId, rows);
        WriteHeroContributionReport(heroReportPath, heroReport);
        HeroContributionSupplementaryReports.WriteMarkdown(heroMarkdownPath, heroReport);
        HeroContributionSupplementaryReports.WriteWorkbook(heroWorkbookPath, heroReport);
        WriteStandaloneHtml(htmlPath, dataset, source.AccountId);

        var manifestPath = Path.Combine(
            Directory.GetParent(outputDirectory)?.FullName ?? outputDirectory,
            "reconstruction-manifest.json");
        var outputPaths = new[]
        {
            summaryPath, estimatesPath, curvePath, svgPath, pngPath, heroReportPath,
            heroMarkdownPath, heroWorkbookPath,
            segmentsPath, datasetPath, htmlPath,
        };
        WriteJsonAtomically(manifestPath, new Dictionary<string, object?>
        {
            ["schema_version"] = 1,
            ["account_id"] = source.AccountId,
            ["model_version"] = ModelVersion,
            ["generated_at_utc"] = DateTimeOffset.UtcNow,
            ["collector_output"] = Path.GetFullPath(rawCollectionPath),
            ["model_output_directory"] = Path.GetFullPath(outputDirectory),
            ["outputs"] = outputPaths.Select(Path.GetFullPath).ToArray(),
            ["raw_input_was_modified"] = false,
        }, PrettyJson);

        return new ReconstructionOutput(
            source.AccountId,
            rows.Count,
            actualRows,
            hiddenRows.Count,
            outputDirectory,
            htmlPath,
            outputPaths.Append(manifestPath).ToArray());
    }

    private static SourceData LoadSource(string path, uint? expectedAccountId)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var accountId = RequiredUInt(root, "AccountId");
        if (expectedAccountId is not null && expectedAccountId.Value != accountId)
        {
            throw new InvalidDataException(
                $"GC 文件属于账号 {accountId}，不是请求账号 {expectedAccountId.Value}。");
        }

        if (!root.TryGetProperty("CapturedAtUtc", out var capturedElement)
            || !DateTimeOffset.TryParse(capturedElement.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var capturedAt))
        {
            throw new InvalidDataException("GC 文件缺少有效 CapturedAtUtc。");
        }

        if (!root.TryGetProperty("CurrentRank", out var currentRank)
            || currentRank.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("GC 文件没有 CurrentRank。");
        }

        var currentMmr = RequiredObservedInt(currentRank, "RankValue");
        var baseUncertainty = RequiredObservedInt(currentRank, "RankData1");
        var timeBaseUnix = RequiredObservedInt64(currentRank, "RankData3");
        var observedAtUnix = capturedAt.ToUnixTimeSeconds();
        var projected = ProjectUncertainty(baseUncertainty, timeBaseUnix, observedAtUnix);
        var anchor = new RankAnchor(
            currentMmr,
            baseUncertainty,
            projected,
            RankConfidencePercent(projected),
            timeBaseUnix,
            observedAtUnix);

        if (!root.TryGetProperty("MatchHistory", out var history)
            || history.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("GC 文件没有 MatchHistory。");
        }
        if (!history.TryGetProperty("Finished", out var finished) || !finished.GetBoolean())
        {
            throw new InvalidDataException("GC Match History 尚未完整下载。");
        }
        if (history.TryGetProperty("Error", out var error)
            && error.ValueKind is not JsonValueKind.Null
            && !string.IsNullOrWhiteSpace(error.GetString()))
        {
            throw new InvalidDataException($"GC Match History 错误：{error.GetString()}");
        }
        if (!history.TryGetProperty("Matches", out var matches)
            || matches.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("GC MatchHistory.Matches 不是数组。");
        }

        var byMatchId = new Dictionary<ulong, TimelineMatch>();
        foreach (var raw in matches.EnumerateArray())
        {
            if (OptionalObservedInt64(raw, "LobbyType") != 7)
            {
                continue;
            }
            var matchId = RequiredObservedUInt64(raw, "MatchId");
            var startUnix = RequiredObservedInt64(raw, "StartTime");
            var startedAt = DateTimeOffset.FromUnixTimeSeconds(startUnix);
            if (startedAt < SingleRankStart)
            {
                continue;
            }

            var rankChange = OptionalObservedInt64(raw, "RankChange");
            var winner = OptionalObservedBoolean(raw, "Winner");
            var won = winner ?? (rankChange is not null && rankChange != 0
                ? rankChange > 0
                : null);
            if (won is null)
            {
                continue;
            }

            var previousRank = OptionalObservedInt64(raw, "PreviousRank");
            ReportedMmr? reported = null;
            if (previousRank is > 0 && rankChange is not null)
            {
                reported = new ReportedMmr((int)previousRank.Value, (int)rankChange.Value);
            }
            if (startedAt < GlickoStart && reported is null)
            {
                continue;
            }

            byMatchId[matchId] = new TimelineMatch(
                matchId,
                startedAt,
                (int)(OptionalObservedInt64(raw, "Duration") ?? 0),
                won.Value,
                (int)(OptionalObservedInt64(raw, "HeroId") ?? 0),
                reported);
        }

        var timeline = byMatchId.Values
            .OrderBy(match => match.StartedAt)
            .ThenBy(match => match.MatchId)
            .ToList();
        if (timeline.Count == 0)
        {
            throw new InvalidDataException("GC 历史中没有 2020-03-02 之后的天梯记录。");
        }
        return new SourceData(accountId, anchor, timeline);
    }

    private static List<HiddenSegment> FindHiddenSegments(IReadOnlyList<TimelineMatch> timeline)
    {
        var segments = new List<HiddenSegment>();
        int? start = null;
        for (var index = 0; index <= timeline.Count; index++)
        {
            var hidden = index < timeline.Count && timeline[index].Reported is null;
            if (hidden && start is null)
            {
                start = index;
            }
            if (!hidden && start is not null)
            {
                var matches = timeline.Skip(start.Value).Take(index - start.Value).ToList();
                segments.Add(new HiddenSegment(
                    segments.Count + 1,
                    matches,
                    start.Value > 0 ? timeline[start.Value - 1] : null,
                    index < timeline.Count ? timeline[index] : null));
                start = null;
            }
        }
        return segments;
    }

    private static ConfidenceFit FitConfidenceProxy(
        IReadOnlyList<TimelineMatch> timeline,
        int currentBaseUncertainty)
    {
        var firstHidden = IndexOf(timeline, match => match.Reported is null);
        int startIndex;
        if (firstHidden < 0)
        {
            startIndex = 0;
        }
        else
        {
            var firstRecovered = IndexOf(
                timeline,
                match => match.Reported is not null,
                firstHidden + 1);
            startIndex = firstRecovered >= 0 ? firstRecovered : Math.Max(0, firstHidden - 1);
        }

        ConfidenceFit? best = null;
        for (var step = 1; step <= 1_000; step++)
        {
            var gain = step * 1e-8;
            var simulation = SimulateUncertainty(timeline, startIndex, gain);
            var mismatches = 0;
            var logLoss = 0.0;
            for (var index = startIndex; index < timeline.Count; index++)
            {
                var uncertainty = simulation.Uncertainties[index]!.Value;
                var observed = timeline[index].Reported is not null;
                var probability = 1.0 / (1.0 + Math.Exp((uncertainty - 150.5) / 2.0));
                probability = Math.Clamp(probability, 1e-9, 1 - 1e-9);
                logLoss -= observed ? Math.Log(probability) : Math.Log(1 - probability);
                if ((uncertainty <= 150) != observed)
                {
                    mismatches++;
                }
            }
            var candidate = new ConfidenceFit(
                gain,
                startIndex,
                mismatches,
                logLoss,
                simulation.EndingBaseUncertainty,
                simulation.Uncertainties,
                simulation.Confidences);
            if (best is null || IsBetterConfidenceFit(candidate, best, currentBaseUncertainty))
            {
                best = candidate;
            }
        }
        return best!;
    }

    private static bool IsBetterConfidenceFit(
        ConfidenceFit candidate,
        ConfidenceFit current,
        int target)
    {
        var candidateEndpoint = Math.Abs(candidate.EndingBaseUncertainty - target);
        var currentEndpoint = Math.Abs(current.EndingBaseUncertainty - target);
        if (candidateEndpoint != currentEndpoint)
        {
            return candidateEndpoint < currentEndpoint;
        }
        if (candidate.Mismatches != current.Mismatches)
        {
            return candidate.Mismatches < current.Mismatches;
        }
        if (Math.Abs(candidate.LogLoss - current.LogLoss) > 1e-12)
        {
            return candidate.LogLoss < current.LogLoss;
        }
        return candidate.InformationGain < current.InformationGain;
    }

    private static UncertaintySimulation SimulateUncertainty(
        IReadOnlyList<TimelineMatch> timeline,
        int startIndex,
        double informationGain)
    {
        var uncertainties = Enumerable.Repeat<int?>(null, timeline.Count).ToList();
        var confidences = Enumerable.Repeat<double?>(null, timeline.Count).ToList();
        var uncertaintyBefore = 150;
        for (var index = startIndex + 1; index < timeline.Count; index++)
        {
            var previous = timeline[index - 1];
            var afterPrevious = UpdateUncertaintyAfterMatch(uncertaintyBefore, informationGain);
            uncertaintyBefore = ProjectUncertainty(
                afterPrevious,
                previous.EndedAt.ToUnixTimeSeconds(),
                timeline[index].StartedAt.ToUnixTimeSeconds());
            uncertainties[index] = uncertaintyBefore;
            confidences[index] = RankConfidencePercent(uncertaintyBefore) / 100.0;
        }
        uncertainties[startIndex] = 150;
        confidences[startIndex] = 0.30;
        return new UncertaintySimulation(
            uncertainties,
            confidences,
            UpdateUncertaintyAfterMatch(uncertaintyBefore, informationGain));
    }

    private static int UpdateUncertaintyAfterMatch(int before, double informationGain)
    {
        if (informationGain <= 0)
        {
            return before;
        }
        var updated = Math.Sqrt(1.0 / (1.0 / (before * (double)before) + informationGain));
        return Math.Clamp((int)Math.Floor(updated + 0.5), 90, 3_000);
    }

    private static int ProjectUncertainty(int baseUncertainty, long timeBaseUnix, long nowUnix)
    {
        if (timeBaseUnix == 0 || nowUnix <= timeBaseUnix)
        {
            return baseUncertainty;
        }
        var elapsedSeconds = F32(nowUnix - timeBaseUnix);
        var elapsedDays = F32(elapsedSeconds / F32(86_400.0));
        var timeFactor = F32(F32(elapsedDays * F32(0.3)) / F32(80.0));
        if (timeFactor <= 0)
        {
            return baseUncertainty;
        }
        var baseValue = F32(baseUncertainty);
        var baseVariance = F32(baseValue * baseValue);
        var referenceVariance = F32(F32(250.0) * F32(250.0));
        var floorVariance = F32(F32(90.0) * F32(90.0));
        var span = F32(referenceVariance - floorVariance);
        var variance = F32(baseVariance + F32(timeFactor * span));
        if (variance < 0)
        {
            return 90;
        }
        var rounded = (int)F32(F32(MathF.Sqrt(variance)) + F32(0.5));
        return Math.Clamp(rounded, 90, 3_000);
    }

    private static int RankConfidencePercent(int uncertainty)
    {
        double score;
        int minimum;
        int maximum;
        if (uncertainty <= 165)
        {
            score = 0.0056 * uncertainty * uncertainty - 2.55 * uncertainty + 286.56;
            minimum = 18;
            maximum = 100;
        }
        else if (uncertainty <= 240)
        {
            score = 0.0016 * uncertainty * uncertainty - 0.8022 * uncertainty + 107.78;
            minimum = 7;
            maximum = 18;
        }
        else if (uncertainty <= 820)
        {
            score = 0.0000165 * uncertainty * uncertainty - 0.0283 * uncertainty + 12.6;
            minimum = 1;
            maximum = 7;
        }
        else
        {
            return 0;
        }
        var rounded = (int)MathF.Round(F32(score), MidpointRounding.AwayFromZero);
        return Math.Clamp(rounded, minimum, maximum);
    }

    private static DoubleDownFit FitDoubleDownModel(
        IReadOnlyList<TimelineMatch> timeline,
        ConfidenceFit confidence)
    {
        var observations = new List<(double Actual, double Normal)>();
        for (var index = confidence.StartIndex; index < timeline.Count; index++)
        {
            var match = timeline[index];
            if (match.Reported is null || confidence.Uncertainties[index] is null)
            {
                continue;
            }
            var delta = match.Reported.RankChange;
            if (delta == 0 || (delta > 0) != match.Won)
            {
                continue;
            }
            var normal = Math.Abs(GlickoSaturatingPrior(confidence.Uncertainties[index]!.Value, match.Won));
            observations.Add((Math.Abs(delta), normal));
        }
        if (observations.Count == 0)
        {
            return new DoubleDownFit(0.05, 5.0, 0, 0, 0, true);
        }

        var rate = 0.05;
        var sigma = 5.0;
        var probabilities = new double[observations.Count];
        for (var iteration = 0; iteration < 200; iteration++)
        {
            for (var index = 0; index < observations.Count; index++)
            {
                probabilities[index] = DoubleDownProbability(
                    observations[index].Actual,
                    observations[index].Normal,
                    rate,
                    sigma);
            }
            var nextRate = Math.Clamp(probabilities.Average(), 0.001, 0.30);
            var variance = observations.Select((observation, index) =>
                (1 - probabilities[index]) * Math.Pow(observation.Actual - observation.Normal, 2)
                + probabilities[index] * Math.Pow(observation.Actual - 2 * observation.Normal, 2)).Average();
            var nextSigma = Math.Clamp(Math.Sqrt(variance), 2.0, 20.0);
            if (Math.Abs(nextRate - rate) + Math.Abs(nextSigma - sigma) < 1e-9)
            {
                rate = nextRate;
                sigma = nextSigma;
                break;
            }
            rate = nextRate;
            sigma = nextSigma;
        }
        for (var index = 0; index < observations.Count; index++)
        {
            probabilities[index] = DoubleDownProbability(
                observations[index].Actual,
                observations[index].Normal,
                rate,
                sigma);
        }
        return new DoubleDownFit(
            rate,
            sigma,
            observations.Count,
            probabilities.Sum(),
            probabilities.Count(value => value >= 0.5),
            false);
    }

    private static double GlickoSaturatingPrior(int uncertainty, bool won)
    {
        var stableMagnitude = won ? 27.0 : 25.0;
        const double stableUncertainty = 90.0;
        const double boundaryUncertainty = 150.0;
        const double boundaryMagnitude = 40.0;
        var scale = (boundaryMagnitude - stableMagnitude)
            / (stableMagnitude / Math.Pow(stableUncertainty, 2)
               - boundaryMagnitude / Math.Pow(boundaryUncertainty, 2));
        var asymptote = stableMagnitude
            * (Math.Pow(stableUncertainty, 2) + scale)
            / Math.Pow(stableUncertainty, 2);
        var squared = uncertainty * (double)uncertainty;
        var magnitude = asymptote * squared / (squared + scale);
        return won ? magnitude : -magnitude;
    }

    private static double DoubleDownProbability(
        double actual,
        double normal,
        double rate,
        double sigma)
    {
        rate = Math.Clamp(rate, 1e-6, 0.49);
        sigma = Math.Max(1e-6, sigma);
        var logOdds = Math.Log(rate / (1 - rate)) - 0.5
            * (Math.Pow(actual - 2 * normal, 2) - Math.Pow(actual - normal, 2))
            / Math.Pow(sigma, 2);
        if (logOdds >= 0)
        {
            return 1.0 / (1.0 + Math.Exp(-Math.Min(logOdds, 700)));
        }
        var odds = Math.Exp(Math.Max(logOdds, -700));
        return odds / (1 + odds);
    }

    private static List<double> InferDoubleDownPosterior(
        IReadOnlyList<double> priors,
        int targetChange,
        double rate,
        double sigma)
    {
        rate = Math.Clamp(rate, 1e-6, 0.49);
        sigma = Math.Max(1e-6, sigma);
        var extras = priors.Select(RoundAwayFromZero).ToArray();
        var mass = new Dictionary<int, double> { [0] = 1.0 };
        var ddMass = new List<Dictionary<int, double>>();
        for (var index = 0; index < extras.Length; index++)
        {
            var nextMass = new Dictionary<int, double>();
            var nextDdMass = Enumerable.Range(0, index + 1)
                .Select(_ => new Dictionary<int, double>())
                .ToList();
            foreach (var (total, probability) in mass)
            {
                AddMass(nextMass, total, probability * (1 - rate));
                AddMass(nextMass, total + extras[index], probability * rate);
                AddMass(nextDdMass[index], total + extras[index], probability * rate);
            }
            for (var previousIndex = 0; previousIndex < index; previousIndex++)
            {
                foreach (var (total, probability) in ddMass[previousIndex])
                {
                    AddMass(nextDdMass[previousIndex], total, probability * (1 - rate));
                    AddMass(nextDdMass[previousIndex], total + extras[index], probability * rate);
                }
            }
            mass = nextMass;
            ddMass = nextDdMass;
        }

        var normalChange = priors.Sum();
        var endpointVariance = Math.Max(1.0, priors.Count * sigma * sigma);
        var logLikelihood = mass.Keys.ToDictionary(
            total => total,
            total => -0.5 * Math.Pow(targetChange - normalChange - total, 2) / endpointVariance);
        var peak = logLikelihood.Values.Max();
        var likelihood = logLikelihood.ToDictionary(pair => pair.Key, pair => Math.Exp(pair.Value - peak));
        var evidence = mass.Sum(pair => pair.Value * likelihood[pair.Key]);
        if (evidence <= 0 || !double.IsFinite(evidence))
        {
            return Enumerable.Repeat(rate, priors.Count).ToList();
        }
        return ddMass.Select(perMatch =>
            perMatch.Sum(pair => pair.Value * likelihood[pair.Key]) / evidence).ToList();
    }

    private static List<int> AllocateEndpointConstrained(
        IReadOnlyList<double> priors,
        int targetChange,
        int minimumMagnitude = 10,
        int maximumMagnitude = 240)
    {
        var signs = priors.Select(value => value > 0 ? 1 : -1).ToArray();
        var magnitudes = priors.Select(Math.Abs).ToArray();
        var minimumChange = signs.Sum(sign => sign > 0 ? minimumMagnitude : -maximumMagnitude);
        var maximumChange = signs.Sum(sign => sign > 0 ? maximumMagnitude : -minimumMagnitude);
        if (targetChange < minimumChange || targetChange > maximumChange)
        {
            throw new InvalidDataException(
                $"端点变化 {targetChange} 超出保持胜负符号的可行范围 {minimumChange}..{maximumChange}。");
        }

        var free = Enumerable.Range(0, priors.Count).ToHashSet();
        var fixedValues = new Dictionary<int, double>();
        var projected = new double[priors.Count];
        while (free.Count > 0)
        {
            var fixedChange = fixedValues.Sum(pair => signs[pair.Key] * pair.Value);
            var priorChange = free.Sum(index => signs[index] * magnitudes[index]);
            var adjustment = (targetChange - fixedChange - priorChange) / free.Count;
            var violations = new List<(int Index, double Boundary)>();
            foreach (var index in free)
            {
                var candidate = magnitudes[index] + adjustment * signs[index];
                if (candidate < minimumMagnitude)
                {
                    violations.Add((index, minimumMagnitude));
                }
                else if (candidate > maximumMagnitude)
                {
                    violations.Add((index, maximumMagnitude));
                }
            }
            if (violations.Count == 0)
            {
                foreach (var index in free)
                {
                    projected[index] = magnitudes[index] + adjustment * signs[index];
                }
                break;
            }
            foreach (var (index, boundary) in violations)
            {
                fixedValues[index] = boundary;
                projected[index] = boundary;
                free.Remove(index);
            }
        }
        foreach (var (index, value) in fixedValues)
        {
            projected[index] = value;
        }

        var integerMagnitudes = projected.Select(value => (int)Math.Floor(value + 0.5)).ToArray();
        var deltas = integerMagnitudes.Select((value, index) => signs[index] * value).ToArray();
        var residual = targetChange - deltas.Sum();
        while (residual != 0)
        {
            var direction = residual > 0 ? 1 : -1;
            var choices = new List<(double Cost, int Index, int NextMagnitude)>();
            for (var index = 0; index < deltas.Length; index++)
            {
                var nextDelta = deltas[index] + direction;
                if (nextDelta == 0 || Math.Sign(nextDelta) != signs[index])
                {
                    continue;
                }
                var nextMagnitude = Math.Abs(nextDelta);
                if (nextMagnitude < minimumMagnitude || nextMagnitude > maximumMagnitude)
                {
                    continue;
                }
                var cost = Math.Pow(nextMagnitude - magnitudes[index], 2)
                    - Math.Pow(integerMagnitudes[index] - magnitudes[index], 2);
                choices.Add((cost, index, nextMagnitude));
            }
            if (choices.Count == 0)
            {
                throw new InvalidDataException("无法把约束后的逐局变化取整到精确端点。");
            }
            var chosen = choices.OrderBy(choice => choice.Cost).ThenBy(choice => choice.Index).First();
            integerMagnitudes[chosen.Index] = chosen.NextMagnitude;
            deltas[chosen.Index] += direction;
            residual -= direction;
        }
        return deltas.ToList();
    }

    private static List<Dictionary<string, object?>> BuildCurve(
        IReadOnlyList<TimelineMatch> timeline,
        IReadOnlyList<HiddenSegment> segments,
        ConfidenceFit confidence,
        int currentMmr,
        DoubleDownFit doubleDown)
    {
        var indexByMatch = timeline.Select((match, index) => (match.MatchId, index))
            .ToDictionary(pair => pair.MatchId, pair => pair.index);
        var segmentByFirst = segments.ToDictionary(segment => segment.Matches[0].MatchId);
        var rows = new List<Dictionary<string, object?>>();
        int? curveMmr = null;
        var index = 0;
        while (index < timeline.Count)
        {
            var match = timeline[index];
            if (segmentByFirst.TryGetValue(match.MatchId, out var segment))
            {
                if (segment.PreviousVisible?.Reported is null)
                {
                    index += segment.Matches.Count;
                    continue;
                }
                var startMmr = segment.PreviousVisible.Reported.EndMmr;
                int? endpointMmr;
                string? endpointSource;
                if (segment.NextVisible?.Reported is not null)
                {
                    endpointMmr = segment.NextVisible.Reported.StartMmr;
                    endpointSource = "next_visible_match";
                }
                else
                {
                    endpointMmr = currentMmr;
                    endpointSource = "current_rank_gc";
                }

                var confidences = new List<double>();
                var uncertainties = new List<int>();
                var normalPriors = new List<double>();
                for (var position = 0; position < segment.Matches.Count; position++)
                {
                    var hidden = segment.Matches[position];
                    var matchIndex = indexByMatch[hidden.MatchId];
                    var estimate = confidence.Confidences[matchIndex]
                        ?? 0.30 * position / segment.Matches.Count;
                    var confidenceValue = Math.Clamp(estimate, 0, 0.299);
                    var uncertainty = confidence.Uncertainties[matchIndex]
                        ?? UncertaintyForConfidence(confidenceValue);
                    confidences.Add(confidenceValue);
                    uncertainties.Add(uncertainty);
                    normalPriors.Add(GlickoSaturatingPrior(uncertainty, hidden.Won));
                }

                var targetChange = endpointMmr.Value - startMmr;
                var crossesTransition = segment.PreviousVisible.StartedAt < GlickoStart
                    && segment.Matches[0].StartedAt >= GlickoStart;
                var probabilities = crossesTransition
                    ? Enumerable.Repeat(0.0, normalPriors.Count).ToList()
                    : InferDoubleDownPosterior(
                        normalPriors,
                        targetChange,
                        doubleDown.Rate,
                        doubleDown.Sigma);
                var priors = normalPriors.Select((prior, position) =>
                    prior * (1 + probabilities[position])).ToList();
                var deltas = AllocateEndpointConstrained(priors, targetChange);
                curveMmr = startMmr;
                for (var position = 0; position < segment.Matches.Count; position++)
                {
                    var hidden = segment.Matches[position];
                    var before = curveMmr.Value;
                    curveMmr += deltas[position];
                    rows.Add(CreateRow(
                        hidden,
                        false,
                        "<30% inferred",
                        uncertainties[position],
                        confidences[position],
                        confidences[position],
                        segment.Number,
                        null,
                        null,
                        null,
                        probabilities[position] >= 0.5,
                        probabilities[position],
                        1 + probabilities[position],
                        crossesTransition ? "disabled_glicko_launch"
                            : probabilities[position] >= 0.5 ? "probable"
                            : probabilities[position] >= 0.2 ? "possible" : "unlikely",
                        normalPriors[position],
                        priors[position],
                        deltas[position] - priors[position],
                        deltas[position],
                        before,
                        curveMmr.Value,
                        endpointMmr,
                        endpointSource,
                        0,
                        "Hidden, endpoint constrained"));
                }
                if (curveMmr != endpointMmr)
                {
                    throw new InvalidDataException($"隐藏区间 {segment.Number} 未命中端点。");
                }
                index += segment.Matches.Count;
                continue;
            }

            if (match.Reported is null)
            {
                throw new InvalidDataException($"隐藏比赛 {match.MatchId} 未归入区间。");
            }
            var actual = match.Reported;
            if (match.StartedAt < GlickoStart)
            {
                var jump = curveMmr is null ? 0 : actual.StartMmr - curveMmr.Value;
                curveMmr = actual.EndMmr;
                rows.Add(CreateRow(
                    match, true, "single-rank pre-Glicko exact", null, null, null, null,
                    actual.StartMmr, actual.RankChange, actual.EndMmr, false, null, null,
                    "not_applicable_pre_glicko", null, null, null, actual.RankChange,
                    actual.StartMmr, actual.EndMmr, null, null, jump,
                    "GC actual (single-rank pre-Glicko)"));
                index++;
                continue;
            }

            var rawConfidence = confidence.Confidences[index];
            var confidenceValueVisible = Math.Clamp(rawConfidence ?? 0.30, 0.30, 1.0);
            var uncertaintyVisible = confidence.Uncertainties[index]
                ?? UncertaintyForConfidence(confidenceValueVisible);
            var normalVisible = GlickoSaturatingPrior(uncertaintyVisible, match.Won);
            var probabilityVisible = DoubleDownProbability(
                Math.Abs(actual.RankChange),
                Math.Abs(normalVisible),
                doubleDown.Rate,
                doubleDown.Sigma);
            var priorVisible = normalVisible * (1 + probabilityVisible);
            var anchorJump = curveMmr is null ? 0 : actual.StartMmr - curveMmr.Value;
            curveMmr = actual.EndMmr;
            rows.Add(CreateRow(
                match, true, ">=30%", uncertaintyVisible, rawConfidence,
                confidenceValueVisible, null, actual.StartMmr, actual.RankChange,
                actual.EndMmr, probabilityVisible >= 0.5, probabilityVisible,
                1 + probabilityVisible, probabilityVisible >= 0.5 ? "probable" : "unlikely",
                normalVisible, priorVisible, actual.RankChange - priorVisible,
                actual.RankChange, actual.StartMmr, actual.EndMmr, null, null,
                anchorJump, "GC actual"));
            index++;
        }
        return rows;
    }

    private static Dictionary<string, object?> CreateRow(
        TimelineMatch match,
        bool visible,
        string confidenceRegime,
        int? uncertainty,
        double? confidenceProxy,
        double? confidenceUsed,
        int? segment,
        int? actualStart,
        int? actualDelta,
        int? actualEnd,
        bool likelyDoubleDown,
        double? doubleDownProbability,
        double? expectedMultiplier,
        string doubleDownStatus,
        double? normalPrior,
        double? unconstrained,
        double? endpointCorrection,
        int modeledDelta,
        int before,
        int after,
        int? endpointMmr,
        string? endpointSource,
        int anchorJump,
        string source) => new()
        {
            ["date_utc"] = match.StartedAt.ToString("O", CultureInfo.InvariantCulture),
            ["unix_time"] = match.StartedAt.ToUnixTimeSeconds(),
            ["match_id"] = match.MatchId.ToString(CultureInfo.InvariantCulture),
            ["result"] = match.Won ? "Win" : "Loss",
            ["hero_id"] = match.HeroId,
            ["average_rank"] = null,
            ["party_size"] = null,
            ["mmr_fields_visible"] = visible,
            ["confidence_regime"] = confidenceRegime,
            ["uncertainty_proxy"] = uncertainty,
            ["confidence_proxy"] = confidenceProxy,
            ["confidence_used"] = confidenceUsed,
            ["segment"] = segment,
            ["actual_start_mmr"] = actualStart,
            ["actual_rank_change"] = actualDelta,
            ["actual_end_mmr"] = actualEnd,
            ["likely_double_down"] = likelyDoubleDown,
            ["double_down_probability"] = doubleDownProbability,
            ["expected_double_down_multiplier"] = expectedMultiplier,
            ["double_down_status"] = doubleDownStatus,
            ["normal_rank_change_prior"] = normalPrior,
            ["unconstrained_rank_change"] = unconstrained,
            ["endpoint_correction"] = endpointCorrection,
            ["modeled_rank_change"] = modeledDelta,
            ["curve_mmr_before"] = before,
            ["curve_mmr_after"] = after,
            ["segment_endpoint_mmr"] = endpointMmr,
            ["segment_endpoint_source"] = endpointSource,
            ["anchor_jump_before"] = anchorJump,
            ["curve_source"] = source,
        };

    private static List<Dictionary<string, object?>> BuildSegmentSummaries(
        IReadOnlyList<HiddenSegment> segments,
        int currentMmr,
        IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var summaries = new List<Dictionary<string, object?>>();
        foreach (var segment in segments)
        {
            int? endpoint = segment.NextVisible?.Reported?.StartMmr
                ?? (segment.NextVisible is null ? currentMmr : null);
            var segmentRows = rows.Where(row =>
                row["segment"] is not null
                && Convert.ToInt32(row["segment"], CultureInfo.InvariantCulture) == segment.Number).ToList();
            var reconstructed = segmentRows.Count > 0
                ? Convert.ToInt32(segmentRows[^1]["curve_mmr_after"], CultureInfo.InvariantCulture)
                : (int?)null;
            summaries.Add(new Dictionary<string, object?>
            {
                ["number"] = segment.Number,
                ["start"] = segment.Matches[0].StartedAt.ToString("O", CultureInfo.InvariantCulture),
                ["end"] = segment.Matches[^1].StartedAt.ToString("O", CultureInfo.InvariantCulture),
                ["matches"] = segment.Matches.Count,
                ["wins"] = segment.Matches.Count(match => match.Won),
                ["losses"] = segment.Matches.Count(match => !match.Won),
                ["observed_total_change"] = endpoint is not null && segment.PreviousVisible?.Reported is not null
                    ? endpoint - segment.PreviousVisible.Reported.EndMmr : null,
                ["endpoint_source"] = segment.NextVisible?.Reported is not null
                    ? "next_visible_match" : segment.NextVisible is null ? "current_rank_gc" : null,
                ["previous_visible_end_mmr"] = segment.PreviousVisible?.Reported?.EndMmr,
                ["next_visible_start_mmr"] = segment.NextVisible?.Reported?.StartMmr,
                ["endpoint_mmr"] = endpoint,
                ["reconstructed_end_mmr"] = reconstructed,
                ["endpoint_residual"] = endpoint is not null && reconstructed is not null
                    ? reconstructed - endpoint : null,
                ["expected_double_downs"] = segmentRows.Sum(row =>
                    row["double_down_probability"] is null
                        ? 0 : Convert.ToDouble(row["double_down_probability"], CultureInfo.InvariantCulture)),
            });
        }
        return summaries;
    }

    private static int UncertaintyForConfidence(double confidence)
    {
        var target = (int)Math.Floor(Math.Clamp(confidence, 0, 1) * 100 + 0.5);
        var candidate = Enumerable.Range(90, 732)
            .OrderBy(value => Math.Abs(RankConfidencePercent(value) - target))
            .ThenBy(value => value)
            .First();
        return confidence < 0.30 ? Math.Max(151, candidate) : candidate;
    }

    private static void WriteStandaloneHtml(
        string path,
        IReadOnlyDictionary<string, object?> dataset,
        uint accountId)
    {
        const string resourceName = "Dota2MmrCollector.Assets.mmr-table-viewer.html";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("发布包缺少内嵌 HTML 查看器。");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var viewer = reader.ReadToEnd();
        const string marker = "<script id=\"viewer-script\">";
        if (!viewer.Contains(marker, StringComparison.Ordinal))
        {
            throw new InvalidDataException("HTML 查看器模板缺少 viewer-script 标记。");
        }
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["schema_version"] = 1,
            ["fileName"] = $"mmr-dataset-{accountId}.json",
            ["metadata"] = new Dictionary<string, object?>
            {
                ["account_id"] = accountId,
                ["model_version"] = ModelVersion,
            },
            ["rows"] = dataset["rows"],
        }).Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase);
        var embedded = $"<script id=\"embedded-dataset\" type=\"application/json\">{payload}</script>";
        WriteTextAtomically(path, viewer.Replace(marker, embedded + Environment.NewLine + "  " + marker, StringComparison.Ordinal), new UTF8Encoding(false));
    }

    private static void WriteSvg(
        string path,
        uint accountId,
        IReadOnlyList<Dictionary<string, object?>> rows)
    {
        const int width = 1400;
        const int height = 720;
        const int left = 82;
        const int right = 28;
        const int top = 54;
        const int bottom = 72;
        var times = rows.Select(row => Convert.ToInt64(row["unix_time"], CultureInfo.InvariantCulture)).ToArray();
        var values = rows.Select(row => Convert.ToInt32(row["curve_mmr_after"], CultureInfo.InvariantCulture)).ToArray();
        var minTime = times.Min();
        var maxTime = Math.Max(minTime + 1, times.Max());
        var minMmr = values.Min() - 50;
        var maxMmr = Math.Max(minMmr + 100, values.Max() + 50);
        double X(long time) => left + (time - minTime) / (double)(maxTime - minTime) * (width - left - right);
        double Y(int mmr) => top + (maxMmr - mmr) / (double)(maxMmr - minMmr) * (height - top - bottom);
        var points = string.Join(" ", times.Zip(values).Select(pair =>
            $"{X(pair.First):0.##},{Y(pair.Second):0.##}"));
        var svg = $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}">
              <rect width="100%" height="100%" fill="#f8fafc"/>
              <text x="{left}" y="32" font-family="Segoe UI, sans-serif" font-size="22" font-weight="600">Dota 2 MMR — {accountId}</text>
              <line x1="{left}" y1="{top}" x2="{left}" y2="{height - bottom}" stroke="#94a3b8"/>
              <line x1="{left}" y1="{height - bottom}" x2="{width - right}" y2="{height - bottom}" stroke="#94a3b8"/>
              <polyline fill="none" stroke="#1976d2" stroke-width="2" points="{points}"/>
              <text x="18" y="{top}" font-family="Segoe UI, sans-serif" font-size="14">{maxMmr}</text>
              <text x="18" y="{height - bottom}" font-family="Segoe UI, sans-serif" font-size="14">{minMmr}</text>
              <text x="{left}" y="{height - 24}" font-family="Segoe UI, sans-serif" font-size="13">{DateTimeOffset.FromUnixTimeSeconds(minTime):yyyy-MM-dd}</text>
              <text x="{width - right - 90}" y="{height - 24}" font-family="Segoe UI, sans-serif" font-size="13">{DateTimeOffset.FromUnixTimeSeconds(maxTime):yyyy-MM-dd}</text>
              <text x="{left}" y="{height - 5}" font-family="Segoe UI, sans-serif" font-size="12" fill="#64748b">真实点与拟合点的详细区分请查看 mmr-history.html</text>
            </svg>
            """;
        WriteTextAtomically(path, svg, new UTF8Encoding(false));
    }

    private static void WritePng(
        string path,
        uint accountId,
        IReadOnlyList<Dictionary<string, object?>> rows)
    {
        const int width = 2300;
        const int height = 1250;
        const float left = 156;
        const float right = 24;
        const float top = 142;
        const float bottom = 88;
        var plotWidth = width - left - right;
        var plotHeight = height - top - bottom;
        var times = rows
            .Select(row => Convert.ToInt64(row["unix_time"], CultureInfo.InvariantCulture))
            .ToArray();
        var values = rows
            .Select(row => Convert.ToInt32(row["curve_mmr_after"], CultureInfo.InvariantCulture))
            .ToArray();
        var firstMmr = Convert.ToInt32(rows[0]["curve_mmr_before"], CultureInfo.InvariantCulture);
        var minTime = times.Min();
        var maxTime = Math.Max(minTime + 1, times.Max());
        var valueMin = Math.Min(firstMmr, values.Min());
        var valueMax = Math.Max(firstMmr, values.Max());
        var valueSpan = Math.Max(valueMax - valueMin, 100);
        var tickStep = Math.Max(50, (int)Math.Ceiling(valueSpan / 7.0 / 50.0) * 50);
        var axisMin = (int)Math.Floor((valueMin - tickStep / 2.0) / tickStep) * tickStep;
        var axisMax = (int)Math.Ceiling((valueMax + tickStep / 2.0) / tickStep) * tickStep;
        if (axisMax <= axisMin)
        {
            axisMax = axisMin + tickStep;
        }

        float X(long unixTime) => left
            + (float)((unixTime - minTime) / (double)(maxTime - minTime) * plotWidth);
        float Y(int mmr) => top
            + (float)((axisMax - mmr) / (double)(axisMax - axisMin) * plotHeight);

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        bitmap.SetResolution(144, 144);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.Clear(Color.White);

        var textColor = Color.FromArgb(31, 35, 40);
        var mutedColor = Color.FromArgb(87, 96, 106);
        var gridColor = Color.FromArgb(216, 222, 228);
        var borderColor = Color.FromArgb(140, 149, 159);
        var actualColor = Color.FromArgb(9, 105, 218);
        var modeledColor = Color.FromArgb(45, 164, 78);
        var doubleDownColor = Color.FromArgb(191, 135, 0);
        using var titleFont = new Font("Segoe UI", 40, FontStyle.Regular, GraphicsUnit.Pixel);
        using var subtitleFont = new Font("Segoe UI", 25, FontStyle.Regular, GraphicsUnit.Pixel);
        using var labelFont = new Font("Segoe UI", 23, FontStyle.Regular, GraphicsUnit.Pixel);
        using var finalFont = new Font("Segoe UI", 25, FontStyle.Bold, GraphicsUnit.Pixel);
        using var gridPen = new Pen(gridColor, 1.5f);
        using var borderPen = new Pen(borderColor, 1.5f);
        using var actualPen = new Pen(actualColor, 3.2f);
        using var modeledPen = new Pen(modeledColor, 3.2f)
        {
            DashStyle = DashStyle.Dash,
        };
        using var doubleDownBrush = new SolidBrush(doubleDownColor);
        using var textBrush = new SolidBrush(textColor);
        using var mutedBrush = new SolidBrush(mutedColor);
        using var whitePen = new Pen(Color.White, 2);

        var modeledCount = rows.Count(row => GetBoolean(row, "mmr_fields_visible") is false);
        var start = DateTimeOffset.FromUnixTimeSeconds(minTime);
        var end = DateTimeOffset.FromUnixTimeSeconds(maxTime);
        graphics.DrawString(
            $"Account {accountId} · MMR reconstruction",
            titleFont,
            textBrush,
            left,
            8);
        graphics.DrawString(
            $"{start:yyyy-MM} to {end:yyyy-MM} · {rows.Count:N0} ranked matches · "
            + $"{rows.Count - modeledCount:N0} GC actual · {modeledCount:N0} endpoint-constrained",
            subtitleFont,
            mutedBrush,
            left,
            66);

        using var rightAligned = new StringFormat
        {
            Alignment = StringAlignment.Far,
            LineAlignment = StringAlignment.Center,
        };
        for (var tick = axisMin; tick <= axisMax; tick += tickStep)
        {
            var y = Y(tick);
            graphics.DrawLine(gridPen, left, y, left + plotWidth, y);
            graphics.DrawString(
                tick.ToString("N0", CultureInfo.InvariantCulture),
                labelFont,
                textBrush,
                new RectangleF(0, y - 18, left - 22, 36),
                rightAligned);
        }

        const int xTickCount = 7;
        using var centered = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Near,
        };
        using var leftAligned = new StringFormat
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Near,
        };
        for (var index = 0; index < xTickCount; index++)
        {
            var ratio = index / (double)(xTickCount - 1);
            var unixTime = minTime + (long)Math.Round((maxTime - minTime) * ratio);
            var x = X(unixTime);
            graphics.DrawLine(borderPen, x, top + plotHeight, x, top + plotHeight + 7);
            var labelBounds = index switch
            {
                0 => new RectangleF(x, top + plotHeight + 14, 180, 35),
                xTickCount - 1 => new RectangleF(x - 180, top + plotHeight + 14, 180, 35),
                _ => new RectangleF(x - 90, top + plotHeight + 14, 180, 35),
            };
            graphics.DrawString(
                DateTimeOffset.FromUnixTimeSeconds(unixTime).ToString("yyyy-MM", CultureInfo.InvariantCulture),
                labelFont,
                textBrush,
                labelBounds,
                index == 0 ? leftAligned : index == xTickCount - 1 ? rightAligned : centered);
        }

        graphics.DrawRectangle(borderPen, left, top, plotWidth, plotHeight);
        var previousX = left;
        var previousY = Y(firstMmr);
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var x = X(times[index]);
            var y = Y(values[index]);
            var modeled = GetBoolean(row, "mmr_fields_visible") is false;
            var pen = modeled ? modeledPen : actualPen;
            graphics.DrawLine(pen, previousX, previousY, x, previousY);
            graphics.DrawLine(pen, x, previousY, x, y);
            if (modeled
                && row["double_down_probability"] is not null
                && Convert.ToDouble(row["double_down_probability"], CultureInfo.InvariantCulture) >= 0.20)
            {
                graphics.FillEllipse(doubleDownBrush, x - 4.5f, y - 4.5f, 9, 9);
                graphics.DrawEllipse(whitePen, x - 4.5f, y - 4.5f, 9, 9);
            }
            previousX = x;
            previousY = y;
        }

        var finalValue = values[^1];
        graphics.DrawString(
            finalValue.ToString("N0", CultureInfo.InvariantCulture),
            finalFont,
            textBrush,
            new RectangleF(left + plotWidth - 170, Y(finalValue) - 42, 155, 38),
            rightAligned);

        var savedState = graphics.Save();
        graphics.TranslateTransform(28, top + plotHeight / 2);
        graphics.RotateTransform(-90);
        graphics.DrawString("MMR", labelFont, textBrush, 0, 0, centered);
        graphics.Restore(savedState);

        var legendY = height - 31;
        graphics.DrawLine(actualPen, left, legendY, left + 46, legendY);
        graphics.DrawString("GC actual", labelFont, textBrush, left + 60, legendY - 17);
        graphics.DrawLine(modeledPen, left + 250, legendY, left + 296, legendY);
        graphics.DrawString("Endpoint constrained", labelFont, textBrush, left + 310, legendY - 17);
        graphics.FillEllipse(doubleDownBrush, left + 630, legendY - 5, 10, 10);
        graphics.DrawString("DD probability ≥20%", labelFont, textBrush, left + 653, legendY - 17);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            bitmap.Save(temporary, ImageFormat.Png);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static HeroContributionReport BuildHeroContributionReport(
        uint accountId,
        IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var contributions = rows
            .GroupBy(row => Convert.ToInt32(row["hero_id"], CultureInfo.InvariantCulture))
            .Select(group =>
            {
                var actual = group.Where(row => GetBoolean(row, "mmr_fields_visible") is true).ToList();
                var fitted = group.Where(row => GetBoolean(row, "mmr_fields_visible") is false).ToList();
                return new HeroContribution(
                    group.Key,
                    HeroNames.Get(group.Key),
                    group.Count(),
                    group.Count(row => string.Equals(row["result"] as string, "Win", StringComparison.Ordinal)),
                    group.Count(row => string.Equals(row["result"] as string, "Loss", StringComparison.Ordinal)),
                    group.Sum(RankChange),
                    actual.Count,
                    actual.Sum(RankChange),
                    fitted.Count,
                    fitted.Sum(RankChange));
            })
            .OrderByDescending(item => item.TotalContribution)
            .ThenByDescending(item => item.Matches)
            .ThenBy(item => item.HeroName, StringComparer.Ordinal)
            .ToList();

        return new HeroContributionReport(
            accountId,
            Convert.ToString(rows[0]["date_utc"], CultureInfo.InvariantCulture)!,
            Convert.ToString(rows[^1]["date_utc"], CultureInfo.InvariantCulture)!,
            rows.Count,
            contributions);

        static int RankChange(Dictionary<string, object?> row) =>
            Convert.ToInt32(row["modeled_rank_change"], CultureInfo.InvariantCulture);
    }

    private static void WriteHeroContributionReport(string path, HeroContributionReport report)
    {
        var contributions = report.Contributions;
        var builder = new StringBuilder();
        builder.AppendLine("Dota 2 MMR 英雄贡献统计");
        builder.AppendLine($"账号 ID32：{report.AccountId}");
        builder.AppendLine($"区间：{report.StartUtc} 至 {report.EndUtc}");
        builder.AppendLine($"天梯比赛：{report.Matches:N0}；出现英雄：{contributions.Count:N0}");
        builder.AppendLine(
            $"总贡献：{Signed(contributions.Sum(item => item.TotalContribution))} MMR；"
            + $"GC 真实：{Signed(contributions.Sum(item => item.ActualContribution))}；"
            + $"低置信度拟合：{Signed(contributions.Sum(item => item.FittedContribution))}");
        builder.AppendLine();
        builder.AppendLine("口径说明：");
        builder.AppendLine("1. 按总 MMR 贡献从高到低排列，只统计该账号在曲线区间内实际使用过的英雄。");
        builder.AppendLine("2. GC 真实比赛使用服务器返回的 Rank Change；隐藏比赛使用端点约束后的拟合 Rank Change。");
        builder.AppendLine("3. 校准、轨道切换或观测断层产生的 anchor_jump_before 不属于任何单场比赛，因此不归因给英雄。");
        builder.AppendLine("4. 英雄英文名来自内置 OpenDota dotaconstants 快照；Hero ID 始终保留，未知新英雄不会丢失。");
        builder.AppendLine();
        builder.AppendLine(
            "排名\t英雄\tHero ID\t场次\t胜\t负\t胜率\t总MMR贡献\t场均贡献\tGC真实场次\tGC真实贡献\t拟合场次\t拟合贡献");
        for (var index = 0; index < contributions.Count; index++)
        {
            var item = contributions[index];
            var winRate = item.Matches == 0 ? 0 : item.Wins / (double)item.Matches;
            var average = item.Matches == 0 ? 0 : item.TotalContribution / (double)item.Matches;
            builder.Append(index + 1).Append('\t')
                .Append(item.HeroName).Append('\t')
                .Append(item.HeroId).Append('\t')
                .Append(item.Matches).Append('\t')
                .Append(item.Wins).Append('\t')
                .Append(item.Losses).Append('\t')
                .Append(winRate.ToString("0.0%", CultureInfo.InvariantCulture)).Append('\t')
                .Append(Signed(item.TotalContribution)).Append('\t')
                .Append(Signed(average, "0.00")).Append('\t')
                .Append(item.ActualMatches).Append('\t')
                .Append(Signed(item.ActualContribution)).Append('\t')
                .Append(item.FittedMatches).Append('\t')
                .Append(Signed(item.FittedContribution)).AppendLine();
        }
        WriteTextAtomically(path, builder.ToString(), new UTF8Encoding(true));
    }

    private static string Signed(int value) => value.ToString("+0;-0;0", CultureInfo.InvariantCulture);

    private static string Signed(double value, string digits) =>
        value.ToString($"+{digits};-{digits};{digits}", CultureInfo.InvariantCulture);

    private static void WriteHiddenSegmentsCsv(
        string path,
        IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var fields = new[]
        {
            "number", "start", "end", "matches", "wins", "losses", "observed_total_change",
            "endpoint_source", "previous_visible_end_mmr", "next_visible_start_mmr",
            "endpoint_mmr", "reconstructed_end_mmr", "endpoint_residual", "expected_double_downs",
        };
        WriteCsv(path, fields, rows);
    }

    private static void WriteCsv(
        string path,
        IReadOnlyList<string> fields,
        IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(',', fields.Select(CsvEscape)));
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(',', fields.Select(field =>
                CsvEscape(row.TryGetValue(field, out var value) ? CsvValue(value) : string.Empty))));
        }
        WriteTextAtomically(path, builder.ToString(), new UTF8Encoding(true));
    }

    private static string CsvValue(object? value) => value switch
    {
        null => string.Empty,
        bool boolean => boolean ? "true" : "false",
        double number => number.ToString("G17", CultureInfo.InvariantCulture),
        float number => number.ToString("G9", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string CsvEscape(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? '"' + value.Replace("\"", "\"\"") + '"'
            : value;

    private static void WriteJsonAtomically<T>(
        string path,
        T value,
        JsonSerializerOptions options)
    {
        WriteTextAtomically(
            path,
            JsonSerializer.Serialize(value, options) + Environment.NewLine,
            new UTF8Encoding(false));
    }

    private static void WriteTextAtomically(string path, string content, Encoding encoding)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, content, encoding);
        File.Move(temporary, path, true);
    }

    private static bool? GetBoolean(Dictionary<string, object?> row, string name) =>
        row.TryGetValue(name, out var value) && value is bool boolean ? boolean : null;

    private static int IndexOf(
        IReadOnlyList<TimelineMatch> source,
        Func<TimelineMatch, bool> predicate,
        int start = 0)
    {
        for (var index = start; index < source.Count; index++)
        {
            if (predicate(source[index]))
            {
                return index;
            }
        }
        return -1;
    }

    private static float F32(double value) => (float)value;

    private static int RoundAwayFromZero(double value) => value > 0
        ? (int)Math.Floor(value + 0.5)
        : (int)Math.Ceiling(value - 0.5);

    private static void AddMass(Dictionary<int, double> target, int key, double value) =>
        target[key] = target.GetValueOrDefault(key) + value;

    private static uint RequiredUInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var element) || !element.TryGetUInt32(out var value))
        {
            throw new InvalidDataException($"GC 字段 {name} 不是有效无符号整数。");
        }
        return value;
    }

    private static int RequiredObservedInt(JsonElement parent, string name)
    {
        var value = OptionalObservedInt64(parent, name);
        if (value is null || value < int.MinValue || value > int.MaxValue)
        {
            throw new InvalidDataException($"GC 字段 {name} 未返回有效整数。");
        }
        return (int)value.Value;
    }

    private static ulong RequiredObservedUInt64(JsonElement parent, string name)
    {
        if (!TryObservedValue(parent, name, out var value) || !value.TryGetUInt64(out var result))
        {
            throw new InvalidDataException($"GC 字段 {name} 未返回有效无符号整数。");
        }
        return result;
    }

    private static long RequiredObservedInt64(JsonElement parent, string name)
    {
        var value = OptionalObservedInt64(parent, name);
        return value ?? throw new InvalidDataException($"GC 字段 {name} 未返回有效整数。");
    }

    private static long? OptionalObservedInt64(JsonElement parent, string name)
    {
        if (!TryObservedValue(parent, name, out var value))
        {
            return null;
        }
        if (value.TryGetInt64(out var signed))
        {
            return signed;
        }
        if (value.TryGetUInt64(out var unsigned) && unsigned <= long.MaxValue)
        {
            return (long)unsigned;
        }
        return null;
    }

    private static bool? OptionalObservedBoolean(JsonElement parent, string name)
    {
        if (!TryObservedValue(parent, name, out var value))
        {
            return null;
        }
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static bool TryObservedValue(
        JsonElement parent,
        string name,
        out JsonElement value)
    {
        value = default;
        return parent.TryGetProperty(name, out var observation)
            && observation.ValueKind == JsonValueKind.Object
            && observation.TryGetProperty("Present", out var present)
            && present.ValueKind == JsonValueKind.True
            && observation.TryGetProperty("Value", out value);
    }
}

internal sealed record ReconstructionOutput(
    uint AccountId,
    int Matches,
    int ActualMatches,
    int ModeledMatches,
    string OutputDirectory,
    string HtmlPath,
    IReadOnlyList<string> OutputPaths);

internal sealed record SourceData(
    uint AccountId,
    RankAnchor Anchor,
    IReadOnlyList<TimelineMatch> Timeline);

internal sealed record RankAnchor(
    int CurrentMmr,
    int BaseUncertainty,
    int ProjectedUncertainty,
    int ConfidencePercent,
    long TimeBaseUnix,
    long ObservedAtUnix);

internal sealed record ReportedMmr(int StartMmr, int RankChange)
{
    public int EndMmr => StartMmr + RankChange;
}

internal sealed record TimelineMatch(
    ulong MatchId,
    DateTimeOffset StartedAt,
    int DurationSeconds,
    bool Won,
    int HeroId,
    ReportedMmr? Reported)
{
    public DateTimeOffset EndedAt => StartedAt.AddSeconds(DurationSeconds);
}

internal sealed record HiddenSegment(
    int Number,
    IReadOnlyList<TimelineMatch> Matches,
    TimelineMatch? PreviousVisible,
    TimelineMatch? NextVisible);

internal sealed record UncertaintySimulation(
    List<int?> Uncertainties,
    List<double?> Confidences,
    int EndingBaseUncertainty);

internal sealed record ConfidenceFit(
    double InformationGain,
    int StartIndex,
    int Mismatches,
    double LogLoss,
    int EndingBaseUncertainty,
    List<int?> Uncertainties,
    List<double?> Confidences);

internal sealed record DoubleDownFit(
    double Rate,
    double Sigma,
    int Observations,
    double EffectiveDoubleDowns,
    int ProbableDoubleDowns,
    bool FallbackUsed);

internal sealed record HeroContribution(
    int HeroId,
    string HeroName,
    int Matches,
    int Wins,
    int Losses,
    int TotalContribution,
    int ActualMatches,
    int ActualContribution,
    int FittedMatches,
    int FittedContribution);

internal sealed record HeroContributionReport(
    uint AccountId,
    string StartUtc,
    string EndUtc,
    int Matches,
    IReadOnlyList<HeroContribution> Contributions)
{
    public int TotalContribution => Contributions.Sum(item => item.TotalContribution);

    public int ActualContribution => Contributions.Sum(item => item.ActualContribution);

    public int FittedContribution => Contributions.Sum(item => item.FittedContribution);
}
