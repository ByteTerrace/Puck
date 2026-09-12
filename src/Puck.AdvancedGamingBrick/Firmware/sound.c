/* SPDX-License-Identifier: MIT
 * Copyright (c) 2026 ByteTerrace
 * Original public-SoundArea PCM driver. The documented channel controls are the
 * interface. Legacy player-management layouts are pinned by independent
 * black-box RAM fixtures; sequencing uses the published M4A song format.
 */
#include "puck.h"

#define SOUND_MAGIC 0x68736D53u
#define PCM_OFFSET 0x350u
#define PCM_CAPACITY 1584u
#define CHANNEL_OFFSET 0x50u
#define CHANNEL_BYTES 0x40u

static void noop(void) { }
static void music_main(u32 player);
typedef void (*MusicCommand)(u32 player, u32 track);
static const MusicCommand commands[36];
static u32 midi_frequency(u32 wave, u32 key, u32 fine);

static u32 next_byte(u32 track) {
    u32 pointer = REG32(track + 0x40);
    REG32(track + 0x40) = pointer + 1;
    return REG8(pointer);
}

static u32 read_pointer(u32 address) {
    return REG8(address) | (REG8(address + 1) << 8) | (REG8(address + 2) << 16) | (REG8(address + 3) << 24);
}

static u32 sound_area(void) {
    u32 area = REG32(SOUND_AREA);
    return area >= 0x02000000 && area < 0x04000000 && REG32(area) == SOUND_MAGIC ? area : 0;
}

static void clear_pcm(u32 area) {
    for (u32 index = 0; index < PCM_CAPACITY * 2; ++index) REG8(area + PCM_OFFSET + index) = 0;
}

static void dma_start(u32 area) {
    REG16(IO + 0xC6) = REG16(IO + 0xD2) = 0;
    REG32(IO + 0xBC) = area + PCM_OFFSET;
    REG32(IO + 0xC0) = IO + 0xA0;
    REG32(IO + 0xC8) = area + PCM_OFFSET + PCM_CAPACITY;
    REG32(IO + 0xCC) = IO + 0xA4;
    REG16(IO + 0xC6) = REG16(IO + 0xD2) = 0xB600;
}

static void mode(u32 area, u32 value) {
    static const u16 rates[12] = { 5734,7884,10512,13379,15768,18157,21024,26758,31536,36314,40137,42048 };
    static const u16 samples[12] = { 96,132,176,224,264,304,352,448,528,608,672,704 };
    if (value & 128) REG8(area + 5) = (u8)(value & 127);
    u32 channels = (value >> 8) & 15, volume = (value >> 12) & 15;
    if (channels) REG8(area + 6) = (u8)(channels > 12 ? 12 : channels);
    if (volume) REG8(area + 7) = (u8)volume;
    u32 resolution = (value >> 20) & 15;
    if (resolution >= 8 && resolution <= 11)
        REG16(IO + 0x88) = (REG16(IO + 0x88) & 0x3FFF) | (u16)((resolution - 8) << 14);
    u32 frequency = (value >> 16) & 15;
    if (!frequency || frequency > 12) return;
    --frequency;
    REG16(IO + 0xC6) = REG16(IO + 0xD2) = 0;
    REG16(IO + 0x102) = 0;
    REG8(area + 8) = (u8)(frequency + 1);
    REG32(area + 0x10) = samples[frequency];
    REG32(area + 0x14) = rates[frequency];
    REG32(area + 0x18) = puck_udiv(0x800000 + rates[frequency] / 2, rates[frequency]);
    REG8(area + 0xB) = (u8)puck_udiv(PCM_CAPACITY, samples[frequency]);
    REG8(area + 4) = REG8(area + 0xB);
    REG16(area + 0xC) = 0;
    clear_pcm(area);
    REG16(IO + 0x100) = (u16)(0u - puck_udiv(16777216, rates[frequency]));
    dma_start(area);
    REG16(IO + 0x102) = 0x80;
}

static void player_initialize(u32 player) {
    for (u32 offset = 0; offset < 64; offset += 4) REG32(player + offset) = 0;
}

