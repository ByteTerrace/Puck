# Near continuation respects a thin sealed wall

The prepared sealed-room creation is reused: a 25mm box wall encloses a small
white receiver, and a unit-white emitter sits outside at engine x=9.1.
Only Emission is enabled, at High with zero feedback. The companion removes
only the outer and inner partition operands; the sphere, emitter, materials,
camera, tier and source controls stay the same.

This station uses a camera on the receiver's +X/+Z side, aimed at its center.
The center normal is approximately (0.944, 0, 0.329), placing the unchanged
finite emitter inside the sampling hemisphere. The existing ray-zero,
phase-zero cosine direction points through the +X wall toward that emitter.
The source camera for the prepared overview sees the +Y/+Z face instead and
cannot serve this opposite observation.

The wall along that direction lies just beyond the 0.5m Near interval.
Both legs therefore require an actual resolved Near continuation, with the
original direction and exact completed source/bank retained. The sealed ray
has zero emission; removing the wall exposes unit emission. The independent
fixed-first-ray reference must report those values with no unresolved path.
GPU emission must agree within 20% on the exposed source and remain exactly
zero through the wall. Direct and feedback remain zero. A missed finite emitter,
an unattempted Near ray, an unresolved continuation or stale provenance fails.

This is source preparation, not a measured station. The actual pointer and
phase still require qualification through the real World on both backends;
the strict manifest loader cannot establish them. The retained capture and
actual indirect-near work row accompany the same fenced explanation.
The separate [shared-cache sealed canary](../gi-sealed/README.md) covers planar
and curved partitions; this case specifically exercises directional continuation
past the local Near interval. Native shader withholding remains owed.
