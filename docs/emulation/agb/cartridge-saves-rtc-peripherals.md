# Cartridge, saves, RTC, and peripherals

## Backup selection

Cartridges commonly identify SRAM, EEPROM, or Flash through marker strings, but
some contain conflicting or misleading markers. `AgbGameOverrides` keys
exceptions by header game code. The override database takes precedence and the
string scan remains the fallback.

## EEPROM

EEPROM supports 512-byte and 8 KiB address widths over its serial command
protocol. Access is associated with the Game Pak bus and commonly driven by DMA.
Address-width selection, command bit count, ready timing, and upper-address
mapping are snapshot state and should be tested with both backup sizes.

## Flash

Flash supports 64 KiB and banked 128 KiB capacities with command unlock,
identification, erase, program, and bank-select operations. Capacity variants
share one protocol implementation with explicit device geometry.

## RTC

The S-3511A RTC is a serial GPIO device in the cartridge address window. Puck
derives time from emulated cycles and a deterministic epoch. A live local-time
sample may enter only as a recorded command that replay can reproduce.

## GPIO sensors

Solar, tilt, gyro, and rumble peripherals reuse cartridge GPIO or auxiliary
protocols. Add them per supported cartridge. Sensor values cross a recordable
input boundary; protocol state and latched readings belong in snapshots.

## Compatibility cartridges

Classic NES cartridges combine backup overrides, unusual ROM access, code
execution patterns, and narrow FIFO writes. Keep these concerns in their owning
subsystems. Do not introduce game-specific CPU or bus branches when a cartridge
override or general hardware rule explains the behavior.

## ROM mirroring

An undersized ROM may mirror within its physical addressable range while other
addresses expose open bus. Establish the cartridge-size and upper-address rules
with a focused ROM before changing modulo mapping.

## STOP and unsupported peripherals

STOP is a machine power mode, not a cartridge special case. Its display and
audio behavior belongs in the bus, PPU, and APU models.

Peripheral families without a supported content path, including e-Reader and
Play-Yan, remain outside the current scope. RFU behavior should be added only as
a deterministic link/peripheral protocol with dedicated evidence.

## Sources

- [GBATEK cartridge backup](https://problemkaputt.de/gbatek-gba-cartridge-backup.htm)
- [GBATEK cartridge GPIO](https://problemkaputt.de/gbatek-gba-cartridge-gpio-port.htm)
- [mGBA game overrides](https://github.com/mgba-emu/mgba/tree/master/src/gba)