static void channel_unlink(u32 channel, u32 unused) {
    (void)unused;
    u32 track = REG32(channel + 0x2C);
    if (!track) return;
    u32 previous = REG32(channel + 0x30), next = REG32(channel + 0x34);
    if (previous) REG32(previous + 0x34) = next;
    else REG32(track + 0x20) = next;
    if (next) REG32(next + 0x30) = previous;
    REG32(channel + 0x2C) = 0;
}

static void track_stop(u32 player, u32 track) {
    (void)player;
    if (!(REG8(track) & 128)) return;
    u32 area = REG32(SOUND_AREA), channel = REG32(track + 0x20);
    while (channel) {
        u32 next = REG32(channel + 0x34), type = REG8(channel + 1) & 7;
        if (type && (REG8(channel) & 0xC7) && REG32(area + 0x2C)) ((void (*)(u32))REG32(area + 0x2C))(type);
        REG8(channel) = 0;
        channel_unlink(channel, 0);
        channel = next;
    }
    REG32(track + 0x20) = 0;
}

static void player_stop(u32 player) {
    if (REG32(player + 0x34) != SOUND_MAGIC) return;
    REG32(player + 4) |= 0x80000000;
    u32 tracks = REG32(player + 0x2C);
    for (u32 index = 0; index < REG8(player + 8); ++index) track_stop(player, tracks + index * 80);
}

static void player_open(u32 area, u32 player, u32 tracks, u32 count) {
    player_initialize(player);
    count = count > 16 ? 16 : count ? count : 1;
    REG8(player + 8) = (u8)count;
    REG32(player + 4) = 0x80000000;
    REG32(player + 0x2C) = tracks;
    for (u32 index = 0; index < count; ++index) REG8(tracks + index * 80) = 0;
    REG32(player + 0x38) = REG32(area + 0x20);
    REG32(player + 0x3C) = REG32(area + 0x24);
    REG32(player + 0x34) = SOUND_MAGIC;
    REG32(area + 0x20) = (u32)music_main;
    REG32(area + 0x24) = player;
}

static void player_start(u32 area, u32 player, u32 song) {
    if (REG32(player + 0x34) != SOUND_MAGIC) return;
    u32 tracks = REG32(player + 0x2C), count = REG8(player + 8);
    player_stop(player);
    REG32(player) = song;
    REG32(player + 4) = REG32(player + 0xC) = 0;
    REG8(player + 9) = REG8(song + 2);
    REG16(player + 0x1C) = REG16(player + 0x20) = 150;
    REG16(player + 0x1E) = 256;
    REG16(player + 0x22) = 0;
    REG32(player + 0x24) = REG32(player + 0x28) = 0;
    REG32(player + 0x30) = REG32(song + 4);
    for (u32 index = 0; index < count; ++index) {
        u32 track = tracks + index * 80;
        REG8(track) = index < REG8(song) ? 0xC0 : 0;
        if (index < REG8(song)) REG32(track + 0x40) = REG32(song + 8 + index * 4);
    }
    if (REG8(song + 3) & 128) mode(area, REG8(song + 3));
}

static void fade_step(u32 player) {
    u32 interval = REG16(player + 0x24);
    if (!interval) return;
    u32 counter = REG16(player + 0x26);
    if (counter > 1) { REG16(player + 0x26) = (u16)(counter - 1); return; }
    REG16(player + 0x26) = (u16)interval;
    u32 volume = REG16(player + 0x28);
    volume = volume > 16 ? volume - 16 : 0;
    REG16(player + 0x28) = (u16)volume;
    for (u32 index = 0; index < REG8(player + 8); ++index) {
        u32 track = REG32(player + 0x2C) + index * 80;
        REG8(track + 0x13) = (u8)(volume >> 2);
        REG8(track) |= 3;
    }
    if (!volume) { REG16(player + 0x24) = 0; player_stop(player); }
}

static void command_fine(u32 player, u32 track) {
    (void)player;
    u32 channel = REG32(track + 0x20);
    while (channel) {
        u32 next = REG32(channel + 0x34);
        REG8(channel) |= 64;
        channel_unlink(channel, 0);
        channel = next;
    }
    REG8(track) = 0;
}

