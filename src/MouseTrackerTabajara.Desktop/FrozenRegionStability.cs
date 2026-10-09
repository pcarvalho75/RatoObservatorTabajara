using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Core;

namespace MouseTrackerTabajara.Desktop;

/// <summary>Conditional trial rows reconstructed from the producer's complete immutable schema2 inputs.</summary>
public static class FrozenRegionStability
{
    public static StabilityEvidenceSnapshot Build(RegionScenarioEvidenceDocument document, string evidencePath, string databasePath, CancellationToken cancellation)
    {
        RegionScenarioEvidenceStore.Authenticate(document);
        var computation = JsonDefaults.Deserialize<RegionScenarioComputation>(document.ComputationJson);
        var material = JsonDefaults.Deserialize<FrozenReplayMaterial>(document.FrozenMaterialJson);
        var reconstructed = RegionProducerReconstruction.Reconstruct(document, cancellation);
        if (material.PlanJson.Length == 0) return RegionStabilityAdapter.ReadRaw(evidencePath, databasePath, cancellation);
        var plan = JsonDefaults.Deserialize<StudyPlan>(material.PlanJson); plan.Validate();
        var assets = material.Recordings.Select(r => JsonDefaults.Deserialize<VideoAsset>(r.VideoJson)).ToArray();
        var inventory = StudyPlanInventory.Build(plan, assets);
        var files = ImmutableArray.CreateBuilder<StabilityFileIdentity>(); var input = StabilityEvidenceSnapshot.Freeze(evidencePath); files.Add(input);
        var inputs = ImmutableArray.CreateBuilder<StabilityProducerInput>(); inputs.Add(new("region-scenario-evidence", input.Path, input.Sha256, File.ReadAllText(evidencePath)));
        var rows = ImmutableArray.CreateBuilder<StabilityTrial>();
        var current = AtlasSnapshot.Read(databasePath); var historical = current.Revision != document.Identity.SnapshotRevision;
        foreach (var item in inventory)
        {
            cancellation.ThrowIfCancellationRequested();
            var context = plan.Contexts.Single(c => c.Id == item.Trial.ContextId); var animal = plan.Animals.Single(a => a.Id == item.Trial.AnimalId);
            var recording = reconstructed.SingleOrDefault(r => r.Asset.Id == item.Recording?.Id);
            var source = document.Identity.Sources.SingleOrDefault(s => s.VideoId == item.Recording?.Id);
            var commonHolds = item.Holds.ToBuilder();
            if (!plan.DesignConfirmed || plan.UnitDesign == StudyUnitDesign.Unknown) commonHolds.Add("Experimental-unit design unconfirmed; biological contrast held.");
            if (!item.Trial.SourceReferenceConfirmed || source is null || item.Trial.ReferenceSourceSha256 != source.Sha256 || item.Trial.ReferenceSettingsHash != source.SettingsHash)
                commonHolds.Add("Frozen per-source common-reference identity is unconfirmed or differs.");
            if (historical) commonHolds.Add("Historical frozen report differs from current study; old input/values retained, current comparison held.");
            StabilityTrace? trace = null;
            if (recording is not null && source is not null)
            {
                try { var file = StabilityEvidenceSnapshot.Freeze(source.Path); files.Add(file); if (file.Sha256 != source.Sha256) throw new InvalidDataException(); }
                catch (Exception) { commonHolds.Add("Current source missing/changed; frozen numerical history retained, source tracing held."); }
                var json = JsonDefaults.Serialize(recording.Effective); var hash = Digest(json); inputs.Add(new("corrected-coordinate-run", "", hash, json));
                if (!commonHolds.Any(h => h.Contains("source missing/changed", StringComparison.Ordinal))) trace = new(source.Path, source.Sha256, "", hash, recording.Effective.Points[0].Frame, recording.Effective.Points[^1].Frame);
            }
            foreach (var scenario in computation.Scenarios)
            {
                var outcome = recording?.Set.Scenarios.SingleOrDefault(s => s.Name == scenario.Name.Trim());
                var original = recording?.Windows.GetValueOrDefault(RegionScenarioWording.OriginalName);
                var variant = recording?.Windows.GetValueOrDefault(scenario.Name.Trim());
                var holds = commonHolds.ToImmutable().ToBuilder();
                var published = computation.Recordings.SingleOrDefault(r => r.VideoId == item.Recording?.Id);
                var publishedOriginal = published?.Windows.SingleOrDefault(w => w.Name == RegionScenarioWording.OriginalName);
                var publishedVariant = published?.Windows.SingleOrDefault(w => w.Name == scenario.Name.Trim());
                if (original is not { Supported: true } || variant is not { Supported: true } || publishedOriginal is not { Supported: true } || publishedVariant is not { Supported: true })
                    holds.Add("Producer confirmed comparison window is unsupported/unrun: " + (variant?.Hold ?? publishedVariant?.Hold ?? "frozen recording unavailable"));
                if (outcome is null || outcome.Held) holds.Add("Scenario measurement failed/held; adverse case retained.");
                if (outcome is not null) holds.AddRange(outcome.Holds);
                if (scenario.Kind == RegionScenarioKind.CalibrationControlUncertainty) holds.Add("Matched original measured-plane trial reference is not supplied by this producer; calibrated transform remains measurement-only.");
                var originalValue = original is null ? new StabilityValue(null, null, null) : RegionProducerReconstruction.SiteValue(original);
                var changedValue = variant is null ? new StabilityValue(null, null, null) : RegionProducerReconstruction.SiteValue(variant);
                var window = WindowKey(context, original);
                var originalScope = new StabilityScope(context.Id, context.Phase.ToString(), context.Day, window, "s", context.Orientation.ToString(), "platform_site_seconds");
                var variantScope = originalScope with { Window = WindowKey(context, variant) };
                var identity = new StabilityIdentity(source?.Sha256 ?? "", source?.RunId ?? "", source?.CorrectionFingerprint ?? "", document.Identity.PlanFingerprint,
                    source?.ProtocolFingerprint ?? "", source?.SettingsHash ?? "", document.Identity.ProfileId, document.Identity.Implementation.CanonicalSha256);
                var scenarioIdentity = identity with { Region = outcome?.MeasurementSettings is { } settings ? Digest(JsonDefaults.Serialize(settings)) : identity.Region, Profile = outcome?.ScenarioFitId ?? identity.Profile };
                var scenarioTrace = trace is null ? null : trace with { MeasurementSettingsJson = outcome?.MeasurementSettings is { } geometry ? JsonDefaults.Serialize(geometry) : "", OutlierFrame = outcome?.OutlierFrame };
                rows.Add(new(item.Trial.Id + "/" + scenario.Name.Trim(), item.Trial.Id, animal.Id, animal.Group, scenario.Name.Trim(), outcome?.Mode ?? RegionScenarioWording.FrozenCoordinateMode,
                    scenario.Rationale + "; " + (outcome?.Ranges ?? scenario.ComponentRange.ToString()), variantScope, identity, scenarioIdentity, originalValue, changedValue,
                    holds.Count == 0, animal.Excluded || item.Recording?.Included == false, holds.Distinct().ToImmutableArray(), scenarioTrace)
                { OriginalScope = originalScope, AnalyzedFrames = recording?.Effective.Points.Count, PlannedSeconds = original?.PlannedSeconds ?? context.ComparisonDurationSeconds, UnknownSeconds = variant?.UnknownSeconds,
                    ContributionNote = outcome?.OutlierReason ?? "", NumericRanges = RegionStabilityAdapter.DeclaredRanges(scenario) });
            }
        }
        // Unassigned imports remain raw rows from the same frozen producer document; no invented planned slot.
        var assigned = plan.Trials.Select(t => t.RecordingId).ToHashSet(StringComparer.Ordinal);
        var raw = RegionStabilityAdapter.ReadRaw(evidencePath, databasePath, cancellation);
        rows.AddRange(raw.Report.Trials.Where(r => r.TrialId.StartsWith("unassigned/", StringComparison.Ordinal) && !assigned.Contains(r.TrialId[11..])));
        inputs.AddRange(raw.Report.Inputs);
        return new(ResultStability.Build(rows, cancellation) with { Inputs = inputs.DistinctBy(i => (i.Kind, i.Sha256)).ToImmutableArray() }, files.DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToImmutableArray())
            { VerifyCurrent = historical ? null : current.IsCurrent };
    }
    public static string WindowKey(PlannedContext context, WindowedTrialMeasurement? measurement) => FormattableString.Invariant($"release + {context.ComparisonStartSeconds:R}s; comparison {(measurement is { Supported: true } ? measurement.PlannedSeconds : context.ComparisonDurationSeconds):R}s; requested {context.RequestedExposureSeconds:R}s");
    private static string Digest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
