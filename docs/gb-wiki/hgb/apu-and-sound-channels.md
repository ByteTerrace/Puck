# APU & Sound Channels

The **Audio Processing Unit (APU)** synthesizes 4 independent sound channels. It is driven by a hardware **Frame Sequencer** clocked at 512 Hz, providing retro stereo sound with frequency sweeps, volume envelopes, and customizable wave patterns.

---

## The 4 Sound Channels

```mermaid
flowchart TD
    subgraph Channels
        CH1[Channel 1: Square with Sweep]
        CH2[Channel 2: Square]
        CH3[Channel 3: 4-Bit Wave RAM]
        CH4[Channel 4: Noise LFSR]
    end

    subgraph Frame Sequencer (512 Hz)
        FS[Frame Sequencer] -->|256 Hz| Len[Length Counters]
        FS -->|128 Hz| Swp[Frequency Sweep]
        FS -->|64 Hz| Env[Volume Envelopes]
    end

    Channels --> Routing[Channel Stereo Matrix (NR51)]
    Routing --> MasterVol[Master Volume Control (NR50)]
    MasterVol --> Out[Stereo Output Buffers (L/R)]
```

### 1. Channel 1 — Square Wave with Sweep (`0xFF10–0xFF14`)
- **Duty Cycle Patterns**: Generated via an 8-step duty cycle counter:
  - `00`: 12.5% (`_-------`)
  - `01`: 25.0% (`__------`)
  - `10`: 50.0% (`____----`)
  - `11`: 75.0% (`______--`)
- **Frequency Sweep**: Automatically steps the 11-bit frequency value:
  $$f_{\text{new}} = f_{\text{old}} \pm \left\lfloor \frac{f_{\text{old}}}{2^n} \right\rfloor$$
  where $n$ is the sweep shift count (0–7). If $f_{\text{new}} > 2047$, the channel immediately disables (overflow cutoff).
- **Volume Envelope**: 15 volume steps with programmable direction (fade in or fade out) and step length.
- **Length Counter**: 64 steps (64 to 0, ticked at 256 Hz).

### 2. Channel 2 — Square Wave (`0xFF16–0xFF19`)
Identical in tone, duty cycle, volume envelope, and length behavior to Channel 1, but lacks hardware frequency sweep.

### 3. Channel 3 — Custom Wave Output (`0xFF1A–0xFF1E`)
Plays arbitrary 4-bit digitized waveforms stored in internal **Wave RAM** (`0xFF30–0xFF3F`, 16 bytes containing 32 4-bit samples):
- **Output Volume Shifts**:
  - `00`: Mute
  - `01`: 100% volume (unmodified sample)
  - `10`: 50% volume (sample shifted right by 1 bit)
  - `11`: 25% volume (sample shifted right by 2 bits)
- **Length Counter**: 256 steps (ticked at 256 Hz).
- **Wave RAM Access Corruption**: On physical DMG hardware, accessing Wave RAM while Channel 3 is active triggers bus conflicts that corrupt the first few bytes of Wave RAM. Puck's `ApuComponent.cs` replicates this exact corruption behavior.

### 4. Channel 4 — Pseudo-Random Noise (`0xFF20–0xFF23`)
Generates white noise using a **Linear Feedback Shift Register (LFSR)**:
- **Shift Modes**:
  - **15-bit LFSR**: Long pseudo-random sequence producing smooth white noise (explosions, surf, percussion).
  - **7-bit LFSR**: Short sequence (127 states) producing metallic, buzzy, retro synthesizer tones.
- **Clock Divider**: Configured via clock divider ratio $r$ and clock shift $s$:
  $$\text{Frequency} = \frac{524,288\text{ Hz}}{r \times 2^{s+1}}$$
- Includes programmable volume envelope and 64-step length counter.

---

## The Frame Sequencer (512 Hz)

The APU's internal envelope and length timing is decoupled from CPU instruction cycles, driven instead by a 512 Hz divider stepped from bit 12 of the CPU timer divider (`DIV`):

| Step | Frequency | Components Ticked |
|---|---|---|
| **0** | 512 Hz | Length Counters |
| **1** | 256 Hz | — |
| **2** | 128 Hz | Length Counters, Frequency Sweep |
| **3** | — | — |
| **4** | 512 Hz | Length Counters |
| **5** | — | — |
| **6** | 128 Hz | Length Counters, Frequency Sweep |
| **7** | 64 Hz | Volume Envelopes |

---

## Stereo Panning & Mixing

Audio mixing is controlled by two central registers:

- **`NR50` (`0xFF24`) — Channel Volume & Vin**:
  - Bits 0–2: Right terminal master volume (0–7).
  - Bits 4–6: Left terminal master volume (0–7).
  - Bits 3 & 7: Cartridge audio input lines (`Vin`).
- **`NR51` (`0xFF25`) — Sound Panning Matrix**:
  - Each of the 4 channels has individual enable bits routing its output to the Left terminal, Right terminal, both (center), or neither (disabled).
- **`NR52` (`0xFF26`) — Master Sound Enable**:
  - Bit 7 toggles the APU master power. Disabling `NR52` clears all internal APU registers and zeroes power consumption.

---

## Sample Rate & DC Offset Removal

Puck's `AudioOutputComponent` converts the discrete digital channel samples into a linear PCM stream:
1. **High-Pass Filter**: Removes DC offset drift caused by uncentered square waves.
2. **Resampling Ring Buffer**: Collects samples for `WorldAudioDirector` spatial emission, holding continuous synchronization with video frame rendering without buffer underruns.
