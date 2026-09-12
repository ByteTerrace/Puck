# Design decisions

These pages explain choices that affect more than one subsystem. Current API
contracts belong in the subsystem guides; proposed changes belong in
[plans](../plans/README.md).

- [Engine design](engine-design.md): document ownership, simulation and
  presentation boundaries, composition and verification policy.
- [World relationships](../architecture/worlds.md): authority, admission,
  transfers and the relationships between worlds.

A decision should state the problem, the chosen approach and its consequences.
When it changes, update both the decision and the affected contract. Preserve
useful reasoning without making historical implementation details mandatory
reading for new contributors.
