---
type: llm
weight: 1
---

The response explains that the views stage (`sdfViewsFilter` in `passes/sdf-views-stage.hlsli`) reads and 4x4-filters the occlusion that the surface pass (`sdfMeshAmbient` in `passes/sdf-surface-stage.hlsli`) estimated and wrote into the visibility record.
