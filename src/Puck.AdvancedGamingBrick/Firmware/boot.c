/* SPDX-License-Identifier: Apache-2.0 OR MIT
 * Copyright (c) 2026 ByteTerrace
 * Original ByteTerrace/Puck pixel lettering and startup sound. Presentation is
 * executed by the emulated CPU and hardware; no cartridge logo is used as art.
 */
#include "puck.h"

/* 5-by-7 cell lettering authored for this firmware, row bits high-to-low. */
static const u8 letters[][7] = {
    { 30,17,17,30,16,16,16 }, /* P */
    { 17,17,17,17,17,17,14 }, /* U */
    { 15,16,16,16,16,16,15 }, /* C */
    { 17,18,20,24,20,18,17 }, /* K */
    { 30,17,17,30,17,17,30 }, /* B */
    { 17,17,10,4,4,4,4 },    /* Y */
    { 31,4,4,4,4,4,4 },     /* T */
    { 31,16,16,30,16,16,31 },/* E */
    { 30,17,17,30,20,18,17 },/* R */
    { 14,17,17,31,17,17,17 },/* A */
    { 16,16,16,16,16,16,31 },/* L */
    { 14,4,4,4,4,4,14 },    /* I */
    { 17,25,25,21,19,19,17 } /* N */
};

/* The same native wordmark cells as the Humble firmware. */
static const u8 wordmark[4][7] = {
    { 0x7C,0x66,0x66,0x7C,0x60,0x60,0x60 },
    { 0x66,0x66,0x66,0x66,0x66,0x66,0x3C },
    { 0x3C,0x66,0x60,0x60,0x60,0x66,0x3C },
    { 0x66,0x6C,0x78,0x70,0x78,0x6C,0x66 }
};

static void rectangle(u32 x, u32 y, u32 width, u32 height, u16 color) {
    for (u32 row = 0; row < height; ++row)
        for (u32 column = 0; column < width; ++column)
            REG16(0x06000000 + ((y + row) * 240 + x + column) * 2) = color;
}

static void letter(u32 glyph, u32 x, u32 y, u32 scale, u16 color) {
    for (u32 row = 0; row < 7; ++row)
        for (u32 column = 0; column < 5; ++column)
            if (letters[glyph][row] & (16u >> column))
                rectangle(x + column * scale, y + row * scale, scale, scale, color);
}

void puck_wait_frame(void) {
    while (REG16(IO + 6) >= 160) { }
    while (REG16(IO + 6) < 160) { }
}

static void note(u32 frequency) {
    REG16(IO + 0x68) = 0x8340; /* Low-volume pulse with a short falling envelope. */
    REG16(IO + 0x6C) = (u16)(0x8000 | frequency);
}

void puck_boot(void) {
    static const u8 publisher[] = { 4,5,6,7,6,7,8,8,9,2,7 };
    const u16 ink = 0x1C83, ivory = 0x6FBC, mint = 0x5F6C;
    u32 download = puck_download_requested();
    REG16(IO) = 0x80;
    rectangle(0, 0, 240, 160, ink);
    for (u32 index = 0; index < 4; ++index)
        for (u32 row = 0; row < 7; ++row)
            for (u32 column = 0; column < 8; ++column)
                if (wordmark[index][row] & (128u >> column))
                    rectangle(56 + index * 32 + column * 4, 55 + row * 4, 4, 4, ivory);
    for (u32 index = 0; index < sizeof(publisher); ++index)
        letter(publisher[index], 87 + index * 6, 102, 1, mint);
    rectangle(111, 91, 18, 2, mint);
    REG16(IO + 0x50) = 0x00C4;
    REG16(IO + 0x54) = 16;
    REG16(IO) = 0x0403; /* Native mode 3, background 2. */
    REG16(IO + 0x84) = 0x80;
    REG16(IO + 0x80) = 0x2277;
    REG16(IO + 0x82) = 2;
    REG16(IO + 0x88) = 0x200;
    for (u32 frame = 0; frame < 72; ++frame) {
        puck_wait_frame();
        u32 dark = frame < 24 ? 16 - puck_udiv(frame * 16, 24)
            : frame < 56 || download ? 0 : frame - 56;
        REG16(IO + 0x54) = (u16)dark;
        if (frame == 23) note(0x720);
        if (frame == 35) note(0x76B);
    }
    REG16(IO + 0x84) = 0;
    if (download) {
        static const u8 link[] = { 10,11,12,3 };
        for (u32 index = 0; index < sizeof(link); ++index)
            letter(link[index], 108 + index * 6, 118, 1, mint);
        puck_receive_download();
    }
    REG16(IO) = 0x80;
}
