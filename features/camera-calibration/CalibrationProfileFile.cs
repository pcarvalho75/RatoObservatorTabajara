using System.Text.Json;

namespace MouseTrackerTabajara.Calibration;

/// <summary>Store the measured inputs. Loading refits and revalidates them rather than trusting saved eligibility/matrix fields.</summary>
public static class CalibrationProfileFile
{
    private sealed record SavedProfile(int SchemaVersion, string AlgorithmContract, string ExpectedProfileId, CalibrationRequest Request);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void Save(CalibrationProfile profile, string path)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var refitted = WaterPlaneCalibration.Fit(profile.Request);
        if (refitted.Id != profile.Id || profile.AlgorithmContract != WaterPlaneCalibration.AlgorithmContract || refitted.ImageToRoom != profile.ImageToRoom ||
            !refitted.HoldReasons.SequenceEqual(profile.HoldReasons)) throw new InvalidDataException("Profile does not match its measured inputs.");
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new SavedProfile(1, profile.AlgorithmContract, profile.Id, profile.Request), Options);
        // Immutable profile files are never overwritten. Different revisions get
        // different identities and should use a new filename in the host catalog.
        var temporary = fullPath + ".pending-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(payload); stream.Flush(true); }
            File.Move(temporary, fullPath, overwrite: false);
        }
        finally
        {
            // Exact known sibling, never a computed recursive cleanup target.
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static CalibrationProfile Load(string path)
    {
        var saved = JsonSerializer.Deserialize<SavedProfile>(File.ReadAllBytes(path), Options) ?? throw new InvalidDataException("Calibration file is empty.");
        if (saved.SchemaVersion != 1 || saved.AlgorithmContract != WaterPlaneCalibration.AlgorithmContract)
            throw new InvalidDataException("Unsupported calibration schema or algorithm; do not silently reinterpret it.");
        var profile = WaterPlaneCalibration.Fit(saved.Request);
        if (profile.Id != saved.ExpectedProfileId) throw new InvalidDataException("Calibration inputs changed; save and explicitly confirm a new revision.");
        return profile;
    }
}
