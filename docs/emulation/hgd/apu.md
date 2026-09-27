# Audio

The 2A03's audio processing unit has five channels: two pulse waves, a triangle,
a noise generator, and the delta-modulation channel (DMC), which plays 1-bit
delta samples from memory. The Deck clocks the APU once per CPU cycle. Its
emulated output is five integer levels: each pulse channel, the triangle and the
noise channel produce 0 to 15, and the DMC produces 0 to 127. Those integers are
what snapshots hold and what the battery checks. Turning them into sound is
presentation, described at the end of this page.
[NESdev's APU page](https://www.nesdev.org/wiki/APU) is the reference.

## Channels

A pulse channel steps an eight-step duty sequence, 12.5%, 25%, 50% or 75% high,
once each time its 11-bit timer expires. The timer counts APU cycles, which are
every other CPU cycle. An envelope supplies the volume, either constant or a
decay from 15 that the quarter-frame clock steps and can loop. A sweep unit can
move the period up or down on half-frame clocks, and it mutes the channel when
the period is below 8 or the target would exceed $7FF. The two pulses negate a
downward sweep differently: pulse 1 with the ones' complement and pulse 2 with
the two's complement.

The triangle steps a 32-step ramp every CPU cycle its timer expires. It advances
only while both its length counter and its linear counter are nonzero, and when
either stops it holds its level rather than dropping to zero.

The noise channel shifts a 15-bit feedback register at one of sixteen NTSC
periods and outputs its envelope volume when bit 0 is clear. Its mode bit taps
bit 6 instead of bit 1, which gives the short, metallic sequence.

The DMC outputs a 7-bit level that each sample bit raises or lowers by 2. When
its one-byte sample buffer is empty and bytes remain, it asks the
[DMA unit](input-and-dma.md#dma) for the next byte. It can loop the sample, and
it can raise an interrupt when the last byte has been fetched.

## Frame counter and interrupts

The frame counter clocks the envelopes and the triangle's linear counter on
quarter frames, and the length counters and sweeps on half frames. In four-step
mode those clocks land on CPU cycles 7457, 14913, 22371 and 29829 of a
29,830-cycle sequence, and the frame interrupt flag is raised on cycles 29828
to 29830 unless $4017 inhibits it. Five-step mode runs 37,282 cycles, never
interrupts, and clocks both units at once when it is selected. A $4017 write
restarts the sequence three CPU cycles later on one cycle parity and four on
the other.

$4015 enables the channels, and a read of it reports which length counters are
running, whether DMC bytes remain, and both interrupt flags; the read clears
the frame interrupt. Because that read is internal to the 2A03, it leaves the
CPU data bus as it was, and bit 5 reads back open bus. The CPU's IRQ line
combines the APU's two interrupts with the cartridge's.

## From levels to sound

`HgdAudioOutput` is the presentation stage. While a host has configured an
output rate, it mixes the five levels every CPU cycle through the APU's
nonlinear mixer, which the
[NESdev mixer page](https://www.nesdev.org/wiki/APU_Mixer) gives as two lookup
tables. It averages the mix over each output sample's span of CPU cycles, keeping
the exact rational ratio between the 19,687,500/11 Hz CPU clock and the output
rate, and removes the DC offset with a 90 Hz high-pass like the console's. It
queues interleaved stereo samples for the host to drain. With no rate
configured it does no work, and nothing it computes feeds back into the machine.

## Not yet modelled

The DMC's separate load and reload DMA timings, the pulse channels' exact phase
reset on a $4003 or $4007 write, and the PAL tables belong to the accuracy and
regional packages. Band-limited synthesis would replace the averaging when
presentation quality needs it.

[Humble Gaming Deck](README.md) · [Picture processing](ppu.md) · [Input and DMA](input-and-dma.md)