static void command_goto(u32 player, u32 track) {
    (void)player;
    REG32(track + 0x40) = read_pointer(REG32(track + 0x40));
}

static void command_pattern(u32 player, u32 track) {
    u32 depth = REG8(track + 2);
    if (depth >= 3) { command_fine(player, track); return; }
    REG32(track + 0x44 + depth * 4) = REG32(track + 0x40) + 4;
    REG8(track + 2) = (u8)(depth + 1);
    command_goto(player, track);
}

static void command_pattern_end(u32 player, u32 track) {
    (void)player;
    u32 depth = REG8(track + 2);
    if (depth) {
        REG8(track + 2) = (u8)(--depth);
        REG32(track + 0x40) = REG32(track + 0x44 + depth * 4);
    }
}

static void command_repeat(u32 player, u32 track) {
    u32 count = next_byte(track);
    if (!count) { command_goto(player, track); return; }
    u32 iteration = REG8(track + 3) + 1;
    REG8(track + 3) = (u8)iteration;
    if (iteration < count) command_goto(player, track);
    else { REG8(track + 3) = 0; REG32(track + 0x40) += 4; }
}

static void command_field(u32 track, u32 offset, u32 flags, u32 bias) {
    REG8(track + offset) = (u8)(next_byte(track) - bias);
    REG8(track) |= (u8)flags;
}

#define FIELD_COMMAND(name, offset, flags, bias) \
    static void name(u32 player, u32 track) { (void)player; command_field(track, offset, flags, bias); }
FIELD_COMMAND(command_priority, 0x1D, 0, 0)
FIELD_COMMAND(command_keyshift, 0x0A, 12, 0)
FIELD_COMMAND(command_volume, 0x12, 3, 0)
FIELD_COMMAND(command_pan, 0x14, 3, 64)
FIELD_COMMAND(command_bend, 0x0E, 12, 64)
FIELD_COMMAND(command_bend_range, 0x0F, 12, 0)
FIELD_COMMAND(command_lfo_speed, 0x19, 0, 0)
FIELD_COMMAND(command_lfo_delay, 0x1B, 0, 0)
FIELD_COMMAND(command_modulation, 0x17, 0, 0)
FIELD_COMMAND(command_modulation_type, 0x18, 15, 0)
FIELD_COMMAND(command_tune, 0x0C, 12, 64)

static void command_tempo(u32 player, u32 track) {
    u32 tempo = next_byte(track) * 2;
    REG16(player + 0x1C) = (u16)tempo;
    REG16(player + 0x20) = (u16)((tempo * REG16(player + 0x1E)) >> 8);
}

static void command_voice(u32 player, u32 track) {
    u32 instrument = REG32(player + 0x30) + next_byte(track) * 12;
    for (u32 offset = 0; offset < 12; ++offset) REG8(track + 0x24 + offset) = REG8(instrument + offset);
}

static void command_end_tie(u32 player, u32 track) {
    (void)player;
    u32 key = REG8(track + 5);
    if (REG8(REG32(track + 0x40)) < 128) REG8(track + 5) = (u8)(key = next_byte(track));
    for (u32 channel = REG32(track + 0x20); channel; channel = REG32(channel + 0x34)) {
        if (REG8(channel + 0x11) == key) REG8(channel) |= 64;
    }
}

static void command_port(u32 player, u32 track) {
    (void)player;
    u32 offset = next_byte(track), value = next_byte(track);
    REG8(IO + 0x60 + offset) = (u8)value;
}

static void track_initialize(u32 player, u32 track) {
    (void)player;
    for (u32 offset = 0; offset < 0x40; offset += 4) REG32(track + offset) = 0;
    REG8(track) = 0x80;
    REG8(track + 0xF) = 2;
    REG8(track + 0x13) = 64;
    REG8(track + 0x19) = 22;
    REG8(track + 0x24) = 1;
}

