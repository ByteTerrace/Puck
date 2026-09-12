/* SPDX-License-Identifier: Apache-2.0 OR MIT
 * Copyright (c) 2026 ByteTerrace
 * Public GBA service contracts: https://mgba-emu.github.io/gbatek/#biosfunctions
 */
#include "puck.h"

static void clear_words(u32 address, u32 bytes) {
    for (u32 end = address + bytes; address < end; address += 4) REG32(address) = 0;
}

void puck_register_reset(u32 flags) {
    /* Forced blank and the SIODATA32 low-halfword write are unconditional. */
    REG16(IO) = 0x80;
    REG16(IO + 0x120) = 0;
    if (flags & 1) clear_words(0x02000000, 0x40000);
    if (flags & 2) clear_words(0x03000000, 0x7E00);
    if (flags & 4) clear_words(0x05000000, 0x400);
    if (flags & 8) clear_words(0x06000000, 0x18000);
    if (flags & 16) clear_words(0x07000000, 0x400);
    if (flags & 32) {
        for (u32 offset = 0x120; offset < 0x15C; offset += 2)
            if (offset != 0x132) REG16(IO + offset) = 0;
        REG16(IO + 0x134) = 0x8000;
        REG16(IO + 0x140) = 7;
    }
    if (flags & 64) {
        REG16(IO + 0x84) = 0x80;
        for (u32 offset = 0x60; offset < 0x80; offset += 2) REG16(IO + offset) = 0;
        REG16(IO + 0x80) = 0;
        /* Reset playback/partial-write state, then prime each FIFO with the
         * two silent words observed by independent native reset probes. */
        REG16(IO + 0x82) = 0x880E;
        REG32(IO + 0xA0) = REG32(IO + 0xA4) = 0;
        REG32(IO + 0xA0) = REG32(IO + 0xA4) = 0;
        REG16(IO + 0x88) &= 0x03FF;
        REG16(IO + 0x70) = 0x70;
        for (u32 offset = 0x90; offset < 0xA0; offset += 2) REG16(IO + offset) = 0;
        REG16(IO + 0x84) = 0;
    }
    if (flags & 128) {
        for (u32 offset = 4; offset < 0x60; offset += 2) REG16(IO + offset) = 0;
        for (u32 offset = 0xB0; offset < 0x110; offset += 2) REG16(IO + offset) = 0;
        REG16(IO + 0x200) = 0;
        REG16(IO + 0x202) = 0xFFFF;
        REG16(IO + 0x204) = 0;
        REG16(IO + 0x208) = 0;
        REG16(IO + 0x132) = 0;
        REG16(IO + 0x20) = REG16(IO + 0x26) = 0x100;
        REG16(IO + 0x30) = REG16(IO + 0x36) = 0x100;
    }
}

static void transfer(u32 source, u32 destination, u32 control, u32 fast) {
    u32 count = control & 0x1FFFFF;
    if (!count || source < 0x02000000) return;
    u32 word = fast || (control & 0x04000000);
    u32 width = word ? 4 : 2;
    source &= ~(width - 1);
    destination &= ~(width - 1);
    if (fast) count = (count + 7) & ~7u;
    u32 value = word ? REG32(source) : REG16(source);
    for (u32 index = 0; index < count; ++index) {
        if (!(control & 0x01000000)) {
            value = word ? REG32(source) : REG16(source);
            source += width;
        }
        if (word) REG32(destination) = value;
        else REG16(destination) = (u16)value;
        destination += width;
    }
}

static void interrupt_wait(u32 discard, u32 mask) {
    REG16(IO + 0x208) = 0;
    if (discard) REG16(0x03007FF8) &= (u16)~mask;
    for (;;) {
        u32 ready = REG16(0x03007FF8) & mask;
        if (ready) {
            REG16(0x03007FF8) &= (u16)~ready;
            REG16(IO + 0x208) = 1;
            return;
        }
        REG16(IO + 0x208) = 1;
        REG8(IO + 0x301) = 0;
        REG16(IO + 0x208) = 0;
    }
}

void puck_dispatch(PuckSwiFrame *frame, u32 number) {
    u32 source = frame->r0, destination = frame->r1, count = frame->r2;
    switch (number) {
    case 0x00: puck_soft_reset();
    case 0x01: puck_register_reset(source); break;
    case 0x02: REG8(IO + 0x301) = 0; break;
    case 0x03: REG8(IO + 0x301) = 0x80; break;
    case 0x04: interrupt_wait(source, destination); break;
    case 0x05: interrupt_wait(1, 1); break;
    case 0x07: frame->r0 = destination; frame->r1 = source; /* fall through */
    case 0x06: puck_div(frame); break;
    case 0x08: frame->r0 = puck_sqrt(source); break;
    case 0x09: frame->r0 = (u32)puck_atan((s32)source); break;
    case 0x0A: frame->r0 = puck_atan2((s32)source, (s32)destination); break;
    case 0x0B: transfer(source, destination, count, 0); break;
    case 0x0C: transfer(source, destination, count, 1); break;
    /* This is the compatibility API result, not the identity of this image. */
    case 0x0D: frame->r0 = 0xBAAE187F; break;
    case 0x0E: puck_affine(source, destination, count, 0, 0); break;
    case 0x0F: puck_affine(source, destination, count, frame->r3, 1); break;
    case 0x10: puck_bit_unpack(source, destination, count); break;
    case 0x11: puck_lz(source, destination, 0); break;
    case 0x12: puck_lz(source, destination, 1); break;
    case 0x13: puck_huffman(source, destination); break;
    case 0x14: puck_rl(source, destination, 0); break;
    case 0x15: puck_rl(source, destination, 1); break;
    case 0x16: puck_diff(source, destination, 0); break;
    case 0x17: puck_diff(source, destination, 1); break;
    case 0x18: puck_diff(source, destination, 2); break;
    case 0x25: frame->r0 = puck_multiboot(source, destination); break;
    case 0x26: puck_reset();
    case 0x27: REG8(IO + 0x301) = (u8)count; break;
    default: puck_sound(frame, number); break;
    }
}
