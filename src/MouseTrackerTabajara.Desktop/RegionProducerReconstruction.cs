using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Calibration;
using MouseTrackerTabajara.Core;

namespace MouseTrackerTabajara.Desktop;

public sealed record ReconstructedRegionRecording(VideoAsset Asset, AnalysisRun Effective, RegionScenarioSet Set, ImmutableDictionary<string, WindowedTrialMeasurement> Windows);

/// <summary>Recomputes declared numerical material through the actual producer APIs. It does not grant a biological/review license.</summary>
public static class RegionProducerReconstruction
{
    public static ImmutableArray<ReconstructedRegionRecording> Reconstruct(RegionScenarioEvidenceDocument document, CancellationToken cancellation = default)
    {
        RegionScenarioEvidenceStore.Authenticate(document);
        if (document.SchemaVersion != 2 || Digest(document.FrozenMaterialJson) != document.FrozenMaterialSha256 || Digest(document.ComputationJson) != document.ComputationSha256)
            throw new InvalidDataException("Authenticated reconstruction requires intact schema2 frozen producer material.");
        var material = JsonDefaults.Deserialize<FrozenReplayMaterial>(document.FrozenMaterialJson);
        var computation = JsonDefaults.Deserialize<RegionScenarioComputation>(document.ComputationJson);
        if (material.SchemaVersion != 1 || document.CodeContract != RegionScenarioWording.CodeContract || document.Identity.CodeContract != RegionScenarioWording.CodeContract) throw new InvalidDataException("Frozen producer material/code contract differs.");
        if (JsonDefaults.Serialize(computation.Replay) != JsonDefaults.Serialize(material) || JsonDefaults.Serialize(computation.Identity) != JsonDefaults.Serialize(document.Identity)) throw new InvalidDataException("Producer computation/frozen input identity differs.");
        var profile = LoadProfile(material); var plan = material.PlanJson.Length == 0 ? null : JsonDefaults.Deserialize<StudyPlan>(material.PlanJson);
        plan?.Validate();
        if (plan is not null && (Digest(material.PlanJson) != material.PlanFingerprint || material.PlanHistory.LastOrDefault() != material.PlanJson || material.PlanFingerprint != document.Identity.PlanFingerprint || material.PlanRevision != document.Identity.PlanRevision)) throw new InvalidDataException("Frozen expected-plan history/identity differs.");
        if ((profile?.Id ?? "") != document.Identity.ProfileId || (profile?.Request.Revision ?? 0) != document.Identity.ProfileRevision) throw new InvalidDataException("Frozen measured profile identity differs.");
        if (material.Recordings.Select(r => r.VideoId).Distinct().Count() != material.Recordings.Length) throw new InvalidDataException("Duplicate frozen producer recording.");
        var result = ImmutableArray.CreateBuilder<ReconstructedRegionRecording>();
        foreach (var frozen in material.Recordings)
        {
            cancellation.ThrowIfCancellationRequested();
            var asset = JsonDefaults.Deserialize<VideoAsset>(frozen.VideoJson);
            if (asset.Id != frozen.VideoId) throw new InvalidDataException("Frozen producer asset identity differs.");
            if (frozen.MeasuredRunJson.Length == 0) continue;
            var identity = document.Identity.Sources.Single(s => s.VideoId == asset.Id);
            if (Digest(frozen.MeasuredRunJson) != identity.RunMaterialSha256 || frozen.RunHistory.LastOrDefault() != frozen.MeasuredRunJson) throw new InvalidDataException("Frozen automatic run receipt differs.");
            var raw = JsonDefaults.Deserialize<AnalysisRun>(frozen.MeasuredRunJson); CompletedRecordingContract.Validate(raw);
            if (raw.VideoId != asset.Id || raw.SourceSha256 != identity.Sha256 || asset.SourceSha256 != identity.Sha256 || raw.Metadata != asset.Metadata || MouseTrackerTabajara.Storage.StudyStore.SettingsHash(raw.Settings) != identity.SettingsHash)
                throw new InvalidDataException("Frozen source/run/settings identity differs.");
            if (MaterialFingerprint.Hash(frozen.Corrections) != identity.CorrectionFingerprint || MaterialFingerprint.Hash(frozen.Protocols.TakeLast(1)) != identity.ProtocolFingerprint) throw new InvalidDataException("Frozen correction/protocol receipt differs.");
            var points = MetricCalculator.ApplyCorrections(raw, frozen.Corrections.Select(JsonDefaults.Deserialize<ManualCorrection>));
            var effective = raw with { Points = points, Metrics = MetricCalculator.Calculate(points, raw.Metadata, raw.Settings) };
            var header = RegionScenarioBaseline.Header(document.Identity, identity);
            var set = RegionScenarioMath.Evaluate(points, raw.Metadata, raw.Settings, computation.Scenarios, header,
                scenario => CalibrationScenarioMath.Evaluate(profile, scenario, points, raw.Metadata, raw.SourceSha256));
            var protocol = frozen.Protocols.LastOrDefault() is { } protocolJson ? JsonDefaults.Deserialize<ProtocolWindow>(protocolJson) : null;
            var slot = plan?.Trials.SingleOrDefault(t => t.RecordingId == asset.Id);
            var context = slot is null ? null : plan!.Contexts.Single(c => c.Id == slot.ContextId);
            var effectiveEvents = EffectiveEvents(frozen);
            var window = ReleasedComparisonWindow.Resolve(protocol, context, raw.Metadata, effectiveEvents.Select(e => e.Event).ToArray(), asset.Phase, CurrentInspection(frozen, asset, raw, effectiveEvents));
            var windows = ImmutableDictionary.CreateBuilder<string, WindowedTrialMeasurement>(StringComparer.Ordinal);
            windows.Add(RegionScenarioWording.OriginalName, RegionScenarioComparison.Measure(points, raw.Metadata, raw.Settings, window, RegionScenarioWording.OriginalName));
            foreach (var outcome in set.Scenarios)
            {
                if (outcome.Kind == RegionScenarioKind.CalibrationControlUncertainty)
                {
                    var named = computation.Scenarios.Single(s => s.Name.Trim() == outcome.Name);
                    var measured = CalibrationScenarioMath.Evaluate(profile, named, points, raw.Metadata, raw.SourceSha256);
                    windows.Add(outcome.Name, CalibrationScenarioMath.MeasureWindow(named, measured, window, raw.Metadata.Fps));
                }
                else if (!outcome.Held && outcome.MeasurementSettings is { } settings)
                    windows.Add(outcome.Name, RegionScenarioComparison.Measure(points, raw.Metadata, settings, window, outcome.Name));
                else windows.Add(outcome.Name, WindowedTrialMeasurement.Held(outcome.Name, outcome.Holds.FirstOrDefault() ?? "Scenario measurement is held."));
            }
            var declared = computation.Recordings.Single(r => r.VideoId == asset.Id);
            if (declared.Set is null || JsonDefaults.Serialize(declared.Set) != JsonDefaults.Serialize(set)) throw new InvalidDataException("Producer endpoint/region/transform scalar material differs from reconstructed inputs.");
            foreach (var declaredWindow in declared.Windows)
                if (!windows.TryGetValue(declaredWindow.Name, out var expected) || JsonDefaults.Serialize(expected) != JsonDefaults.Serialize(declaredWindow)) throw new InvalidDataException("Producer comparison-window scalar/bounds differ from reconstructed inputs.");
            result.Add(new(asset, effective, set, windows.ToImmutable()));
        }
        return result.ToImmutable();
    }

