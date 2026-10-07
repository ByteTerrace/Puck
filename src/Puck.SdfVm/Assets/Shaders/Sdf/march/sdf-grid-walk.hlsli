// Candidate enumeration shared by camera cones, secondary cones, boxes and probe reach balls.
#ifndef MARCH_SDF_GRID_WALK_HLSLI
#define MARCH_SDF_GRID_WALK_HLSLI

bool sdfInstancePassesTileCone(float4 bound, float3 rayOrigin, float3 centerDirection, float chord, float inverseAperture) {
    if (bound.w < 0.0) {
        return false;
    }

    float3 toCenter = (bound.xyz - rayOrigin);
    float alongRay = max(dot(toCenter, centerDirection), 0.0);
    float axisDistance = length(toCenter - (centerDirection * alongRay));

    return (axisDistance <= ((bound.w + (chord * alongRay)) * inverseAperture));
}

struct SdfGridQuery {
    uint shape; // 0 cone, 1 ball, 2 box
    float3 origin;
    float3 direction;
    float3 low;
    float3 high;
    float chord;
    float inverseAperture;
    float inflate;
    float reach;
};

SdfGridQuery sdfGridCone(float3 origin, float3 direction, float chord, float inverseAperture, float inflate, float reach) {
    SdfGridQuery query = (SdfGridQuery)0;
    query.origin = origin;
    query.direction = direction;
    query.chord = chord;
    query.inverseAperture = inverseAperture;
    query.inflate = inflate;
    query.reach = reach;
    return query;
}

SdfGridQuery sdfGridBall(float3 origin, float radius) {
    SdfGridQuery query = (SdfGridQuery)0;
    query.shape = 1u;
    query.origin = origin;
    query.reach = radius;
    query.low = origin - radius;
    query.high = origin + radius;
    return query;
}

SdfGridQuery sdfGridBox(float3 low, float3 high) {
    SdfGridQuery query = (SdfGridQuery)0;
    query.shape = 2u;
    query.low = low;
    query.high = high;
    return query;
}

bool sdfGridQueryContains(SdfGridQuery query, float4 bound) {
    if (bound.w < 0.0) { return false; }
    if (query.shape == 0u) {
        bound.w += query.inflate;
        return sdfInstancePassesTileCone(bound, query.origin, query.direction, query.chord, query.inverseAperture);
    }
    if (query.shape == 1u) {
        return length(bound.xyz - query.origin) <= (bound.w + query.reach);
    }
    float3 delta = max(max(query.low - bound.xyz, bound.xyz - query.high), 0.0);
    return dot(delta, delta) <= bound.w * bound.w;
}

// Each lane owns whole cells, so the striding never omits an entry in a populated cell. A one-lane caller uses
// exactly the same traversal. Repeated candidates in adjacent slabs are intentional: mask insertion is idempotent.
struct SdfGridWalk {
    uint always;
    uint lane;
    uint lanes;
    uint slab;
    uint cell;
    uint cells;
    uint entry;
    uint end;
    int3 low;
    uint3 span;
    float start;
    float finish;
    bool done;
};

SdfGridWalk sdfGridWalkBegin(SdfInstanceGridHeader grid, SdfGridQuery query, uint lane, uint lanes) {
    SdfGridWalk walk = (SdfGridWalk)0;
    walk.always = lane;
    walk.lane = lane;
    walk.lanes = lanes;
    walk.done = !grid.enabled || lanes == 0u || lanes > SDF_MAX_INSTANCES || lane >= lanes;
    if (walk.done) { walk.always = grid.alwaysCount; return walk; }
    if (query.shape != 0u) { return walk; }
    float3 gridMax = grid.origin + float3(grid.dims) * grid.cellSize;
    float3 farCorner = float3(query.direction.x > 0.0 ? gridMax.x : grid.origin.x,
        query.direction.y > 0.0 ? gridMax.y : grid.origin.y,
        query.direction.z > 0.0 ? gridMax.z : grid.origin.z);
    float projection = max(dot(farCorner - query.origin, query.direction), 0.0);
    walk.finish = min((projection + grid.footprintPad + query.inflate) / max(1.0 - query.chord, 0.01),
        query.reach + grid.footprintPad);
    float pad = query.chord * walk.finish + grid.footprintPad + query.inflate;
    float3 low = grid.origin - pad;
    float3 high = gridMax + pad;
    [unroll]
    for (uint axis = 0u; axis < 3u; axis++) {
        if (abs(query.direction[axis]) > 1.0e-8) {
            float a = (low[axis] - query.origin[axis]) / query.direction[axis];
            float b = (high[axis] - query.origin[axis]) / query.direction[axis];
            walk.start = max(walk.start, min(a, b));
            walk.finish = min(walk.finish, max(a, b));
        } else if (query.origin[axis] < low[axis] || query.origin[axis] > high[axis]) {
            walk.done = true;
        }
    }
    walk.done = walk.done || !isfinite(walk.start) || !isfinite(walk.finish) || walk.start > walk.finish;
    return walk;
}