static void track_volume_pitch(u32 player, u32 track) {
    (void)player;
    u32 flags = REG8(track), type = REG8(track + 0x18);
    s32 modulation = (s8)REG8(track + 0x16);
    if (flags & 1) {
        s32 volume = (REG8(track + 0x12) * REG8(track + 0x13)) >> 5;
        if (type == 1) volume = (volume * (modulation + 128)) >> 7;
        s32 pan = (s8)REG8(track + 0x14) * 2 + (s8)REG8(track + 0x15) + 128;
        if (type == 2) pan += modulation;
        if (pan < 0) pan = 0;
        if (pan > 255) pan = 255;
        REG8(track + 0x10) = (u8)((volume * pan) >> 8);
        REG8(track + 0x11) = (u8)((volume * (255 - pan)) >> 8);
    }
    if (flags & 4) {
        s32 pitch = ((s8)REG8(track + 0xE) * REG8(track + 0xF) + (s8)REG8(track + 0xC)) * 4;
        pitch += REG8(track + 0xD);
        if (!type) pitch += modulation * 16;
        REG8(track + 8) = (u8)((s8)REG8(track + 0xA) + (s8)REG8(track + 0xB) + (pitch >> 8));
        REG8(track + 9) = (u8)pitch;
    }
    REG8(track) &= (u8)~5;
}

static u32 note_length(u32 number) {
    static const u8 extended[25] = { 24,28,30,32,36,40,42,44,48,52,54,56,60,64,66,68,72,76,78,80,84,88,90,92,96 };
    return number < 24 ? number : extended[number - 24];
}

static void note_start(u32 duration, u32 player, u32 track) {
    u32 length = note_length(duration);
    if (REG8(REG32(track + 0x40)) < 128) REG8(track + 5) = (u8)next_byte(track);
    if (REG8(REG32(track + 0x40)) < 128) REG8(track + 6) = (u8)next_byte(track);
    if (REG8(REG32(track + 0x40)) < 128) length += next_byte(track);
    REG8(track + 4) = (u8)length;
    u32 area = REG32(SOUND_AREA), selected = 0, priority = REG8(player + 9) + REG8(track + 0x1D);
    u32 instrument = track + 0x24, key = REG8(track + 5);
    if (REG8(instrument) & 0x80) {
        instrument = REG32(instrument + 4) + key * 12;
        key = REG8(instrument + 1);
        if (REG8(instrument + 3) & 128) {
            REG8(track + 0x15) = (u8)((REG8(instrument + 3) - 192) * 2);
            REG8(track) |= 3;
        }
    }
    else if (REG8(instrument) & 0x40) instrument = REG32(instrument + 4) + REG8(REG32(instrument + 8) + key) * 12;
    u32 type = REG8(instrument) & 7;
    if (type) {
        if (type > 4 || !REG32(area + 0x1C)) return;
        selected = REG32(area + 0x1C) + (type - 1) * CHANNEL_BYTES;
        if ((REG8(selected) & 0xC7) && REG8(selected + 0x13) > priority) return;
    } else {
        for (u32 index = 0; index < REG8(area + 6); ++index) {
            u32 channel = area + CHANNEL_OFFSET + index * CHANNEL_BYTES;
            if (!(REG8(channel) & 0xC7)) { selected = channel; break; }
            if (REG8(channel + 0x13) <= priority && (!selected || REG8(channel + 0x13) < REG8(selected + 0x13))) selected = channel;
        }
    }
    if (!selected) return;
    channel_unlink(selected, 0);
    for (u32 offset = 0; offset < CHANNEL_BYTES; offset += 4) REG32(selected + offset) = 0;
    REG8(selected) = 0x80;
    REG8(selected + 1) = REG8(instrument);
    REG8(selected + 0x11) = REG8(track + 5);
    REG8(selected + 0x12) = REG8(track + 6);
    REG8(selected + 0x13) = (u8)priority;
    REG8(selected + 0xC) = REG8(track + 0x1E);
    REG8(selected + 0xD) = REG8(track + 0x1F);
    for (u32 index = 0; index < 4; ++index) REG8(selected + 4 + index) = REG8(instrument + 8 + index);
    track_volume_pitch(player, track);
    s32 shifted = (s32)key + (s8)REG8(track + 8);
    REG8(selected + 8) = (u8)key;
    u32 wave = REG32(instrument + 4);
    if (type) {
        REG8(selected + 0x1E) = REG8(instrument + 2);
        REG8(selected + 0x1F) = REG8(instrument + 3);
        REG32(selected + 0x20) = ((u32 (*)(u32, u32, u32))REG32(area + 0x30))(type, shifted < 0 ? 0 : (u32)shifted, REG8(track + 9));
    } else REG32(selected + 0x20) = midi_frequency(wave, shifted < 0 ? 0 : (u32)shifted, REG8(track + 9));
    REG32(selected + 0x24) = wave;
    REG8(selected + 0x10) = (u8)length;
    REG32(selected + 0x2C) = track;
    u32 previous = REG32(track + 0x20);
    REG32(selected + 0x34) = previous;
    if (previous) REG32(previous + 0x30) = selected;
    REG32(track + 0x20) = selected;
    REG8(selected + 2) = (u8)((REG8(track + 0x10) * REG8(track + 6)) >> 7);
    REG8(selected + 3) = (u8)((REG8(track + 0x11) * REG8(track + 6)) >> 7);
    REG8(track + 0x1C) = REG8(track + 0x1B);
}

