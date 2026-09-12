/* SPDX-License-Identifier: Apache-2.0 OR MIT
 * Copyright (c) 2026 ByteTerrace
 * GBA MultiBoot sender, implemented from GBATEK's public wire protocol.
 * The initiating cartridge still owns discovery and header negotiation.
 */
#include "puck.h"

typedef struct { u32 mode, clients; u16 response[4]; } PuckLink;

static u32 transfer(PuckLink *link, u32 value) {
    if (link->mode == 1) REG16(IO + 0x12A) = (u16)value;
    else REG32(IO + 0x120) = value;
    REG16(IO + 0x128) |= 0x80;
    u32 timeout = 0x20000;
    while ((REG16(IO + 0x128) & 0x80) && --timeout) { }
    if (!timeout || (REG16(IO + 0x128) & 0x40)) return 0;
    for (u32 index = 0; index < 4; ++index)
        link->response[index] = REG16(IO + 0x120 + index * 2);
    puck_delay(160); /* At least the required 36 us inter-transfer settling. */
    return 1;
}

static u32 all_match(PuckLink *link, u32 value, u32 mask) {
    for (u32 index = 1; index < 4; ++index)
        if ((link->clients & (1u << index)) && (link->response[index] & mask) != value) return 0;
    return 1;
}

static u32 crc_word(u32 crc, u32 value, u32 polynomial) {
    crc ^= value;
    for (u32 bit = 0; bit < 32; ++bit) crc = (crc >> 1) ^ ((crc & 1) ? polynomial : 0);
    return crc;
}

u32 puck_multiboot(u32 parameters, u32 mode) {
    if (mode > 2 || parameters < 0x02000000 || (parameters & 3)) return 1;
    u32 source = REG32(parameters + 0x20), end = REG32(parameters + 0x24);
    if (source < 0x02000000 || (source & 3) || end < source) return 1;
    u32 length = end - source;
    if (length < 0x100 || length > 0x3FF40 || (length & 15)) return 1;
    PuckLink link;
    link.mode = mode;
    link.clients = REG8(parameters + 0x1E) & 14;
    if (!link.clients || (mode != 1 && link.clients != 2)) return 1;
    REG16(IO + 0x134) = 0;
    REG16(IO + 0x128) = mode == 1 ? 0x2003 : mode == 2 ? 0x1003 : 0x1001;
    puck_delay(262144); /* 1/16 second or more, entirely in native CPU clocks. */
    /* The wire length includes the C0h-byte header, already sent by the game. */
    if (!transfer(&link, (length >> 2) - 4)) return 1;
    if (!all_match(&link, 0x7300, 0xFF00)) return 1;
    u32 seed = REG8(parameters + 0x1C), final = REG8(parameters + 0x14);
    for (u32 index = 1; index < 4; ++index) {
        seed |= (u32)REG8(parameters + 0x18 + index) << (index * 8);
        final |= ((link.clients & (1u << index)) ? link.response[index] & 255 : 255) << (index * 8);
    }
    u32 crc = mode == 1 ? 0xFFF8 : 0xC387;
    u32 polynomial = mode == 1 ? 0xA517 : 0xC37B;
    u32 key = mode == 1 ? 0x6465646F : 0x43202F2F;
    for (u32 offset = 0; offset < length; offset += 4) {
        u32 value = REG32(source + offset);
        crc = crc_word(crc, value, polynomial);
        seed = 0x6F646573 * seed + 1;
        u32 encoded = value ^ (0u - 0x020000C0 - offset) ^ seed ^ key;
        if (!transfer(&link, mode == 1 ? encoded & 0xFFFF : encoded)) return 1;
        if (!all_match(&link, (offset + 0xC0) & 0xFFFF, 0xFFFF)) return 1;
        if (mode == 1 && !transfer(&link, encoded >> 16)) return 1;
    }
    crc = crc_word(crc, final, polynomial);
    u32 attempts = 1024;
    do {
        if (!transfer(&link, 0x65)) return 1;
        if (all_match(&link, 0x75, 0xFFFF)) break;
    } while (--attempts);
    if (!attempts || !transfer(&link, 0x66) || !transfer(&link, crc)) return 1;
    return all_match(&link, crc, 0xFFFF) ? 0 : 1;
}

u32 puck_download_requested(void) {
    if (!(REG16(IO + 0x130) & 12)) return 1; /* START + SELECT at power-on. */
    u32 first = REG32(0x08000000), second = REG32(0x08000004);
    return (first == 0x00010000 && second == 0x00030002)
        || (first == 0xFFFFFFFF && second == 0xFFFFFFFF);
}

typedef struct { u32 mode, last, value, client, clients; } PuckReceiver;

