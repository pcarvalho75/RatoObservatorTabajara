using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using MouseTrackerTabajara.Core;

namespace MouseTrackerTabajara.Desktop;

public static class RegionScenarioPartial
{
    public static void Render(StackPanel panel, RegionScenarioComputation? result, string? videoId, bool blinded, BlindSecrets secrets, Action<SourceFrameRequest> open)
    {
        panel.Children.Clear();
        if (result is null) return;
        if (!string.IsNullOrWhiteSpace(result.StaleReason))
        {
            panel.Children.Add(Block(blinded ? "Results are stale. Compute the frozen set again." : result.StaleReason));
            return;
        }
        panel.Children.Add(Block(RegionScenarioWording.ParameterPolicy));
        foreach (var recording in result.Recordings)
        {
            var title = SafeRecording(recording.VideoId, blinded);
            panel.Children.Add(Heading(title + (recording.VideoId == videoId ? " · selected" : "")));
            foreach (var hold in recording.RecordingHolds) panel.Children.Add(Block(Show(hold, blinded, secrets)));
            if (recording.Set is null) continue;
            panel.Children.Add(Block(IdentityLine(recording.Set, blinded)));
            panel.Children.Add(EndpointBlock(RegionScenarioWording.OriginalName, "Frozen baseline coordinates.", recording.Set.Original, null, blinded, secrets));
            AddWindow(panel, recording, RegionScenarioWording.OriginalName, blinded, secrets);
            foreach (var scenario in recording.Set.Scenarios)
            {
                panel.Children.Add(EndpointBlock(scenario.Name, scenario.Rationale, scenario.Endpoints, scenario, blinded, secrets));
                AddWindow(panel, recording, scenario.Name, blinded, secrets);
                if (scenario.OutlierFrame is int frame)
                {
                    var button = new Button { Content = "Open source frame", Tag = new SourceFrameRequest(recording.VideoId, scenario.Name, frame), Margin = new Thickness(0, 0, 8, 8), HorizontalAlignment = HorizontalAlignment.Left };
                    button.Click += (_, _) => open((SourceFrameRequest)button.Tag);
                    panel.Children.Add(button);
                }
            }
            foreach (var envelope in recording.Set.Envelopes) panel.Children.Add(Block(blinded ? BlindIdentity.PresentSurface(EnvelopeText(envelope), secrets) : EnvelopeText(envelope)));
            AddComparisonRange(panel, recording, blinded, secrets);
        }
        if (result.Collections.IsDefaultOrEmpty)
            panel.Children.Add(Block("Collection rows are withheld until a confirmed animal, trial, window, and source-reference plan is frozen."));
        foreach (var collection in result.Collections)
        {
            panel.Children.Add(Heading("Collection · " + Show(collection.ContextId, blinded, secrets) + " · " + Show(collection.ScenarioName, blinded, secrets)));
            panel.Children.Add(Block(collection.Summary.Weighting));
            if (!string.IsNullOrWhiteSpace(collection.Summary.Hold)) panel.Children.Add(Block(collection.Summary.Hold));
            foreach (var animal in collection.Summary.Animals)
                panel.Children.Add(Block(AnimalLine(animal, blinded, secrets)));
            foreach (var group in collection.Summary.Groups.Where(group => group.EligibleAnimals > 0))
                panel.Children.Add(Block("Equal-animal mean " + Number(group.Mean) + " s across " + group.EligibleAnimals.ToString(CultureInfo.InvariantCulture) + " eligible animals. " + group.Note));
        }
        if (!string.IsNullOrWhiteSpace(result.EvidencePath))
            panel.Children.Add(Block(blinded ? "Immutable scenario evidence was appended. The path stays hidden during blind review." : "Immutable scenario evidence: " + result.EvidencePath));
    }

    private static string SafeRecording(string videoId, bool blinded) => blinded ? "Recording" : videoId;

    private static string IdentityLine(RegionScenarioSet set, bool blinded)
    {
        var identity = set.Identity;
        if (blinded) return "Original run, source, settings, corrections, plan, protocol, profile, and code revision are frozen. Identifying values stay hidden.";
        return "Run " + identity.OriginalRunId + " · source " + identity.SourceSha256 + " · settings " + identity.SettingsHash
            + " · corrections " + identity.CorrectionFingerprint + " · plan " + identity.PlanRevision.ToString(CultureInfo.InvariantCulture) + " " + identity.PlanFingerprint
            + " · protocol " + identity.ProtocolRevision.ToString(CultureInfo.InvariantCulture) + " · profile " + (string.IsNullOrWhiteSpace(identity.ProfileId) ? "none" : identity.ProfileId)
            + " · " + identity.CodeContract + " · implementation " + identity.ImplementationSha256;
    }

