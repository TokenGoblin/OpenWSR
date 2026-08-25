# Measurement harnesses

Drop-in xunit fixtures that re-derive the numbers quoted in `docs/verification.md` and in
the code comments that justify a constant. They are not part of the test suite — they
assert nothing and print tables — but every figure in those documents came from one of
them, and a figure nobody can reproduce is a figure nobody can argue with.

The project already keeps `resources/crosscheck/` for the *reference* implementations
(MetPy, ecCodes, Py-ART). These are the other half: they measure OpenWSR's own output, so
they live here rather than there.

## Running one

Copy the file into `tests/OpenWSR.Nexrad.Tests/`, then:

```
OWSR_OUT=<some-file> dotnet test tests/OpenWSR.Nexrad.Tests \
    --filter "FullyQualifiedName~<ClassName>"
```

Delete it again afterwards. They are kept out of the suite deliberately: they take a
committed volume and print numbers, which is a slow way to assert nothing.

| file | derives |
|---|---|
| `DealiasRateMeasurement.cs` | The walk-vs-merge-vs-Py-ART correction-rate table in `docs/verification.md`, and the peak/largest-shift figures in `VelocityDealiasing`'s summary |
| `ShearQualityMeasurement.cs` | The reflectivity-threshold sweep behind `GateQuality.DefaultMinReflectivityDbz`, the 88.8 % figure, and the correlation-coefficient numbers that argue *against* a CC mask |
| `ClutterDiscriminationMeasurement.cs` | The stage table behind `GateQuality.MaskClutter` — what the echo mask and then the clutter mask do to a tornado volume and to a clear-air coastal one |
| `SmoothingMeasurement.cs` | The mean-vs-median comparison in `RotationTracks.Smooth` |

## A note on comparing against an old implementation

Two of these tables have a "before" column. To reproduce it, check the previous version of
the file out over the current one, run the harness, then restore:

```
git show <commit>:src/OpenWSR.Nexrad/Analysis/VelocityDealiasing.cs \
    > src/OpenWSR.Nexrad/Analysis/VelocityDealiasing.cs
# ... run ...
git checkout src/OpenWSR.Nexrad/Analysis/VelocityDealiasing.cs
```

That is also how the regression guards were checked to actually fail on the old code, which
matters: a guard that cannot fail is not one.
