#ifndef PUCK_SDF_ELLIPSE_HLSLI
#define PUCK_SDF_ELLIPSE_HLSLI

float sdfEllipse2D(float2 p, float2 ab) {
    float largestRadius = max(ab.x, ab.y);
    if (min(ab.x, ab.y) > 0.999 * largestRadius && length(p) > 4.0 * largestRadius) {
        // Outside a nearly circular ellipse, solve the monotone projection equation in normalized units.
        // The cubic expression below divides by b^2-a^2 and becomes unstable at distant probes in this regime.
        float2 radius = ab / largestRadius, position = abs(p) / largestRadius;
        float2 r2 = radius * radius, weighted = radius * position;
        float norm = length(weighted);
        float low = max(norm - 1.0, 0.0), high = max(norm - min(r2.x, r2.y), 0.0);
        float parameter = (low + high) * 0.5;
        [unroll] for (uint step = 0u; step < 4u; step++) {
            float2 denominator = parameter + r2;
            float2 ratio = weighted / denominator;
            float value = dot(ratio, ratio) - 1.0;
            float derivative = -2.0 * dot(ratio * ratio, 1.0 / denominator);
            if (value > 0.0) low = parameter; else high = parameter;
            float nextParameter = parameter - value / derivative;
            parameter = clamp(nextParameter, low, high);
        }
        float2 closest = r2 * position / (parameter + r2);
        return length(position - closest) * largestRadius;
    }

    p = abs(p);

    if (p.x > p.y) {
        p = p.yx;
        ab = ab.yx;
    }

    float l = ((ab.y * ab.y) - (ab.x * ab.x));
    float m = ((ab.x * p.x) / l);
    float m2 = (m * m);
    float n = ((ab.y * p.y) / l);
    float n2 = (n * n);
    float c = (((m2 + n2) - 1.0) / 3.0);
    float c3 = ((c * c) * c);
    float q = (c3 + ((m2 * n2) * 2.0));
    float d = (c3 + (m2 * n2));
    float g = (m + (m * n2));
    float co;

    if (d < 0.0) {
        float h = (acos(q / c3) / 3.0);
        float s = cos(h);
        float t = (sin(h) * SDF_SQRT3);
        float rx = sqrt((-c * ((s + t) + 2.0)) + m2);
        float ry = sqrt((-c * ((s - t) + 2.0)) + m2);
        co = ((((ry + (sign(l) * rx)) + (abs(g) / (rx * ry))) - m) / 2.0);
    } else {
        float h = ((2.0 * m) * (n * sqrt(d)));
        float s = (sign(q + h) * pow(abs(q + h), (1.0 / 3.0)));
        float u = (sign(q - h) * pow(abs(q - h), (1.0 / 3.0)));
        float rx = ((((-s - u) - (c * 4.0)) + (2.0 * m2)));
        float ry = ((s - u) * SDF_SQRT3);
        float rm = sqrt(((rx * rx) + (ry * ry)));
        co = ((((ry / sqrt(rm - rx)) + ((2.0 * g) / rm)) - m) / 2.0);
    }

    float2 r = (ab * float2(co, sqrt(saturate(1.0 - (co * co)))));
    return (length(r - p) * sign(p.y - r.y));
}

#endif