static u32 receive(PuckReceiver *receiver, u32 response) {
    if (receiver->mode == 1) REG16(IO + 0x12A) = (u16)response;
    else REG32(IO + 0x120) = (response << 16) | (receiver->last & 0xFFFF);
    REG16(IO + 0x202) = 128;
    if (receiver->mode != 1) REG16(IO + 0x128) |= 128;
    u32 timeout = 0x100000;
    while (!(REG16(IO + 0x202) & 128) && --timeout) { }
    if (!timeout) return 0;
    receiver->value = receiver->mode == 1 ? REG16(IO + 0x120) : REG32(IO + 0x120);
    receiver->last = receiver->value;
    if (receiver->mode == 1) receiver->client = (REG16(IO + 0x128) >> 4) & 3;
    return receiver->client != 0;
}

static u32 negotiate(PuckReceiver *receiver, u32 *seed, u32 *final, u32 *length) {
    if (!receive(receiver, 0) || receiver->value != 0x6200) return 0;
    u32 bit = 1u << receiver->client;
    do {
        if (!receive(receiver, 0x7200 | bit)) return 0;
    } while (receiver->value == 0x6200);
    if ((receiver->value & 0xFFF0) != 0x6100 || !(receiver->value & bit)) return 0;
    receiver->clients = receiver->value & 14;
    for (u32 index = 0; index < 0x60; ++index) {
        if (!receive(receiver, ((0x60 - index) << 8) | bit)) return 0;
        REG16(0x02000000 + index * 2) = (u16)receiver->value;
    }
    if (!receive(receiver, bit) || receiver->value != 0x6200) return 0;
    if (!receive(receiver, 0x7200 | bit) || (receiver->value & 0xFFF0) != 0x6200) return 0;
    u32 client_data = 0x10 + receiver->client;
    do {
        if (!receive(receiver, 0x7200 | bit)) return 0;
    } while ((receiver->value & 0xFF00) != 0x6300);
    u32 palette = receiver->value & 255;
    do {
        if (!receive(receiver, 0x7300 | client_data)) return 0;
    } while ((receiver->value & 0xFF00) == 0x6300);
    if ((receiver->value & 0xFF00) != 0x6400) return 0;
    u32 handshake = receiver->value & 255, random = 0xA0 + receiver->client;
    *seed = palette;
    for (u32 index = 1; index < 4; ++index) {
        u32 client = (receiver->clients & (1u << index))
            ? receiver->mode == 1 ? REG16(IO + 0x120 + index * 2) & 255 : client_data : 255;
        *seed |= client << (index * 8);
    }
    if (!receive(receiver, 0x7300 | random)) return 0;
    *length = (receiver->value + 4) << 2;
    if (*length < 0x100 || *length > 0x3FF40 || (*length & 15)) return 0;
    /* Multiplayer receives every child's response in SIOMULTI1..3, so each
     * child derives the identical seed and CRC suffix even with three peers. */
    *final = handshake;
    for (u32 index = 1; index < 4; ++index) {
        u32 client = (receiver->clients & (1u << index))
            ? receiver->mode == 1 ? REG16(IO + 0x120 + index * 2) & 255 : random : 255;
        *final |= client << (index * 8);
    }
    return 1;
}

static u32 download(PuckReceiver *receiver, u32 seed, u32 final, u32 length) {
    u32 crc = receiver->mode == 1 ? 0xFFF8 : 0xC387;
    u32 polynomial = receiver->mode == 1 ? 0xA517 : 0xC37B;
    u32 key = receiver->mode == 1 ? 0x6465646F : 0x43202F2F;
    for (u32 offset = 0; offset < length; offset += 4) {
        if (!receive(receiver, (offset + 0xC0) & 0xFFFF)) return 0;
        u32 encoded = receiver->value;
        if (receiver->mode == 1) {
            if (!receive(receiver, (offset + 0xC2) & 0xFFFF)) return 0;
            encoded |= receiver->value << 16;
        }
        seed = 0x6F646573 * seed + 1;
        u32 value = encoded ^ (0u - 0x020000C0 - offset) ^ seed ^ key;
        REG32(0x020000C0 + offset) = value;
        crc = crc_word(crc, value, polynomial);
    }
    crc = crc_word(crc, final, polynomial);
    if (!receive(receiver, (length + 0xC0) & 0xFFFF) || receiver->value != 0x65) return 0;
    do {
        if (!receive(receiver, 0x75)) return 0;
    } while (receiver->value == 0x65);
    if (receiver->value != 0x66 || !receive(receiver, crc)) return 0;
    return (receiver->value & 0xFFFF) == crc;
}

void puck_receive_download(void) {
    PuckReceiver receiver;
    receiver.mode = 1;
    for (;;) {
        receiver.client = receiver.mode == 1 ? 0 : 1;
        receiver.last = 0;
        REG16(IO + 0x134) = 0;
        REG16(IO + 0x128) = receiver.mode == 1 ? 0x6003 : 0x5000;
        u32 seed, final, length;
        if (negotiate(&receiver, &seed, &final, &length)
            && download(&receiver, seed, final, length)) {
            REG8(0x020000C4) = receiver.mode == 1 ? 3 : 2;
            REG8(0x020000C5) = (u8)receiver.client;
            puck_register_reset(0xFE); /* Preserve the just-received EWRAM. */
            REG8(IO + 0x300) = 1;
            puck_download_enter();
        }
        receiver.mode ^= 1;
    }
}
