# Measured water-plane calibration

This .NET10 library implements the numerical and profile-reuse contracts for
roadmap E4/D4. It is not yet connected to native WPF editing or production
measurements; those steps receive no points from these module tests alone.

## Tested behavior

`WaterPlaneCalibration.Fit` estimates an image-to-room planar homography from
at least4 measured, distinct, noncollinear controls, using normalized linear
least squares. Residuals are reported in cm separately for fitting controls and
independent check landmarks. A fit can be computed while measured use is held:
missing independent checks, reviewer, water-plane confirmation or excessive
prespecified residuals each produce an explicit reason. A4-point exact fit is
not presented as independent accuracy evidence.

Measured points carry camera/resolution, source SHA256/reference frame and
profile revision. Canonical identity covers measured inputs, tolerances,
confirmation and algorithm version. Input order does not change that identity.
`CalibrationProfileFile` saves measured inputs to an immutable new profile file
through a flushed sibling temporary file and non-overwriting rename. Loading
refits inputs and verifies identity/schema/algorithm, instead of trusting a
saved matrix or an edited eligibility flag. Changed inputs require a new revision.

`CameraReuse.Evaluate` compares confirmed stationary registration markers from
another source frame against reference image positions. It reports median pixel
translation and maximum departure from that translation. Camera/resolution
changes, missing/unknown markers, unconfirmed stationary markers, excessive
translation and nontranslation displacement prevent silent profile reuse. It
does not automatically register images or shift old animal coordinates.

Room coordinates remain the measurement reference. `ToTargetAligned` provides a
separate view centered on the measured pool center with the target direction on
positive X; it preserves distances and never changes the room transform.

## Native integration that remains

1. Add camera identity, measured landmarks, check landmarks, error tolerances,
   profile revision and reviewer confirmation to the study's geometry editor.
   Validate source frame against the actual video frame count before saving.
2. Persist immutable profile files in a study-owned calibration catalog and
   store profile identity/revision on asset and analyzed run. Applying a new
   profile must audit the action and invalidate incompatible prior review/run.
3. Collect or locally detect registration markers in each new recording. They
   must be stationary known points; moving mice and water reflections cannot
   stand in for them. A failed check should offer explicit recalibration.
4. Map accepted coordinates only after they are checked against the source
   pool/water-plane domain. Keep physical distances/proximity/regions defined
   consistently in room cm; a homography does not preserve image-circle shape.
   Do not pass calibrated values through the legacy ellipse/diameter transform
   or imply legacy measurements already used this matrix.
5. Freeze calibration identity with the analysis/report snapshot. Provide both
   room and target-aligned display choices. Joint maps require compatible units,
   planned windows, biological identity and a shared room/target reference.
6. Add WPF edit/confirmation/shift/reopen smoke evidence and held-out real
   calibration measurements. The present checks use synthetic measured points.

The numerical model assumes a planar water surface and does not correct lens
distortion or height-dependent parallax. Independent checks must cover the
measured region and use prespecified tolerances. Reviewer input in this module
records an assertion; it is not proof of a human's identity or measurement skill.

## Verification

```
dotnet restore features/camera-calibration/tests/MouseTrackerTabajara.Calibration.Tests.csproj --locked-mode
dotnet test features/camera-calibration/tests/MouseTrackerTabajara.Calibration.Tests.csproj -c Release --no-restore
```

On2026-10-08,16 tests passed,0 failed/0 skipped, with warnings treated as errors.
`powershell -File scripts/test-features.ps1` runs every accepted standalone
feature test project on the primary checkout using locked restores.
Checks cover synthetic projective/inverse mappings, source resolution scale,
independent/control errors, degenerate geometry/horizon, confirmation holds,
canonical identity, room/target orientation, shift and reuse boundaries,
immutable saving and edited-input rejection. This is software regression,
not real-video calibration or position-accuracy validation.