static void track_update(u32 player, u32 track) {
    if (REG8(track + 0x1C)) --REG8(track + 0x1C);
    else if (REG8(track + 0x17) && REG8(track + 0x19)) {
        u32 phase = (REG8(track + 0x1A) + REG8(track + 0x19)) & 255;
        REG8(track + 0x1A) = (u8)phase;
        s32 triangle = phase < 64 ? (s32)phase : phase < 192 ? 128 - (s32)phase : (s32)phase - 256;
        s32 modulation = (triangle * REG8(track + 0x17)) >> 6;
        if ((s8)REG8(track + 0x16) != modulation) {
            REG8(track + 0x16) = (u8)modulation;
            REG8(track) |= REG8(track + 0x18) ? 3 : 12;
        }
    }
    track_volume_pitch(player, track);
    u32 channel = REG32(track + 0x20), flags = REG8(track);
    while (channel) {
        u32 next = REG32(channel + 0x34);
        if (!REG8(channel)) channel_unlink(channel, 0);
        else {
            if (flags & 2) {
                REG8(channel + 2) = (u8)((REG8(track + 0x10) * REG8(channel + 0x12)) >> 7);
                REG8(channel + 3) = (u8)((REG8(track + 0x11) * REG8(channel + 0x12)) >> 7);
            }
            if (flags & 8) {
                s32 key = REG8(channel + 8) + (s8)REG8(track + 8);
                u32 type = REG8(channel + 1) & 7;
                if (type) {
                    u32 callback = REG32(REG32(SOUND_AREA) + 0x30);
                    REG32(channel + 0x20) = ((u32 (*)(u32, u32, u32))callback)(type, key < 0 ? 0 : (u32)key, REG8(track + 9));
                } else REG32(channel + 0x20) = midi_frequency(REG32(channel + 0x24), key < 0 ? 0 : (u32)key, REG8(track + 9));
            }
        }
        channel = next;
    }
    REG8(track) &= 0xF0;
}

static void track_tick(u32 player, u32 track) {
    if (REG8(track) & 64) track_initialize(player, track);
    u32 area = REG32(SOUND_AREA);
    for (u32 channel = REG32(track + 0x20); channel; channel = REG32(channel + 0x34)) {
        if (REG8(channel + 0x10)) {
            if (!--REG8(channel + 0x10)) REG8(channel) |= 64;
        }
    }
    while ((REG8(track) & 128) && !REG8(track + 1)) {
        u32 command = REG8(REG32(track + 0x40));
        if (command < 128) command = REG8(track + 7);
        else {
            next_byte(track);
            if (command >= 0xBD) REG8(track + 7) = (u8)command;
        }
        if (command >= 0xCF) ((void (*)(u32, u32, u32))REG32(area + 0x38))(command - 0xCF, player, track);
        else if (command >= 0xB1) ((const MusicCommand *)REG32(area + 0x34))[command - 0xB1](player, track);
        else if (command >= 0x80) REG8(track + 1) = (u8)note_length(command - 0x80);
        else { command_fine(player, track); break; }
    }
    if (REG8(track + 1)) --REG8(track + 1);
    if (REG8(track) & 128) track_update(player, track);
}