bool sdfGridWalkNext(SdfInstanceGridHeader grid, SdfGridQuery query, inout SdfGridWalk walk, out uint instance) {
    instance = 0u;
    if (walk.always < grid.alwaysCount) {
        instance = sdfGridWordAt(grid, grid.alwaysWord + walk.always);
        walk.always += walk.lanes;
        return instance < grid.instanceCount;
    }
    [loop]
    for (uint advance = 0u; advance < SDF_GRID_MAX_ADVANCES; advance++) {
        if (walk.entry < walk.end) {
            instance = sdfGridWordAt(grid, grid.entryWord + walk.entry++);
            return instance < grid.instanceCount;
        }
        if (walk.cell < walk.cells) {
            uint c = walk.cell;
            walk.cell += walk.lanes;
            uint3 offset = uint3(c % walk.span.x, (c / walk.span.x) % walk.span.y, c / (walk.span.x * walk.span.y));
            uint3 cell = uint3(walk.low) + offset;
            uint index = (cell.z * grid.dims.y + cell.y) * grid.dims.x + cell.x;
            walk.entry = sdfGridWordAt(grid, grid.cellStartWord + index);
            walk.end = sdfGridWordAt(grid, grid.cellStartWord + index + 1u);
            if (walk.entry > walk.end || walk.end > grid.entryCount) { return false; }
            continue;
        }
        if (walk.done) { return false; }
        float3 low;
        float3 high;
        if (query.shape == 0u) {
            if (walk.slab >= SDF_GRID_MAX_SLABS) { return false; }
            // The last slab covers the rest even when a very wide cone spends the slab allowance.
            float end = ((walk.slab + 1u) < SDF_GRID_MAX_SLABS)
                ? min(walk.start + grid.cellSize * SDF_GRID_SLAB_CELLS, walk.finish) : walk.finish;
            float3 a = query.origin + query.direction * walk.start;
            float3 b = query.origin + query.direction * end;
            float radius = query.chord * end + grid.footprintPad + query.inflate;
            low = min(a, b) - radius;
            high = max(a, b) + radius;
            walk.start = end;
            walk.slab++;
            walk.done = end >= walk.finish;
        } else {
            low = query.low - grid.footprintPad;
            high = query.high + grid.footprintPad;
            walk.done = true;
        }
        walk.cell = walk.lane;
        walk.cells = 0u;
        float3 gridMax = grid.origin + float3(grid.dims) * grid.cellSize;
        // An overflowing query footprint conservatively visits the complete finite grid.
        if (!all(isfinite(low)) || !all(isfinite(high))) { low = grid.origin; high = gridMax; }
        if (all(low <= high) && all(high >= grid.origin) && all(low <= gridMax)) {
            int3 last = int3(grid.dims) - 1;
            // Clip in floating point before conversion: a finite remote query can overflow int coordinates.
            float3 lowerCell = clamp(floor((max(low, grid.origin) - grid.origin) * grid.invCellSize), 0.0, float3(last));
            float3 upperCell = clamp(floor((min(high, gridMax) - grid.origin) * grid.invCellSize), 0.0, float3(last));
            if (!all(isfinite(lowerCell)) || !all(isfinite(upperCell))) { return false; }
            walk.low = int3(lowerCell);
            int3 upper = int3(upperCell);
            walk.span = uint3(upper - walk.low) + 1u;
            walk.cells = walk.span.x * walk.span.y * walk.span.z;
        }
    }
    return false;
}
#endif
