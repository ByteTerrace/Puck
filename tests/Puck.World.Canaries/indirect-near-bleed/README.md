# Small-wall diffuse replacement

The prepared red wall and small red object are retained beside the white floor.
Only explicit Direct transport is enabled, with zero feedback. The selected
Near ray must hit the wall's flat front: its independent captured-direction
reference must report unit red incoming radiance. The actual GPU Direct category
must stay within 20% of unit red; green and blue stay zero. A station whose ray
misses or hits a differently oriented face refuses these observations and must
be corrected from the real capture, never accepted as a black answer.

The companion changes only the source bleed to black through the prepared
no-bleed document. It requires exactly zero GPU and CPU incoming radiance while
retaining the same hit, source identity and fenced-cache contract. This supplies
the physical bleed opposite; the native replacement-vs-addition mutation remains
separate evidence. Current source compilation and both real backend legs remain
owed, including confirmation of the selected receiver and ray phase.
