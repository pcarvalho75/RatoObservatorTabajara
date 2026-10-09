using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Core;

namespace MouseTrackerTabajara.Desktop;

/// <summary>Consumes the actual G05 schema1 document. Full-decoder producer endpoints remain raw until a confirmed interval is supplied.</summary>
public static class RegionStabilityAdapter
{
    public static StabilityEvidenceSnapshot Read(string evidencePath, string databasePath, CancellationToken cancellation = default)
    {
        var document = JsonDefaults.Deserialize<RegionScenarioEvidenceDocument>(File.ReadAllText(evidencePath));
        if (document.SchemaVersion == 2 && JsonDefaults.Deserialize<FrozenReplayMaterial>(document.FrozenMaterialJson).PlanJson.Length > 0)
            return FrozenRegionStability.Build(document, evidencePath, databasePath, cancellation);
        return ReadRaw(evidencePath, databasePath, cancellation);
    }
    public static StabilityEvidenceSnapshot ReadRaw(string evidencePath, string databasePath, CancellationToken cancellation = default)
    {
        var file = StabilityEvidenceSnapshot.Freeze(evidencePath);
        var document = JsonDefaults.Deserialize<RegionScenarioEvidenceDocument>(File.ReadAllText(evidencePath));
        if (document.SchemaVersion is not (1 or 2) || Digest(document.ComputationJson) != document.ComputationSha256) throw new InvalidDataException("Region computation schema/hash differs.");
        if (document.SchemaVersion == 2 && (document.FrozenMaterialJson.Length == 0 || Digest(document.FrozenMaterialJson) != document.FrozenMaterialSha256)) throw new InvalidDataException("Region frozen replay material hash differs.");
        var computation = JsonDefaults.Deserialize<RegionScenarioComputation>(document.ComputationJson);
        if (JsonDefaults.Serialize(computation.Identity) != JsonDefaults.Serialize(document.Identity)) throw new InvalidDataException("Region document/computation identity differs.");
        var snapshot = AtlasSnapshot.Read(databasePath);
        var global = new List<string>();
        if (document.CodeContract != RegionScenarioWording.CodeContract || document.Identity.CodeContract != RegionScenarioWording.CodeContract) global.Add("Region code contract differs.");
        if (snapshot.Revision != document.Identity.SnapshotRevision) global.Add(document.SchemaVersion == 1 ? "Historical region evidence differs from current study snapshot; frozen plan/review JSON is absent." : "Historical region evidence differs from current study snapshot; recorded frozen replay material remains retained.");
        var files = ImmutableArray.CreateBuilder<StabilityFileIdentity>(); files.Add(file);
        var inputs = ImmutableArray.CreateBuilder<StabilityProducerInput>(); inputs.Add(new("region-schema1-evidence", file.Path, file.Sha256, File.ReadAllText(evidencePath)));
        var trials = ImmutableArray.CreateBuilder<StabilityTrial>();
        var plan = global.Count == 0 ? ExperimentPlanAdapter.FrozenPlan(snapshot)?.Plan : null;
        if (plan is not null)
        {
            foreach (var item in StudyPlanInventory.Build(plan, snapshot.Recordings.Select(r => r.Asset)))
            {
                var context = plan.Contexts.Single(c => c.Id == item.Trial.ContextId);
                var animal = plan.Animals.Single(a => a.Id == item.Trial.AnimalId);
                Add(item.Recording?.Id, item.Trial.Id, animal.Id, animal.Group, context.Id, context.Phase.ToString(), context.Day,
                    $"release + [{context.ComparisonStartSeconds:R},{context.ComparisonStartSeconds + context.ComparisonDurationSeconds:R}) s", context.Orientation.ToString(), animal.Excluded,
                    global.Concat(item.Holds).ToList());
            }
        }
        var assigned = plan?.Trials.Select(t => t.RecordingId).ToHashSet(StringComparer.Ordinal) ?? [];
        foreach (var recording in computation.Recordings.Where(r => !assigned.Contains(r.VideoId)))
            Add(recording.VideoId, "unassigned/" + recording.VideoId, "Unconfirmed " + recording.VideoId, "", "Unassigned raw recordings", "Unspecified", 0, "raw decoder interval", "image coordinates", false,
                global.Concat(new[] { "No matched frozen confirmed planned trial; raw decoder measurements only." }).ToList());
        if (computation.Recordings.Select(r => r.VideoId).Distinct().Count() != computation.Recordings.Length) throw new InvalidDataException("Duplicate region recording evidence.");
        if (!file.Equals(StabilityEvidenceSnapshot.Freeze(evidencePath))) throw new InvalidDataException("Region evidence changed during load.");
        return new(ResultStability.Build(trials, cancellation) with { Inputs = inputs.DistinctBy(i => (i.Kind, i.Sha256)).ToImmutableArray() }, files.DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToImmutableArray()) { VerifyCurrent = snapshot.IsCurrent };

        void Add(string? videoId, string trialId, string animalId, string group, string contextId, string phase, int day, string window, string orientation, bool excluded, List<string> initialHolds)
        {
            cancellation.ThrowIfCancellationRequested();
            var recording = computation.Recordings.SingleOrDefault(r => r.VideoId == videoId);
            var source = document.Identity.Sources.SingleOrDefault(s => s.VideoId == videoId);
            var row = snapshot.Recordings.SingleOrDefault(r => r.Asset.Id == videoId);
            var holds = initialHolds.Concat(recording?.RecordingHolds ?? []).ToList();
            holds.Add("Producer full decoder measurements or absent supported interval material; confirmed-window biological endpoint is withheld.");
            StabilityTrace? trace = null;
            if (source is not null)
            {
                try
                {
                    var sourceFile = StabilityEvidenceSnapshot.Freeze(source.Path); files.Add(sourceFile);
                    if (sourceFile.Sha256 != source.Sha256) throw new InvalidDataException("Source bytes differ.");
                    var rawJson = snapshot.Rows("runs").LastOrDefault(r => r.Value("video_id") == videoId)?.Value("json");
                    if (rawJson is null || Digest(rawJson) != source.RunMaterialSha256) throw new InvalidDataException("Original run material differs.");
                    var effective = row?.EffectiveRun() ?? throw new InvalidDataException("Frozen coordinate run absent.");
                    CompletedRecordingContract.Validate(row!.RawRun!);
                    var effectiveJson = JsonDefaults.Serialize(effective);
                    inputs.Add(new("corrected-coordinate-run", "", Digest(effectiveJson), effectiveJson));
                    trace = new(source.Path, source.Sha256, "", Digest(effectiveJson), effective.Points[0].Frame, effective.Points[^1].Frame);
                }
                catch (Exception) { holds.Add("Frozen source/run trace is unavailable or mismatched."); }
            }
            foreach (var scenario in computation.Scenarios)
            {
                var outcome = recording?.Set?.Scenarios.SingleOrDefault(s => s.Name == scenario.Name);
                var outcomeHolds = holds.Concat(outcome?.Holds ?? []).ToList();
                if (outcome is null) outcomeHolds.Add("Named scenario is missing/unrun and remains represented.");
                if (outcome?.Held == true) outcomeHolds.Add("Scenario held; adverse outcome retained.");
                var original = recording?.Set?.Original.PlatformSiteSeconds; var changed = outcome?.Endpoints?.PlatformSiteSeconds;
                var scope = new StabilityScope(contextId, phase, day, window, "s", orientation, "platform_site_seconds (raw decoder)");
                var identity = new StabilityIdentity(source?.Sha256 ?? "", source?.RunId ?? "", source?.CorrectionFingerprint ?? "", document.Identity.PlanFingerprint,
                    source?.ProtocolFingerprint ?? "", source?.SettingsHash ?? "", document.Identity.ProfileId, document.SchemaVersion == 2 ? document.Identity.Implementation.CanonicalSha256 : document.CodeContract);
                var scenarioIdentity = identity with { Region = outcome?.MeasurementSettings is { } settings ? Digest(JsonDefaults.Serialize(settings)) : identity.Region, Profile = outcome?.ScenarioFitId ?? identity.Profile };
                TransformedCoordinate? transformedOutlier = outcome is { OutlierFrame: int outlier } && !outcome.Coordinates.IsDefault && outcome.Coordinates.Any(c => c.Frame == outlier) ? outcome.Coordinates.Single(c => c.Frame == outlier) : null;
                var scenarioTrace = trace is null ? null : trace with { MeasurementSettingsJson = outcome?.MeasurementSettings is { } traceSettings ? JsonDefaults.Serialize(traceSettings) : "", OutlierFrame = outcome?.OutlierFrame,
                    TransformEvidenceJson = transformedOutlier is { } transformed ? JsonDefaults.Serialize(transformed) : "" };
                trials.Add(new(trialId + "/" + scenario.Name, trialId, animalId, group, scenario.Name, outcome?.Mode ?? RegionScenarioWording.FrozenCoordinateMode,
                    scenario.Rationale + "; " + (outcome?.Ranges ?? scenario.ComponentRange.ToString()), scope, identity, scenarioIdentity, new(original, null, null), new(changed, null, null),
                    false, excluded, outcomeHolds.ToImmutableArray(), scenarioTrace) { OriginalScope = scope, AnalyzedFrames = row?.RawRun?.Metrics.TotalFrames, PlannedSeconds = row?.RawRun?.Metrics.DurationSeconds, UnknownSeconds = row?.RawRun?.Metrics.UnknownSeconds, ContributionNote = outcome?.OutlierReason ?? "", NumericRanges = DeclaredRanges(scenario) });
            }
        }
    }
    private static string Digest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static ImmutableArray<StabilityDeclaredRange> DeclaredRanges(NamedRegionScenario scenario)
    {
        static string Unit(DisclosedUnit unit) => unit switch { DisclosedUnit.SourcePixels => "source pixels", DisclosedUnit.Normalized => "normalized source fractions", _ => "cm" };
        var image = new StabilityDeclaredRange(scenario.Kind == RegionScenarioKind.CalibrationControlUncertainty ? "image_control" : "geometry_components", Unit(scenario.ComponentRange.Unit), scenario.ComponentRange.Minimum, scenario.ComponentRange.Maximum);
        return scenario.Kind == RegionScenarioKind.CalibrationControlUncertainty ? [image, new("room_control", Unit(scenario.RoomRange.Unit), scenario.RoomRange.Minimum, scenario.RoomRange.Maximum)] : [image];
    }
}
