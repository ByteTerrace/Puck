# Red diffuse transport

A horizontal white directional light illuminates a red vertical wall and has
zero Lambert response on the horizontal white floor. The same floor is sampled
0.25 m and 2.5 m from the wall. Its incoming direct-source red must be higher
near the wall; green and blue stay zero. The independent CPU cast must establish
the same ordering from each captured source.

The floor creation is centered at local X=0, then placed at world X=2 with
half-width 2: it spans world X=0 through 4 and contains both receiver stations.
The key points toward world +X, the wall face seen by those receivers.

The companion keeps the visible wall and light but makes its bleed black.
Both incoming red answers must become exactly zero. Temporal, AO, artistic
fill, sky, emission and feedback are disabled to keep this observation specific
to the wall's explicit diffuse response. Geometry and stations use existing
creation and state-driven camera vocabulary. Native source compilation and
the two real backend observations remain owed.
