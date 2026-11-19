// A float's bits remapped so that their unsigned order is the float's order, for a min or max a kernel takes with the
// integer atomics (InterlockedMin, InterlockedMax) over values of either sign: a non-negative float gains its top bit and a
// negative one is inverted whole, so larger magnitudes sort lower. Minus zero sorts just below plus zero, and a NaN sorts
// past the infinity of its sign. A float known non-negative needs none of it: its own bits already sort in its order.
#ifndef PUCK_ORDERED_FLOAT_HLSLI
#define PUCK_ORDERED_FLOAT_HLSLI

uint puckOrderedFromFloat(float value) {
    uint bits = asuint(value);

    return (bits ^ (((bits >> 31u) != 0u) ? 0xFFFFFFFFu : 0x80000000u));
}
float puckFloatFromOrdered(uint ordered) {
    return asfloat(ordered ^ (((ordered >> 31u) != 0u) ? 0x80000000u : 0xFFFFFFFFu));
}
// The ordered bits' extremes: the starting value of a running minimum and of a running maximum over the ordered bits.
static const uint PuckOrderedHighest = 0xFFFFFFFFu;
static const uint PuckOrderedLowest = 0u;

#endif