    public static void VerifySavedScalars(StabilityReport saved, CancellationToken cancellation = default)
    {
        var inputs = saved.Inputs.Where(i => i.Kind.StartsWith("region-", StringComparison.Ordinal)).ToArray();
        if (inputs.Length == 0) throw new InvalidDataException("No frozen region producer input is supplied; aggregation-only replay cannot authenticate endpoint scalars.");
        var verified = new HashSet<string>(StringComparer.Ordinal);
        var expectedCases = new Dictionary<string, ExpectedRegionCase>(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            if (Digest(input.Json) != input.Sha256) throw new InvalidDataException("Producer input receipt differs.");
            var document = JsonDefaults.Deserialize<RegionScenarioEvidenceDocument>(input.Json);
            var reconstructed = Reconstruct(document, cancellation);
            var frozenMaterial = JsonDefaults.Deserialize<FrozenReplayMaterial>(document.FrozenMaterialJson);
            var frozenPlan = frozenMaterial.PlanJson.Length == 0 ? null : JsonDefaults.Deserialize<StudyPlan>(frozenMaterial.PlanJson);
            var frozenComputation = JsonDefaults.Deserialize<RegionScenarioComputation>(document.ComputationJson);
            AddExpectedCases(expectedCases, document, frozenMaterial, frozenPlan, frozenComputation, reconstructed);
            foreach (var trial in saved.Trials)
            {
                var recording = reconstructed.SingleOrDefault(r => r.Asset.SourceSha256 == trial.BaselineIdentity.Source && r.Effective.Id == trial.BaselineIdentity.Run);
                if (recording is null) continue;
                var scenario = recording.Set.Scenarios.SingleOrDefault(s => s.Name == trial.Scenario); if (scenario is null) continue;
                var source = document.Identity.Sources.Single(s => s.VideoId == recording.Asset.Id);
                var expectedIdentity = new StabilityIdentity(source.Sha256, source.RunId, source.CorrectionFingerprint, document.Identity.PlanFingerprint,
                    source.ProtocolFingerprint, source.SettingsHash, document.Identity.ProfileId, document.Identity.Implementation.CanonicalSha256);
                var expectedScenarioIdentity = expectedIdentity with { Region = scenario.MeasurementSettings is { } settings ? Digest(JsonDefaults.Serialize(settings)) : expectedIdentity.Region, Profile = scenario.ScenarioFitId };
                if (trial.BaselineIdentity != expectedIdentity || trial.ScenarioIdentity != expectedScenarioIdentity) throw new InvalidDataException("Saved source/run/correction/plan/window/region/profile/code contribution identity differs from frozen producer material.");
                if (trial.Scope.Unit != "s") throw new InvalidDataException("Saved platform occupancy endpoint unit differs from the producer's seconds.");
                var named = frozenComputation.Scenarios.Single(s => s.Name.Trim() == trial.Scenario);
                if (!trial.NumericRanges.SequenceEqual(RegionStabilityAdapter.DeclaredRanges(named))) throw new InvalidDataException("Saved prespecified ranges differ from frozen producer inputs.");
                StabilityValue original, changed;
                if (trial.Scope.Endpoint == "platform_site_seconds (raw decoder)")
                {
                    if (trial.Supported) throw new InvalidDataException("A raw decoder descriptor cannot be promoted into supported biological trial material.");
                    original = new(recording.Set.Original.PlatformSiteSeconds, null, null); changed = new(scenario.Endpoints?.PlatformSiteSeconds, null, null);
                }
                else if (trial.Scope.Endpoint == "platform_site_seconds")
                {
                    original = SiteValue(recording.Windows[RegionScenarioWording.OriginalName]); changed = SiteValue(recording.Windows[trial.Scenario]);
                    if (frozenPlan is null) throw new InvalidDataException("Frozen expected trial plan absent.");
                    var slot = frozenPlan.Trials.Single(t => t.Id == trial.TrialId); var context = frozenPlan.Contexts.Single(c => c.Id == slot.ContextId); var animal = frozenPlan.Animals.Single(a => a.Id == slot.AnimalId);
                    var originalScope = new StabilityScope(context.Id, context.Phase.ToString(), context.Day, FrozenRegionStability.WindowKey(context, recording.Windows[RegionScenarioWording.OriginalName]), "s", context.Orientation.ToString(), "platform_site_seconds");
                    var variantScope = originalScope with { Window = FrozenRegionStability.WindowKey(context, recording.Windows[trial.Scenario]) };
                    if (trial.OriginalScope != originalScope || trial.Scope != variantScope || slot.RecordingId != recording.Asset.Id || trial.AnimalId != animal.Id || trial.Group != animal.Group || trial.CaseId != slot.Id + "/" + named.Name.Trim())
                        throw new InvalidDataException("Saved matched trial/animal/allocation/phase/day/window/unit/orientation scope differs from frozen plan.");
                    if (trial.Supported)
                    {
                        var contract = StudyPlanInventory.Build(frozenPlan, frozenMaterial.Recordings.Select(r => JsonDefaults.Deserialize<VideoAsset>(r.VideoJson))).Single(t => t.Trial.Id == slot.Id);
                        var published = frozenComputation.Recordings.Single(r => r.VideoId == recording.Asset.Id);
                        if (!frozenPlan.DesignConfirmed || frozenPlan.UnitDesign == StudyUnitDesign.Unknown || !contract.Holds.IsEmpty || !slot.SourceReferenceConfirmed || slot.ReferenceSourceSha256 != source.Sha256 || slot.ReferenceSettingsHash != source.SettingsHash ||
                            !recording.Windows["original"].Supported || !recording.Windows[trial.Scenario].Supported || !published.Windows.Any(w => w.Name == "original" && w.Supported) || !published.Windows.Any(w => w.Name == trial.Scenario && w.Supported) || scenario.Held || named.Kind == RegionScenarioKind.CalibrationControlUncertainty)
                            throw new InvalidDataException("Saved supported flag is not authorized by the frozen producer/reference/review/window contract.");
                    }
                }
                else throw new InvalidDataException("Endpoint authentication for this supplied producer schema is unsupported.");
                if (trial.Original != original || trial.Changed != changed) throw new InvalidDataException("Saved trial scalar/bounds were altered independently of frozen producer material.");
                verified.Add(trial.CaseId);
            }
        }
        VerifyCompleteCaseSet(saved.Trials, expectedCases);
        if (saved.Trials.Any(t => t.Supported && !verified.Contains(t.CaseId))) throw new InvalidDataException("A supported trial lacks authenticated frozen producer reconstruction.");
        if (saved.Trials.Any(t => !verified.Contains(t.CaseId) && (t.Original.Value.HasValue || t.Original.Lower.HasValue || t.Original.Upper.HasValue || t.Changed.Value.HasValue || t.Changed.Lower.HasValue || t.Changed.Upper.HasValue)))
            throw new InvalidDataException("A held/missing trial contains numerical values without frozen producer reconstruction.");
    }
    private sealed record ExpectedRegionCase(string TrialId, string AnimalId, string Group, string Scenario, bool Excluded, StabilityIdentity Identity, StabilityScope Scope, StabilityScope OriginalScope);
    private static void AddExpectedCases(Dictionary<string, ExpectedRegionCase> expected, RegionScenarioEvidenceDocument document, FrozenReplayMaterial material, StudyPlan? plan, RegionScenarioComputation computation, ImmutableArray<ReconstructedRegionRecording> reconstructed)
    {
        var assets = material.Recordings.Select(r => JsonDefaults.Deserialize<VideoAsset>(r.VideoJson)).ToDictionary(a => a.Id, StringComparer.Ordinal);
        var observed = computation.Recordings.Select(r => r.VideoId).ToHashSet(StringComparer.Ordinal);
        if (observed.Count != computation.Recordings.Length || !observed.SetEquals(assets.Keys)) throw new InvalidDataException("Frozen producer recording inventory is incomplete or duplicated.");
        var names = computation.Scenarios.Select(s => s.Name.Trim()).ToArray();
        if (names.Length == 0 || names.Any(string.IsNullOrWhiteSpace) || names.Distinct(StringComparer.Ordinal).Count() != names.Length) throw new InvalidDataException("Frozen producer scenario inventory is invalid or duplicated.");
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        if (plan is not null)
        {
            // The same accepted inventory semantics used by the adapter include every planned missing/excluded slot.
            foreach (var item in StudyPlanInventory.Build(plan, assets.Values))
            {
                if (item.Trial.RecordingId is { } id) assigned.Add(id);
                var animal = plan.Animals.Single(a => a.Id == item.Trial.AnimalId);
                var context = plan.Contexts.Single(c => c.Id == item.Trial.ContextId);
                var recording = reconstructed.SingleOrDefault(r => r.Asset.Id == item.Recording?.Id);
                var originalScope = new StabilityScope(context.Id, context.Phase.ToString(), context.Day, FrozenRegionStability.WindowKey(context, recording?.Windows.GetValueOrDefault("original")), "s", context.Orientation.ToString(), "platform_site_seconds");
                foreach (var name in names)
                {
                    var scope = originalScope with { Window = FrozenRegionStability.WindowKey(context, recording?.Windows.GetValueOrDefault(name)) };
                    Add(item.Trial.Id + "/" + name, item.Trial.Id, animal.Id, animal.Group, name, animal.Excluded || item.Recording?.Included == false, item.Recording?.Id, scope, originalScope);
                }
            }
        }
        foreach (var asset in assets.Values.Where(a => !assigned.Contains(a.Id)))
            foreach (var name in names)
            {
                var scope = new StabilityScope("Unassigned raw recordings", "Unspecified", 0, "raw decoder interval", "s", "image coordinates", "platform_site_seconds (raw decoder)");
                Add("unassigned/" + asset.Id + "/" + name, "unassigned/" + asset.Id, "Unconfirmed " + asset.Id, "", name, false, asset.Id, scope, scope);
            }

        void Add(string caseId, string trialId, string animalId, string group, string scenario, bool excluded, string? videoId, StabilityScope scope, StabilityScope originalScope)
        {
            var source = document.Identity.Sources.SingleOrDefault(s => s.VideoId == videoId);
            var identity = new StabilityIdentity(source?.Sha256 ?? "", source?.RunId ?? "", source?.CorrectionFingerprint ?? "", document.Identity.PlanFingerprint,
                source?.ProtocolFingerprint ?? "", source?.SettingsHash ?? "", document.Identity.ProfileId, document.Identity.Implementation.CanonicalSha256);
            var entry = new ExpectedRegionCase(trialId, animalId, group, scenario, excluded, identity, scope, originalScope);
            if (expected.TryGetValue(caseId, out var prior) && prior != entry) throw new InvalidDataException("Multiple frozen region inputs disagree on the identity of a planned case.");
            expected[caseId] = entry; // Identical embedded inputs may repeat; the case itself must occur once.
        }
    }
    private static void VerifyCompleteCaseSet(ImmutableArray<StabilityTrial> trials, Dictionary<string, ExpectedRegionCase> expected)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in trials)
        {
            if (!seen.Add(row.CaseId)) throw new InvalidDataException("Saved region case inventory contains a duplicate case identity.");
            if (!expected.TryGetValue(row.CaseId, out var entry)) throw new InvalidDataException("Saved region case inventory contains an extra/unrecognized case.");
            if (row.TrialId != entry.TrialId || row.AnimalId != entry.AnimalId || row.Group != entry.Group || row.Scenario != entry.Scenario || row.Excluded != entry.Excluded || row.BaselineIdentity != entry.Identity || row.Scope != entry.Scope || row.OriginalScope != entry.OriginalScope)
                throw new InvalidDataException("Saved region case identity differs from authenticated frozen trial/scenario inventory.");
        }
        if (!seen.SetEquals(expected.Keys)) throw new InvalidDataException("Saved region case inventory is incomplete; all frozen planned/raw/held/missing/excluded trial × scenario cases are required.");
    }
    public static StabilityValue SiteValue(WindowedTrialMeasurement measurement)
    {
        var seconds = measurement.Supported ? measurement.Comparison?.PlatformSiteSeconds : null;
        return seconds is null ? new(null, null, null) : new(seconds, seconds, Math.Min(measurement.PlannedSeconds, seconds.Value + measurement.UnknownSeconds));
    }
    private static ImmutableArray<(string Json, BiologicalEvent Event)> EffectiveEvents(FrozenReplayRecording frozen) => frozen.Events
        .Select(json => (Json: json, Event: JsonDefaults.Deserialize<BiologicalEvent>(json))).GroupBy(e => e.Event.Id, StringComparer.Ordinal)
        .Select(group => group.OrderBy(e => e.Event.Revision).Last()).OrderBy(e => e.Event.Id, StringComparer.Ordinal).ToImmutableArray();
    private static bool CurrentInspection(FrozenReplayRecording frozen, VideoAsset asset, AnalysisRun raw, ImmutableArray<(string Json, BiologicalEvent Event)> events)
    {
        try
        {
            var latest = frozen.Reviews.LastOrDefault(); if (latest is null) return false;
            var contract = JsonDefaults.Deserialize<ReviewContract>(latest);
            if (events.Any(e => e.Event.VideoId != asset.Id || e.Event.Frame < 0 || e.Event.Frame >= raw.Metadata.FrameCount || e.Event.Revision < 1 || string.IsNullOrWhiteSpace(e.Event.Id) ||
                !Enum.IsDefined(e.Event.Kind) || !double.IsFinite(e.Event.TimeSeconds) || Math.Abs(e.Event.TimeSeconds - e.Event.Frame / raw.Metadata.Fps) > .001 || string.IsNullOrWhiteSpace(e.Event.Reviewer) || string.IsNullOrWhiteSpace(e.Event.Reason))) return false;
            return raw.Settings.ArenaConfirmed && contract.RunId == raw.Id && contract.VideoId == asset.Id && contract.SourceSha256 == asset.SourceSha256 &&
                contract.SettingsHash == MouseTrackerTabajara.Storage.StudyStore.SettingsHash(raw.Settings) && contract.SettingsHash == MouseTrackerTabajara.Storage.StudyStore.SettingsHash(asset.Settings) &&
                contract.CorrectionFingerprint == MaterialFingerprint.Hash(frozen.Corrections) && contract.EventFingerprint == MaterialFingerprint.Hash(events.Select(e => e.Json)) &&
                contract.ProtocolFingerprint == MaterialFingerprint.Hash(frozen.Protocols.TakeLast(1));
        }
        catch (Exception) { return false; }
    }
    private static CalibrationProfile? LoadProfile(FrozenReplayMaterial material)
    {
        if (material.ProfileJson.Length == 0) return null;
        if (Digest(material.ProfileJson) != material.ProfileSha256) throw new InvalidDataException("Frozen measured profile receipt differs.");
        var path = Path.Combine(Path.GetTempPath(), "tabajara-stability-profile-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { var bytes = Encoding.UTF8.GetBytes(material.ProfileJson); stream.Write(bytes); }
            return CalibrationProfileFile.Load(path);
        }
        finally { if (File.Exists(path)) File.Delete(path); } // exact GUID path owned by this read-only reconstruction operation
    }
    private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