    private static UIElement EndpointBlock(string name, string rationale, TrialEndpointSet? endpoints, RegionScenarioOutcome? scenario, bool blinded, BlindSecrets secrets)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
        stack.Children.Add(Heading(Show(name, blinded, secrets)));
        stack.Children.Add(Block("Rationale: " + (blinded ? BlindIdentity.ProjectAuthored(rationale, secrets) : rationale)));
        if (scenario is not null)
        {
            stack.Children.Add(Block(scenario.Mode));
            stack.Children.Add(Block("Documented range: " + scenario.Ranges));
            foreach (var hold in scenario.Holds) stack.Children.Add(Block("Hold: " + Show(hold, blinded, secrets)));
            if (scenario.OutlierFrame is int frame) stack.Children.Add(Block("Linked frame " + frame.ToString(CultureInfo.InvariantCulture) + ". " + (blinded ? "Frame link recorded." : scenario.OutlierReason)));
            if (!string.IsNullOrWhiteSpace(scenario.ScenarioFitId) && !blinded) stack.Children.Add(Block("Baseline profile " + scenario.BaselineProfileId + " revision " + scenario.BaselineProfileRevision.ToString(CultureInfo.InvariantCulture) + " · scenario fit " + scenario.ScenarioFitId));
        }
        if (endpoints is null) stack.Children.Add(Block("Endpoint values held."));
        else
        {
            stack.Children.Add(Block("Platform site " + Number(endpoints.PlatformSiteSeconds) + " s · entries " + (endpoints.PlatformSiteEntries?.ToString(CultureInfo.InvariantCulture) ?? "held")
                + " · target observed " + Number(endpoints.TargetObservedPercent) + " percent · distance " + Number(endpoints.Distance) + " " + endpoints.DistanceUnit));
            stack.Children.Add(Block("First visit " + (endpoints.FirstVisitWithheld ? "withheld" : Number(endpoints.SiteVisitTime)) + " s · history " + endpoints.SiteVisitStatus
                + " · lower " + Number(endpoints.FirstVisitLowerSeconds) + " · upper " + Number(endpoints.FirstVisitUpperSeconds)));
            stack.Children.Add(Block(endpoints.MissingPositionLabel + " " + Bounds(endpoints.TargetMissingPositionBounds)));
            if (scenario?.DeltaFromOriginal is { } delta)
                stack.Children.Add(Block("Delta from original: site " + Number(delta.PlatformSiteSeconds) + " s · entries " + (delta.PlatformSiteEntries?.ToString(CultureInfo.InvariantCulture) ?? "held")
                    + " · distance " + Number(delta.Distance) + " " + delta.DistanceUnit + " · first visit " + Number(delta.FirstVisitSeconds) + " s. " + delta.FirstVisitNote));
        }
        return stack;
    }

    private static void AddWindow(StackPanel panel, RecordingScenarioComputation recording, string name, bool blinded, BlindSecrets secrets)
    {
        var windowed = recording.Windows.FirstOrDefault(item => item.Name == name);
        if (windowed is not null) panel.Children.Add(Block(Show(WindowText(windowed), blinded, secrets)));
    }

    private static void AddComparisonRange(StackPanel panel, RecordingScenarioComputation recording, bool blinded, BlindSecrets secrets)
    {
        var supported = recording.Windows.Where(item => item.Supported && item.Comparison?.PlatformSiteSeconds is not null).ToArray();
        if (supported.Length == 0) return;
        var values = supported.Select(item => item.Comparison!.PlatformSiteSeconds!.Value).ToArray();
        var held = recording.Windows.Where(item => item.Comparison?.PlatformSiteSeconds is null).Select(item => item.Name);
        var text = "Comparison-window platform site " + values.Min().ToString("0.000", CultureInfo.InvariantCulture) + " to " + values.Max().ToString("0.000", CultureInfo.InvariantCulture)
            + " s. Contributing: " + string.Join(", ", supported.Select(item => item.Name))
            + ". Held outside this numeric range: " + string.Join(", ", held) + ". " + RegionScenarioWording.EnvelopeLabel
            + " This range is the release-relative comparison window, not the full-decoder envelope.";
        panel.Children.Add(Block(Show(text, blinded, secrets)));
    }

    private static string WindowText(WindowedTrialMeasurement windowed)
    {
        var raw = " Raw decoder site " + Number(windowed.FullRun?.PlatformSiteSeconds) + " s stays separate and is not this trial value.";
        if (!windowed.Supported || windowed.Comparison is null)
            return windowed.Name + " comparison window held. " + windowed.Hold + raw;
        return windowed.Name + " comparison window site " + Number(windowed.Comparison.PlatformSiteSeconds) + " s. Planned " + Number(windowed.PlannedSeconds)
            + " s, observed " + Number(windowed.ObservedSeconds) + " s, unknown " + Number(windowed.UnknownSeconds) + " s. " + windowed.Scope
            + (string.IsNullOrWhiteSpace(windowed.DistanceNote) ? "" : " " + windowed.DistanceNote) + raw;
    }

    private static string EnvelopeText(ScenarioEnvelope envelope)
    {
        var numeric = envelope.Numeric ? Number(envelope.Minimum) + " to " + Number(envelope.Maximum) + " " + envelope.Unit : "numeric envelope held";
        return envelope.Endpoint + " " + numeric + ". Contributing: " + string.Join(", ", envelope.Contributing) + ". Held outside this numeric envelope: " + string.Join(", ", envelope.HeldOutside) + ". " + envelope.Label;
    }

    private static string AnimalLine(AnimalWeightedRow animal, bool blinded, BlindSecrets secrets)
    {
        var label = blinded ? "Animal" : animal.AnimalId;
        var group = blinded ? "" : " group " + animal.Group;
        return label + group + " weight " + animal.Weight.ToString("0", CultureInfo.InvariantCulture) + " " + animal.Status
            + " mean " + Number(animal.AnimalMean) + " s. " + (blinded ? BlindIdentity.ProjectAuthored(animal.Detail, secrets) : animal.Detail);
    }

    private static string Show(string text, bool blinded, BlindSecrets secrets) => blinded ? BlindIdentity.PresentSurface(text, secrets) : text;
    private static string Number(double? value) => value?.ToString("0.000", CultureInfo.InvariantCulture) ?? "held";
    private static string Bounds(PercentBounds? bounds) => bounds is null ? "held" : bounds.Lower.ToString("0.000", CultureInfo.InvariantCulture) + " to " + bounds.Upper.ToString("0.000", CultureInfo.InvariantCulture) + " percent";
    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 2) };
    private static TextBlock Block(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
}