static void command_fade(u32 player, u32 track) { (void)track; fade_step(player); }
static void command_frequency(u32 value, u32 unused) { (void)unused; mode(REG32(SOUND_AREA), value & 0xF0000); }
static void command_player_initialize(u32 player, u32 track) { (void)track; player_initialize(player); }

static const MusicCommand commands[36] = {
    command_fine, command_goto, command_pattern, command_pattern_end, command_repeat,
    command_fine, command_fine, command_fine, command_fine, command_priority,
    command_tempo, command_keyshift, command_voice, command_volume, command_pan,
    command_bend, command_bend_range, command_lfo_speed, command_lfo_delay, command_modulation,
    command_modulation_type, command_fine, command_fine, command_tune, command_fine,
    command_fine, command_fine, command_port, command_fine, command_end_tie,
    command_frequency, track_stop, command_fade, track_volume_pitch,
    channel_unlink, command_player_initialize,
};

static void music_main(u32 player) {
    if (REG32(player + 0x34) != SOUND_MAGIC) return;
    u32 previous = REG32(player + 0x38);
    if (previous) ((void (*)(u32))previous)(REG32(player + 0x3C));
    if (REG32(player + 4) & 0x80000000) return;
    fade_step(player);
    u32 tempo = REG16(player + 0x22) + REG16(player + 0x20);
    while (tempo >= 150 && !(REG32(player + 4) & 0x80000000)) {
        tempo -= 150;
        u32 active = 0;
        for (u32 index = 0; index < REG8(player + 8); ++index) {
            u32 track = REG32(player + 0x2C) + index * 80;
            if (!(REG8(track) & 128)) continue;
            track_tick(player, track);
            if (REG8(track) & 128) active |= 1u << index;
        }
        REG32(player + 4) = active ? active : 0x80000000;
    }
    REG16(player + 0x22) = (u16)tempo;
}

static void initialize(u32 area) {
    if (area < 0x02000000 || area >= 0x04000000 || (area & 3)) return;
    REG16(IO + 0xC6) = REG16(IO + 0xD2) = 0;
    for (u32 offset = 0; offset < 0xFB0; offset += 4) REG32(area + offset) = 0;
    REG32(SOUND_AREA) = area;
    REG32(area) = SOUND_MAGIC;
    REG8(area + 6) = 8;
    REG8(area + 7) = 15;
    REG32(area + 0x28) = REG32(area + 0x2C) = (u32)noop;
    REG32(area + 0x30) = REG32(area + 0x3C) = (u32)noop;
    REG32(area + 0x34) = (u32)commands;
    REG32(area + 0x38) = (u32)note_start;
    REG16(IO + 0x84) = 0x80;
    REG16(IO + 0x82) = 0xA90E;
    REG16(IO + 0x88) = (REG16(IO + 0x88) & 0x3FFF) | 0x4000;
    mode(area, 0x00040000);
}

static u32 midi_frequency(u32 wave, u32 key, u32 fine) {
    /* round(2^31 * 2^(semitone/12)), generated without copying ROM data. */
    static const u32 ratios[12] = { 0x80000000u, 0x879C7C97u, 0x8FACD61Eu, 0x9837F052u, 0xA14517CCu, 0xAADC0848u, 0xB504F334u, 0xBFC886BBu, 0xCB2FF52Au, 0xD744FCCBu, 0xE411F03Au, 0xF1A1BF39u };
    if (key > 178) { key = 178; fine = 255; }
    u32 octave = puck_udiv(key, 12), note = key - octave * 12;
    u32 current = ratios[note] >> (14 - octave);
    u32 next = note == 11 ? ratios[0] >> (13 - octave) : ratios[note + 1] >> (14 - octave);
    u32 ratio = current + puck_mul_hi(next - current, (fine & 255) << 24);
    return puck_mul_hi(REG32(wave + 4), ratio);
}

