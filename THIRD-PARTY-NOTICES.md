# Third-party notices

## Essgee

- Author: xdaniel (Daniel R.).
- Source: https://github.com/xdanieldzd/Essgee
- Vendored revision: `16e8ffdf1b44f00fbf21d2d97130c260e887dff2`.
- License: MIT, copyright (c) 2019 xdaniel (Daniel R.).
- Files: `src/Zafiro.MasterSystem.Debug.Emulator/Vendor/Essgee/`.
- Complete license: `src/Zafiro.MasterSystem.Debug.Emulator/Vendor/Essgee/LICENSE.txt`, also packed
  as `ESSGEE-LICENSE.txt` in the .NET tool package.

Only the Z80 CPU, SMS/GG video/audio chips, Sega/Codemasters cartridges, their
interfaces, event arguments, emulation helpers and state attribute are vendored.
Essgee's desktop frontend, assets, OpenTK and Windows Forms are excluded.

Adaptations:

- Disable nullable analysis for original pre-nullability code; remove desktop
  CPU logging, UI enum attributes and obsolete exception serialization.
- Deterministic VDP reset; clear video buffers and interrupt line on reset.
- Allocate video buffers after timing/resolution configuration to support PAL.
- Return from interrupt entry before fetching an opcode; correct IM0/IM1 entry
  timing, implement the IM2 vector and release HALT before NMI entry.
- Preserve the half VDP-cycle in the machine scheduler across Z80 instructions.
- Use fractional PSG sample phase across instruction/frame boundaries; wait for
  valid timing configuration; reset channel counters/output and Sega noise LFSR.
- Add partial-class debugger accessors for registers, memory, mapper banks,
  side-effect-free VDP state/framebuffer reads and sample observation.
- Derive bank masks from the supplied ROM size and mask startup banks so short
  ROMs mirror the cartridge's available pages correctly.

## emu2413

- Author: Mitsutaka Okazaki.
- Source: https://github.com/digital-sound-antiques/emu2413
- Ported version: v1.5.9, commit `11676f6c43af7a53a0a940f8faea57eed73a22ba`.
- License: MIT, copyright (C) 2001-2019 Mitsutaka Okazaki.
- Files: `src/Zafiro.MasterSystem.Debug.Emulator/Vendor/Emu2413/`.
- Complete license: `src/Zafiro.MasterSystem.Debug.Emulator/Vendor/Emu2413/LICENSE.txt`, also packed
  as `EMU2413-LICENSE.txt` in the .NET tool package.

The YM2413 synthesizer, its tables and its sinc rate converter were translated to C#.
Slot/patch pointers became indices so all chip state lives in snapshot-friendly
fields. Only the YM2413 tone set is included; VRC7/YMF281B presets, stereo panning
and debug printing are omitted. A regression test compares the port's output with
the C original bit for bit.

## Zafiro.Nes.Debug

The PNG encoder was adapted from the user's `Zafiro.Nes.Debug` project (formerly
`NesMcp`, `src/Nes.Debug.Core/PngEncoder.cs`), under its MIT license. Copyright (c)
José Manuel Nieto (@SuperJMN). Namespace adapted for Zafiro.MasterSystem.Debug.

No commercial ROMs or ROM-derived graphics/audio are distributed with the source
or package. Qualification artifacts remain local and ignored by Git.
