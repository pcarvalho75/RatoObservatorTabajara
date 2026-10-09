using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MouseTrackerTabajara.Calibration;

public readonly record struct Point2(double X, double Y)
{
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);
    public double DistanceTo(Point2 other) => Math.Sqrt(Math.Pow(X - other.X, 2) + Math.Pow(Y - other.Y, 2));
}

/// <summary>A measured point on the water plane, in room coordinates in cm.</summary>
public sealed record MeasuredLandmark(string Id, Point2 ImagePixels, Point2 RoomCentimeters);

public sealed record CalibrationRequest(
    string CameraId, int ImageWidth, int ImageHeight, string ReferenceSourceSha256,
    int ReferenceFrame, int Revision, ImmutableArray<MeasuredLandmark> ControlLandmarks,
    ImmutableArray<MeasuredLandmark> IndependentCheckLandmarks,
    double MaximumControlErrorCm, double MaximumIndependentCheckErrorCm,
    bool WaterPlaneAssumptionConfirmed, string ConfirmedBy);

public sealed record LandmarkResidual(string Id, double ErrorCm, bool IndependentCheck);

/// <summary>Immutable homography. The image/room and room/target transforms remain separate.</summary>
public sealed record Matrix3(double A, double B, double C, double D, double E, double F, double G, double H, double I)
{
    public Point2 Map(Point2 point)
    {
        if (!point.IsFinite) throw new ArgumentException("Point must be finite.", nameof(point));
        var z = G * point.X + H * point.Y + I;
        var scale = Math.Abs(G * point.X) + Math.Abs(H * point.Y) + Math.Abs(I);
        if (!double.IsFinite(z) || Math.Abs(z) <= 1e-12 * Math.Max(scale, 1e-300))
            throw new InvalidOperationException("Point is at the projective horizon; no finite plane coordinate exists.");
        var result = new Point2((A * point.X + B * point.Y + C) / z, (D * point.X + E * point.Y + F) / z);
        if (!result.IsFinite) throw new InvalidOperationException("Coordinate transform overflowed.");
        return result;
    }

    public Matrix3 Inverse()
    {
        var determinant = A * (E * I - F * H) - B * (D * I - F * G) + C * (D * H - E * G);
        if (!double.IsFinite(determinant) || determinant == 0) throw new InvalidOperationException("Calibration transform is singular.");
        return new((E * I - F * H) / determinant, (C * H - B * I) / determinant, (B * F - C * E) / determinant,
            (F * G - D * I) / determinant, (A * I - C * G) / determinant, (C * D - A * F) / determinant,
            (D * H - E * G) / determinant, (B * G - A * H) / determinant, (A * E - B * D) / determinant);
    }

    internal double[,] Array() => new[,] { { A, B, C }, { D, E, F }, { G, H, I } };
    internal static Matrix3 Product(Matrix3 left, Matrix3 right)
    {
        var a = left.Array(); var b = right.Array(); var c = new double[3, 3];
        for (var row = 0; row < 3; row++)
            for (var column = 0; column < 3; column++)
                for (var k = 0; k < 3; k++) c[row, column] += a[row, k] * b[k, column];
        return new(c[0, 0], c[0, 1], c[0, 2], c[1, 0], c[1, 1], c[1, 2], c[2, 0], c[2, 1], c[2, 2]);
    }
}

public sealed record CalibrationProfile(string Id, string AlgorithmContract, CalibrationRequest Request,
    Matrix3 ImageToRoom, ImmutableArray<LandmarkResidual> Residuals, ImmutableArray<string> HoldReasons)
{
    public bool EligibleForMeasuredCoordinates => HoldReasons.IsEmpty;
    public Point2 ToRoom(Point2 imagePoint)
    {
        if (!EligibleForMeasuredCoordinates) throw new InvalidOperationException("Calibration is held: " + string.Join(" ", HoldReasons));
        WaterPlaneCalibration.ValidateImagePoint(imagePoint, Request.ImageWidth, Request.ImageHeight);
        return ImageToRoom.Map(imagePoint);
    }
}

public static class WaterPlaneCalibration
{
    public const string AlgorithmContract = "water-plane-normalized-homography-ls-v1";