static u32 envelope(u32 channel) {
    u32 status = REG8(channel), value = REG8(channel + 9);
    if (!status) return 0;
    if (status & 128) {
        u32 wave = REG32(channel + 0x24);
        REG32(channel + 0x28) = wave + 16;
        REG32(channel + 0x18) = REG32(wave + 12);
        REG32(channel + 0x1C) = 0;
        status = 3 | (REG16(wave + 2) & 0x4000 ? 16 : 0);
        value = 0;
    }
    if (status & 4) {
        if (!--REG8(channel + 0xD)) { REG8(channel) = 0; return 0; }
    } else if (status & 64) {
        u32 release = (value * REG8(channel + 7)) >> 8;
        if (release <= REG8(channel + 0xC)) {
            if (!REG8(channel + 0xC)) { REG8(channel) = 0; return 0; }
            value = REG8(channel + 0xC);
            status |= 4;
        } else value = release;
    } else if ((status & 3) == 3) {
        value += REG8(channel + 4);
        if (value >= 255) { value = 255; status = (status & ~3u) | 2; }
    } else if ((status & 3) == 2) {
        u32 decay = (value * REG8(channel + 5)) >> 8;
        if (decay <= REG8(channel + 6)) {
            if (!REG8(channel + 6)) {
                if (!REG8(channel + 0xC)) { REG8(channel) = 0; return 0; }
                value = REG8(channel + 0xC);
                status |= 4;
            } else { value = REG8(channel + 6); status = (status & ~3u) | 1; }
        } else value = decay;
    }
    REG8(channel) = (u8)status;
    REG8(channel + 9) = (u8)value;
    return value;
}

static s32 sample_channel(u32 channel, u32 rate, u32 scale) {
    if (!REG8(channel)) return 0;
    u32 wave = REG32(channel + 0x24), position = REG32(channel + 0x28) - wave - 16;
    if (wave < 0x02000000) { REG8(channel) = 0; return 0; }
    u32 length = REG32(wave + 12), fraction = REG32(channel + 0x1C), fixed = REG8(channel + 1) & 8;
    if (!fixed && fraction >= rate) {
        u32 advance = puck_udiv(fraction, rate);
        position += advance;
        fraction -= advance * rate;
    }
    if (position >= length) {
        u32 loop = REG32(wave + 8);
        if (!(REG16(wave + 2) & 0x4000) || loop >= length) { REG8(channel) = 0; return 0; }
        u32 span = length - loop, overshoot = position - length;
        position = loop + overshoot - puck_udiv(overshoot, span) * span;
    }
    s32 result = (s8)REG8(wave + 16 + position);
    if (!fixed) {
        s32 next = (s8)REG8(wave + 17 + position);
        result += ((next - result) * (s32)(fraction * scale)) >> 23;
        REG32(channel + 0x1C) = fraction + REG32(channel + 0x20);
    } else ++position;
    REG32(channel + 0x28) = wave + 16 + position;
    REG32(channel + 0x18) = position < length ? length - position : 0;
    return result;
}

static void mix(u32 area) {
    u32 channels = REG8(area + 6), count = REG32(area + 0x10), rate = REG32(area + 0x14), master = REG8(area + 7) + 1;
    if (!rate || count > PCM_CAPACITY || channels > 12) return;
    for (u32 index = 0; index < channels; ++index) {
        u32 channel = area + CHANNEL_OFFSET + index * CHANNEL_BYTES;
        u32 level = (envelope(channel) * master) >> 4;
        if (REG8(channel)) {
            REG8(channel + 0xA) = (u8)((level * REG8(channel + 2)) >> 8);
            REG8(channel + 0xB) = (u8)((level * REG8(channel + 3)) >> 8);
        }
    }
    u32 offset = (REG8(area + 0xB) - REG8(area + 4)) * count, reverb = REG8(area + 5);
    if (offset + count > PCM_CAPACITY) offset = 0;
    u32 next = offset + count;
    if (next >= REG8(area + 0xB) * count) next = 0;
    for (u32 sample = 0; sample < count; ++sample) {
        u32 output = area + PCM_OFFSET + offset + sample;
        u32 delayed = area + PCM_OFFSET + next + sample;
        s32 feedback = (s8)REG8(output) + (s8)REG8(output + PCM_CAPACITY)
            + (s8)REG8(delayed) + (s8)REG8(delayed + PCM_CAPACITY);
        s32 right = (feedback * (s32)reverb) >> 9, left = right;
        for (u32 index = 0; index < channels; ++index) {
            u32 channel = area + CHANNEL_OFFSET + index * CHANNEL_BYTES;
            s32 value = sample_channel(channel, rate, REG32(area + 0x18));
            right += (value * (s32)REG8(channel + 0xA)) >> 8;
            left += (value * (s32)REG8(channel + 0xB)) >> 8;
        }
        REG8(output) = (u8)right;
        REG8(output + PCM_CAPACITY) = (u8)left;
    }
}

