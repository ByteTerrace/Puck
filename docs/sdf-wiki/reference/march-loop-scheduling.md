# March-loop scheduling

Puck currently traces each output sample in a single compute invocation. This
keeps instruction state local, preserves a straightforward reference path, and
maps consistently to Vulkan and Direct3D 12.

## Alternative schedules

Wavefront tracing, queue compaction, and persistent-thread schedulers can
reduce divergence when rays have very different step counts. They also add
global queues, synchronization, capacity management, and backend-sensitive
execution behavior.

A scheduling change is worthwhile only when profiles show that divergence—not
field evaluation, memory bandwidth, shading, or the beam prepass—is the active
bottleneck. Collect at least:

- step-count distribution by workload;
- active-lane utilization through the march;
- queue and compaction overhead;
- transient-memory requirements; and
- equivalent measurements on both GPU backends.

## Required invariants

Any alternate scheduler must preserve the same ray state, instruction order,
termination criteria, and output ownership as the reference marcher. Queue
overflow must have a deterministic, visible failure policy; silently dropping
rays is not acceptable.

Keep the monolithic path available for parity and regression diagnosis. Avoid
backend-specific subgroup assumptions unless a portable fallback has the same
observable behavior.

Wavefront scheduling remains conditional rather than planned work. See
[the technique index](verdict-index.md) for the reconsideration criteria.
