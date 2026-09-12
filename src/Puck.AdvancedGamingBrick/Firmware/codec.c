/* SPDX-License-Identifier: MIT
 * Copyright (c) 2026 ByteTerrace
 * Original decoders for the public cartridge compression formats documented in
 * GBATEK. No upstream decompiled BitUnpack/Huffman implementations are included.
 */
#include "puck.h"

static void output_byte(u32 *destination, u32 *pending, u32 wide, u8 value) {
    u32 address = *destination;
    if (!wide) REG8(address) = value;
    else if (address & 1) REG16(address - 1) = (u16)(*pending | ((u32)value << 8));
    else *pending = value;
    *destination = address + 1;
}

void puck_bit_unpack(u32 source, u32 destination, u32 info) {
    if (source < 0x02000000) return;
    u32 length = REG16(info), input_width = REG8(info + 2), output_width = REG8(info + 3);
    if ((input_width != 1 && input_width != 2 && input_width != 4 && input_width != 8)
        || !output_width || output_width > 32 || (output_width & (output_width - 1))) return;
    u32 offset = REG32(info + 4), mask = (1u << input_width) - 1;
    u32 word = 0, used = 0;
    while (length--) {
        u32 data = REG8(source++);
        for (u32 bit = 0; bit < 8; bit += input_width) {
            u32 value = (data >> bit) & mask;
            if (value || (offset & 0x80000000)) value += offset & 0x7FFFFFFF;
            word |= value << used;
            used += output_width;
            if (used == 32) {
                REG32(destination) = word;
                destination += 4;
                used = word = 0;
            }
        }
    }
}

void puck_lz(u32 source, u32 destination, u32 wide) {
    if (source < 0x02000000) return;
    u32 length = REG32(source) >> 8, pending = 0;
    source += 4;
    while (length) {
        u32 flags = REG8(source++);
        for (u32 flag = 128; flag && length; flag >>= 1) {
            if (!(flags & flag)) {
                output_byte(&destination, &pending, wide, REG8(source++));
                --length;
            } else {
                u32 code = REG8(source++);
                u32 distance = ((code & 15) << 8) + REG8(source++) + 1;
                u32 count = (code >> 4) + 3;
                while (count-- && length) {
                    u8 value = REG8(destination - distance);
                    output_byte(&destination, &pending, wide, value);
                    --length;
                }
            }
        }
    }
}

void puck_rl(u32 source, u32 destination, u32 wide) {
    if (source < 0x02000000) return;
    u32 length = REG32(source) >> 8, pending = 0;
    source += 4;
    while (length) {
        u32 code = REG8(source++);
        u32 repeat = code & 128;
        u32 count = (code & 127) + (repeat ? 3 : 1);
        u8 value = repeat ? REG8(source++) : 0;
        while (count-- && length) {
            if (!repeat) value = REG8(source++);
            output_byte(&destination, &pending, wide, value);
            --length;
        }
    }
}

void puck_diff(u32 source, u32 destination, u32 width) {
    if (source < 0x02000000) return;
    u32 length = REG32(source) >> 8, sum = 0, pending = 0;
    source += 4;
    if (width == 2) {
        for (u32 count = length >> 1; count; --count) {
            sum += REG16(source);
            REG16(destination) = (u16)sum;
            source += 2;
            destination += 2;
        }
    } else {
        while (length--) {
            sum += REG8(source++);
            output_byte(&destination, &pending, width, (u8)sum);
        }
    }
}

void puck_huffman(u32 source, u32 destination) {
    if (source < 0x02000000) return;
    u32 header = REG32(source), length = header >> 8, width = header & 15;
    if (width != 4 && width != 8) return;
    u32 root = source + 5, node = root;
    source += 6 + ((u32)REG8(source + 4) << 1);
    u32 word = 0, used = 0;
    while (length) {
        u32 code = REG32(source);
        source += 4;
        for (u32 mask = 0x80000000; mask && length; mask >>= 1) {
            u32 descriptor = REG8(node), branch = (code & mask) ? 1 : 0;
            node = (node & ~1u) + ((descriptor & 63) + 1) * 2 + branch;
            if (descriptor & (128u >> branch)) {
                word |= (u32)REG8(node) << used;
                used += width;
                node = root;
                if (used == 32) {
                    REG32(destination) = word;
                    destination += 4;
                    length = length > 4 ? length - 4 : 0;
                    used = word = 0;
                }
            }
        }
    }
}