    public static CalibrationProfile Fit(CalibrationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        // Canonical order makes both the numerical accumulation and profile identity
        // independent of UI landmark insertion order.
        request = request with
        {
            ReferenceSourceSha256 = request.ReferenceSourceSha256.ToLowerInvariant(),
            ControlLandmarks = request.ControlLandmarks.OrderBy(p => p.Id, StringComparer.Ordinal).ToImmutableArray(),
            IndependentCheckLandmarks = request.IndependentCheckLandmarks.OrderBy(p => p.Id, StringComparer.Ordinal).ToImmutableArray()
        };
        var imageNormalization = Normalize(request.ControlLandmarks.Select(p => p.ImagePixels).ToArray());
        var roomNormalization = Normalize(request.ControlLandmarks.Select(p => p.RoomCentimeters).ToArray());
        var normal = new double[8, 8]; var rhs = new double[8];
        foreach (var landmark in request.ControlLandmarks)
        {
            var p = imageNormalization.Map(landmark.ImagePixels); var q = roomNormalization.Map(landmark.RoomCentimeters);
            Accumulate([p.X, p.Y, 1, 0, 0, 0, -q.X * p.X, -q.X * p.Y], q.X, normal, rhs);
            Accumulate([0, 0, 0, p.X, p.Y, 1, -q.Y * p.X, -q.Y * p.Y], q.Y, normal, rhs);
        }
        var h = Solve(normal, rhs);
        var normalized = new Matrix3(h[0], h[1], h[2], h[3], h[4], h[5], h[6], h[7], 1);
        var transform = Matrix3.Product(roomNormalization.Inverse(), Matrix3.Product(normalized, imageNormalization));
        _ = transform.Inverse();
        // A valid water-plane mapping cannot cross a projective horizon within
        // the source image. Extreme or incorrectly placed controls are refused.
        var corners = new[] { new Point2(0, 0), new Point2(request.ImageWidth - 1, 0),
            new Point2(0, request.ImageHeight - 1), new Point2(request.ImageWidth - 1, request.ImageHeight - 1) };
        var signs = corners.Select(p => Math.Sign(transform.G * p.X + transform.H * p.Y + transform.I)).Distinct().ToArray();
        if (signs.Length != 1 || signs[0] == 0) throw new ArgumentException("Calibration crosses a projective horizon inside the source image.", nameof(request));
        foreach (var p in corners) _ = transform.Map(p);
        var residuals = request.ControlLandmarks.Select(p => Residual(p, false, transform))
            .Concat(request.IndependentCheckLandmarks.Select(p => Residual(p, true, transform))).ToImmutableArray();
        var holds = ImmutableArray.CreateBuilder<string>();
        if (!request.WaterPlaneAssumptionConfirmed) holds.Add("Measured landmarks must lie on the water plane; confirm this assumption.");
        if (string.IsNullOrWhiteSpace(request.ConfirmedBy)) holds.Add("Calibration has not been confirmed by a reviewer.");
        if (residuals.Any(p => !p.IndependentCheck && p.ErrorCm > request.MaximumControlErrorCm)) holds.Add("Control landmark fit exceeds the prespecified tolerance.");
        if (request.IndependentCheckLandmarks.IsEmpty) holds.Add("No independent check landmark; a fit alone does not validate calibration accuracy.");
        if (residuals.Any(p => p.IndependentCheck && p.ErrorCm > request.MaximumIndependentCheckErrorCm)) holds.Add("Independent check landmark exceeds the prespecified tolerance.");
        var identityInput = JsonSerializer.Serialize(new { AlgorithmContract, Request = request });
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identityInput))).ToLowerInvariant();
        return new(id, AlgorithmContract, request, transform, residuals, holds.ToImmutable());
    }

    public static Point2 ToTargetAligned(Point2 roomPoint, Point2 poolCenterCm, Point2 targetCm)
    {
        if (!roomPoint.IsFinite || !poolCenterCm.IsFinite || !targetCm.IsFinite) throw new ArgumentException("Room coordinates must be finite.");
        var tx = targetCm.X - poolCenterCm.X; var ty = targetCm.Y - poolCenterCm.Y;
        var radius = Math.Sqrt(tx * tx + ty * ty);
        if (!double.IsFinite(radius) || radius <= 1e-12) throw new ArgumentException("Target direction must differ from the pool center.");
        var x = roomPoint.X - poolCenterCm.X; var y = roomPoint.Y - poolCenterCm.Y;
        return new((x * tx + y * ty) / radius, (-x * ty + y * tx) / radius);
    }

    internal static void ValidateImagePoint(Point2 point, int width, int height)
    {
        if (!point.IsFinite || point.X < 0 || point.Y < 0 || point.X > width - 1 || point.Y > height - 1)
            throw new ArgumentException("Landmark or coordinate is outside the source image.");
    }
    private static void Validate(CalibrationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.CameraId) || request.ImageWidth < 2 || request.ImageHeight < 2 || request.Revision < 1 || request.ReferenceFrame < 0)
            throw new ArgumentException("Supply camera identity, valid source dimensions, revision and zero-based reference frame.");
        if (string.IsNullOrWhiteSpace(request.ReferenceSourceSha256) || request.ReferenceSourceSha256.Length != 64 || !request.ReferenceSourceSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Reference source SHA256 must have64 hexadecimal characters.");
        if (request.ControlLandmarks.IsDefault || request.ControlLandmarks.Length < 4 || request.IndependentCheckLandmarks.IsDefault)
            throw new ArgumentException("Supply at least4 measured controls and an explicit independent-check list.");
        if (!double.IsFinite(request.MaximumControlErrorCm) || request.MaximumControlErrorCm <= 0 || !double.IsFinite(request.MaximumIndependentCheckErrorCm) || request.MaximumIndependentCheckErrorCm <= 0)
            throw new ArgumentException("Prespecified error tolerances must be positive, finite centimeters.");
        var all = request.ControlLandmarks.Concat(request.IndependentCheckLandmarks).ToArray();
        if (all.Any(p => p is null || string.IsNullOrWhiteSpace(p.Id)) || all.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != all.Length)
            throw new ArgumentException("Landmark identities must be nonempty and unique across controls and independent checks.");
        if (all.Select(p => p.ImagePixels).Distinct().Count() != all.Length || all.Select(p => p.RoomCentimeters).Distinct().Count() != all.Length)
            throw new ArgumentException("Independent checks and controls must use distinct measured positions.");
        foreach (var point in all)
        {
            ValidateImagePoint(point.ImagePixels, request.ImageWidth, request.ImageHeight);
            if (!point.RoomCentimeters.IsFinite) throw new ArgumentException("Room-plane measurements must be finite centimeters.");
        }
    }
    private static LandmarkResidual Residual(MeasuredLandmark point, bool check, Matrix3 transform) => new(point.Id, transform.Map(point.ImagePixels).DistanceTo(point.RoomCentimeters), check);
    private static Matrix3 Normalize(Point2[] points)
    {
        var x = points.Average(p => p.X); var y = points.Average(p => p.Y);
        var rms = Math.Sqrt(points.Average(p => Math.Pow(p.X - x, 2) + Math.Pow(p.Y - y, 2)));
        if (!double.IsFinite(rms) || rms < 1e-12) throw new ArgumentException("Measured landmarks have insufficient spatial extent.");
        var scale = Math.Sqrt(2) / rms;
        return new(scale, 0, -x * scale, 0, scale, -y * scale, 0, 0, 1);
    }
    private static void Accumulate(double[] row, double value, double[,] normal, double[] rhs)
    {
        for (var i = 0; i < row.Length; i++)
        {
            rhs[i] += row[i] * value;
            for (var j = 0; j < row.Length; j++) normal[i, j] += row[i] * row[j];
        }
    }
    private static double[] Solve(double[,] matrix, double[] rhs)
    {
        var n = rhs.Length; var globalScale = matrix.Cast<double>().Select(Math.Abs).Max();
        for (var column = 0; column < n; column++)
        {
            var pivot = column;
            for (var row = column + 1; row < n; row++) if (Math.Abs(matrix[row, column]) > Math.Abs(matrix[pivot, column])) pivot = row;
            if (!double.IsFinite(matrix[pivot, column]) || Math.Abs(matrix[pivot, column]) < 1e-10 * globalScale)
                throw new ArgumentException("Landmark geometry is singular or poorly conditioned; add well-spaced noncollinear measured points.");
            for (var j = column; j < n; j++) (matrix[column, j], matrix[pivot, j]) = (matrix[pivot, j], matrix[column, j]);
            (rhs[column], rhs[pivot]) = (rhs[pivot], rhs[column]);
            var divisor = matrix[column, column];
            for (var j = column; j < n; j++) matrix[column, j] /= divisor;
            rhs[column] /= divisor;
            for (var row = 0; row < n; row++)
            {
                if (row == column) continue;
                var factor = matrix[row, column];
                for (var j = column; j < n; j++) matrix[row, j] -= factor * matrix[column, j];
                rhs[row] -= factor * rhs[column];
            }
        }
        if (rhs.Any(v => !double.IsFinite(v))) throw new ArgumentException("Calibration fit is not finite.");
        return rhs;
    }
}
