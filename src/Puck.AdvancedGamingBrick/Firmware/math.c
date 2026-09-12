/* SPDX-License-Identifier: MIT
 * Copyright (c) 2026 ByteTerrace
 * Fixed-width integer algorithms. The trigonometric approximation coefficients
 * are compatibility data documented by the MIT Cult-of-GBA implementation.
 * No original-ROM table or instruction bytes are included.
 */
#include "puck.h"

u32 puck_udiv(u32 numerator, u32 denominator) {
    if (!denominator) return 0;
    u32 quotient = 0;
    for (int bit = 31; bit >= 0; --bit) {
        if ((numerator >> bit) >= denominator) {
            numerator -= denominator << bit;
            quotient |= 1u << bit;
        }
    }
    return quotient;
}

s32 puck_sdiv(s32 numerator, s32 denominator) {
    u32 a = numerator < 0 ? 0u - (u32)numerator : (u32)numerator;
    u32 b = denominator < 0 ? 0u - (u32)denominator : (u32)denominator;
    u32 result = puck_udiv(a, b);
    return (s32)((numerator < 0) != (denominator < 0) ? 0u - result : result);
}

void puck_div(PuckSwiFrame *frame) {
    s32 numerator = (s32)frame->r0, denominator = (s32)frame->r1;
    /* Zero divisors return for 0 and +/-1 only. Larger magnitudes do not return
     * in the independent bounded retail execution probes. Keep this native so
     * an emulator host can still step, pause and restore the machine. */
    if (!denominator && (u32)(numerator + 1u) > 2u) for (;;) __asm__ volatile ("nop");
    s32 quotient = denominator ? puck_sdiv(numerator, denominator) : (numerator < 0 ? -1 : 1);
    frame->r0 = (u32)quotient;
    frame->r1 = (u32)numerator - (u32)quotient * (u32)denominator;
    frame->r3 = quotient < 0 ? 0u - (u32)quotient : (u32)quotient;
}

u32 puck_sqrt(u32 value) {
    u32 result = 0;
    for (u32 bit = 0x40000000; bit; bit >>= 2) {
        u32 trial = result + bit;
        result >>= 1;
        if (value >= trial) {
            value -= trial;
            result += bit;
        }
    }
    return result;
}

static s32 product_shift(s32 left, s32 right, u32 shift) {
    return (s32)((u32)left * (u32)right) >> shift;
}

s32 puck_atan(s32 tangent) {
    static const u16 coefficients[] = { 0x390, 0x91C, 0xFB6, 0x16AA, 0x2081, 0x3651, 0xA2F9 };
    s32 square = (s32)(0u - (u32)product_shift(tangent, tangent, 14));
    s32 polynomial = 0xA9;
    for (u32 index = 0; index < 7; ++index)
        polynomial = (s32)((u32)product_shift(polynomial, square, 14) + coefficients[index]);
    return product_shift(polynomial, tangent, 16);
}

u32 puck_atan2(s32 x, s32 y) {
    if (!y) return x < 0 ? 0x8000 : 0;
    if (!x) return y < 0 ? 0xC000 : 0x4000;
    u32 ax = x < 0 ? 0u - (u32)x : (u32)x;
    u32 ay = y < 0 ? 0u - (u32)y : (u32)y;
    s32 angle;
    if (ax >= ay) {
        angle = puck_atan(puck_sdiv((s32)((u32)y << 14), x));
        if (x < 0) angle += 0x8000;
    } else {
        angle = 0x4000 - puck_atan(puck_sdiv((s32)((u32)x << 14), y));
        if (y < 0) angle += 0x8000;
    }
    return (u16)angle;
}

/* Generated from floor(16384 * sin(index * pi / 128)), index 0..64.
 * Reflection supplies the other quadrants without importing a BIOS table. */
static const u16 sine_quarter[65] = {
    0, 402, 803, 1205, 1605, 2005, 2404, 2801,
    3196, 3589, 3980, 4369, 4756, 5139, 5519, 5896,
    6269, 6639, 7005, 7366, 7723, 8075, 8423, 8765,
    9102, 9434, 9759, 10079, 10393, 10701, 11002, 11297,
    11585, 11866, 12139, 12406, 12665, 12916, 13159, 13395,
    13622, 13842, 14053, 14255, 14449, 14634, 14810, 14978,
    15136, 15286, 15426, 15557, 15678, 15790, 15892, 15985,
    16069, 16142, 16206, 16260, 16305, 16339, 16364, 16379,
    16384,
};

static s32 sine(u32 phase) {
    phase &= 255;
    u32 offset = phase & 63;
    u32 value = sine_quarter[(phase & 64) ? 64 - offset : offset];
    return (phase & 128) ? -(s32)value : (s32)value;
}

void puck_affine(u32 source, u32 destination, u32 count, u32 stride, u32 object) {
    while (count--) {
        u32 scaling = source + (object ? 0 : 12);
        s32 sx = (s16)REG16(scaling), sy = (s16)REG16(scaling + 2);
        u32 phase = REG16(scaling + 4) >> 8;
        s32 sin = sine(phase), cos = sine(phase + 64);
        s32 a = product_shift(sx, cos, 14);
        s32 b = -product_shift(sx, sin, 14);
        s32 c = product_shift(sy, sin, 14);
        s32 d = product_shift(sy, cos, 14);
        u32 step = object ? stride : 2;
        REG16(destination) = (u16)a;
        REG16(destination + step) = (u16)b;
        REG16(destination + step * 2) = (u16)c;
        REG16(destination + step * 3) = (u16)d;
        if (!object) {
            s32 x = (s16)REG16(source + 8), y = (s16)REG16(source + 10);
            REG32(destination + 8) = REG32(source) - (u32)x * (u32)a - (u32)y * (u32)b;
            REG32(destination + 12) = REG32(source + 4) - (u32)x * (u32)c - (u32)y * (u32)d;
        }
        source += object ? 8 : 20;
        destination += object ? stride * 4 : 16;
    }
}
