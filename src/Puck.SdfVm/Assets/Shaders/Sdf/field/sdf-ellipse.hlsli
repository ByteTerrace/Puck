#ifndef PUCK_SDF_ELLIPSE_HLSLI
#define PUCK_SDF_ELLIPSE_HLSLI

// The signed Euclidean distance from p to the ellipse of semi-axes ab, negative inside: Eberly's robust projection, the
// one root of F(u) = (r·z0 / (u − 1 + r))² + (z1 / u)² − 1 bisected on its bracket, where z is the point over the
// semi-axes with the major axis first, r the squared ratio of the axes and u the minor axis's projection parameter plus
// one. Bisecting u rather than the parameter itself keeps its relative precision where it nears zero, beside the major
// axis inside the ellipse; float cannot hold a closed form's cancellation there or along either axis. The points on
// either axis take the CPU evaluator's branches (SdfEllipse2D in SdfFieldEvaluator.Ellipse.cs, KEEP IN SYNC), which
// answers the same distance in Q48.16.
float sdfEllipse2D(float2 p, float2 ab) {
    float2 y = abs(p);
    float2 e = ab;

    // The major axis first.
    if (e.x < e.y) {
        y = y.yx;
        e = e.yx;
    }
    if (y.y > 0.0) {
        if (y.x <= 0.0) {
            return (y.y - e.y);
        }

        float2 z = (y / e);
        float g = (dot(z, z) - 1.0);
        float ratio = (e.x / e.y);
        float r = (ratio * ratio);
        float n = (r * z.x);
        // The minor axis's term bounds u below by z1; zero inside and the projection's reach outside bound the parameter
        // above.
        float low = z.y;
        float high = ((g < 0.0) ? 1.0 : length(float2(n, z.y)));
        float u = low;

        [loop]
        for (uint step = 0u; (step < 64u); step++) {
            u = (0.5 * (low + high));
            if ((u <= low) || (u >= high)) {
                break;
            }

            float a = (n / ((u - 1.0) + r));
            float b = (z.y / u);
            float value = (((a * a) + (b * b)) - 1.0);

            if (value > 0.0) {
                low = u;
            } else {
                high = u;
            }
        }

        float2 nearest = float2(((r * y.x) / ((u - 1.0) + r)), (y.y / u));
        float distance = length(nearest - y);

        return ((g < 0.0) ? -distance : distance);
    }

    // On the major axis: inside, nearest a point off it while the axis point lies within its evolute's cusp; otherwise
    // nearest the vertex.
    float numerator = (e.x * y.x);
    float denominator = ((e.x * e.x) - (e.y * e.y));

    if (numerator < denominator) {
        float t = (numerator / denominator);
        float2 nearest = float2((e.x * t), (e.y * sqrt(max(0.0, (1.0 - (t * t))))));

        return -length(nearest - float2(y.x, 0.0));
    }

    return (y.x - e.x);
}

#endif