void puck_sound(PuckSwiFrame *frame, u32 number) {
    if (number == 0x19) {
        u32 target = frame->r0 ? 0x200 : 0;
        u32 value = REG16(IO + 0x88), level = value & 0x3FF;
        if (frame->r0 && level >= target) return;
        while (level != target) {
            level += level < target ? 1 : (u32)-1;
            REG16(IO + 0x88) = (u16)((value & ~0x3FFu) | level);
            puck_delay(8);
        }
        return;
    }
    if (number == 0x1A) { initialize(frame->r0); return; }
    if (number == 0x1F) { frame->r0 = midi_frequency(frame->r0, frame->r1, frame->r2); return; }
    if (number == 0x29) {
        u32 area = REG32(SOUND_AREA);
        if (area >= 0x02000000 && area < 0x04000000) dma_start(area);
        return;
    }
    if (number == 0x28) {
        u32 area = REG32(SOUND_AREA);
        if (area < 0x02000000 || area >= 0x04000000) return;
        u32 identity = REG32(area);
        if (identity != SOUND_MAGIC && identity != SOUND_MAGIC + 1) return;
        REG32(area) = identity + 1;
        REG16(IO + 0xC6) = REG16(IO + 0xD2) = 0;
        REG8(area + 4) = 0;
        clear_pcm(area);
        REG32(area) = identity;
        return;
    }
    if (number == 0x2A) {
        for (u32 index = 0; index < 36; ++index) REG32(frame->r0 + index * 4) = (u32)commands[index];
        frame->r0 += 144;
        return;
    }
    u32 area = sound_area();
    if (!area) return;
    switch (number) {
    case 0x1B:
        REG32(area) = SOUND_MAGIC + 1;
        mode(area, frame->r0);
        REG32(area) = SOUND_MAGIC;
        break;
    case 0x1C:
        REG32(area) = SOUND_MAGIC + 1;
        if (REG32(area + 0x20)) ((void (*)(u32))REG32(area + 0x20))(REG32(area + 0x24));
        if (REG32(area + 0x28)) ((void (*)(u32))REG32(area + 0x28))(area);
        mix(area);
        REG32(area) = SOUND_MAGIC;
        break;
    case 0x1D:
        if (REG8(area + 4)) REG8(area + 4)--;
        if (!REG8(area + 4)) { REG8(area + 4) = REG8(area + 0xB); dma_start(area); }
        break;
    case 0x1E:
        REG32(area) = SOUND_MAGIC + 1;
        for (u32 index = 0; index < 12; ++index) REG8(area + CHANNEL_OFFSET + index * CHANNEL_BYTES) = 0;
        if (REG32(area + 0x2C)) {
            for (u32 type = 1; type <= 4; ++type) ((void (*)(u32))REG32(area + 0x2C))(type);
        }
        REG32(area) = SOUND_MAGIC;
        break;
    case 0x20: player_open(area, frame->r0, frame->r1, frame->r2); break;
    case 0x21: player_start(area, frame->r0, frame->r1); break;
    case 0x22: player_stop(frame->r0); break;
    case 0x23:
        if (REG32(frame->r0 + 0x34) == SOUND_MAGIC) REG32(frame->r0 + 4) &= 0x7FFFFFFF;
        break;
    case 0x24:
        if (REG32(frame->r0 + 0x34) == SOUND_MAGIC) {
            REG16(frame->r0 + 0x24) = REG16(frame->r0 + 0x26) = (u16)frame->r1;
            REG16(frame->r0 + 0x28) = 0x100;
        }
        break;
    default: break;
    }
}
