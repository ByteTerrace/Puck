#ifndef PUCK_SDF_ELLIPSE_HLSLI
#define PUCK_SDF_ELLIPSE_HLSLI

// F's sign at u for the root below: a ratio at or above one makes it positive without squaring anything past one.
bool sdfEllipseRootAbove(float2 numerator, float2 gap, float u) {
    float2 w = (numerator / (u + gap));

    return ((w.x >= 1.0) || (w.y >= 1.0) || ((dot(w, w) - 1.0) > 0.0));
}

// The signed Euclidean distance from p to the ellipse of semi-axes ab, negative inside: the same root as the CPU's
// FixedEllipsoidProjection, in binary32 and with no interval. With m the least axis, gap_i = (e_i - e_m)(e_i + e_m) and
// w_i(u) = e_i p_i / (u + gap_i), F(u) = sum(w_i^2) - 1 falls strictly for u > 0 and the foot at u is q_i = e_i w_i; u is
// measured from the least axis's pole, so no step subtracts two like quantities. Every length is first scaled by one power
// of two that puts the largest below two, so no square overflows at any radius a document admits. The side comes from the
// implicit test; the bracket runs from e_m p_m to e_m^2 inside and to |e p| outside, halved geometrically while its ends
// differ by more than two to one and arithmetically after, until it has no middle; a point on the minor axis's plane takes
// the pole when the other term's square there is at most one. A result that is not finite answers the scaled circle's
// bound, (|p / e| - 1) min(e).
precise float sdfEllipse2D(float2 p, float2 ab) {
    float2 e = max(ab, 1.0e-30);
    float2 y = abs(p);
    float widest = max(max(e.x, e.y), max(y.x, y.y));
    // 2^-k for widest in [2^(k-1), 2^k): the scaled widest lies in [0.5, 1).
    int exponent = (int)((asuint(widest) >> 23u) & 0xFFu) - 126;
    float scale = ldexp(1.0, -exponent);

    e *= scale;
    y *= scale;

    float2 z = (y / e);

    if ((z.x < 1.0) && (z.y < 1.0) && (dot(z, z) == 1.0)) {
        return 0.0;
    }

    bool outside = ((z.x >= 1.0) || (z.y >= 1.0) || (dot(z, z) > 1.0));
    float least = min(e.x, e.y);
    float2 gap = ((e - least) * (e + least));
    float2 numerator = (e * y);
    float start = max(((gap.x == 0.0) ? numerator.x : 0.0), ((gap.y == 0.0) ? numerator.y : 0.0));
    float pole = (least * least);
    float low = start;
    float high = pole;

    if (outside) {
        low = max(start, pole);
        high = length(numerator);
    } else if (start == 0.0) {
        // Every least axis's coordinate is zero: the root is the pole when the other term's square there is at most one.
        float2 w = float2(((gap.x > 0.0) ? (numerator.x / gap.x) : 0.0), ((gap.y > 0.0) ? (numerator.y / gap.y) : 0.0));
        float sum = dot(w, w);

        if ((w.x < 1.0) && (w.y < 1.0) && (sum <= 1.0)) {
            float2 foot = (e * w);
            float rest = (least * sqrt(max(0.0, (1.0 - sum))));

            if (gap.x == 0.0) { foot.x = rest; } else { foot.y = rest; }
            return (-length(foot - y) / scale);
        }
    }

    [loop]
    for (uint step = 0u; (step < SDF_ELLIPSE_ROOT_STEPS); step++) {
        float middle = (((low > 0.0) && (high > (2.0 * low))) ? (sqrt(low) * sqrt(high)) : (0.5 * (low + high)));

        if ((middle <= low) || (middle >= high)) {
            break;
        }
        if (sdfEllipseRootAbove(numerator, gap, middle)) {
            low = middle;
        } else {
            high = middle;
        }
    }

    float u = (0.5 * (low + high));
    float2 nearest = ((e * numerator) / (u + gap));
    float distance = (length(nearest - y) / scale);

    if (!isfinite(distance)) {
        return ((length(z) - 1.0) * least / scale);
    }

    return (outside ? distance : -distance);
}

#endif
