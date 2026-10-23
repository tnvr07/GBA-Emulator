# PocketBoy

PocketBoy is a Windows desktop emulator starter built with C# and Windows Forms. It accepts original Game Boy `.gb`/`.gbc` cartridges and Game Boy Advance `.gba` ROMs.

## Run

Install the .NET 8 SDK on Windows, then run:

```powershell
dotnet run --project src\PocketBoy\PocketBoy.csproj
```

You can pass a ROM path to load it when the app starts:

```powershell
dotnet run --project src\PocketBoy\PocketBoy.csproj -- "C:\Games\game.gba"
```

To publish a standalone 64-bit Windows app that includes its .NET runtime:

```powershell
dotnet publish src\PocketBoy\PocketBoy.csproj --configuration Release --runtime win-x64 --self-contained true
```

The executable is written to `src\PocketBoy\bin\Release\net8.0-windows\win-x64\publish\PocketBoy.exe`.

Use **Open ROM** to load a ROM file. Controls are:

| Game Boy | Keyboard |
| --- | --- |
| D-pad | Arrow keys (or WASD) |
| A | Z or Space |
| B | X |
| Start | Enter |
| Select | Backspace |
| L / R (GBA) | Q / E |
| Pause / resume | Esc |

## Current support

- LR35902 CPU base and CB-prefixed instruction sets
- ROM-only and MBC1 cartridges, including cartridge RAM while the game is running
- DMG memory map, interrupts, timer, joypad, OAM DMA, and background/window/sprite rendering
- Original Game Boy palette and frame timing
- Early GBA ARM/Thumb CPU, memory map, joypad, display timing, timers, VBlank waits/IRQ callbacks, tiled/affine backgrounds, sprites, and bitmap modes 3-5
- Basic GBA software-interrupt emulation for waits, arithmetic, memory copy/fill, and LZ77/RLE decompression

This is an early emulator implementation, not a compatibility-certified replacement for dedicated emulators. GBA support is experimental and does not include the original GBA BIOS; its built-in BIOS-service replacements and hardware interrupt handling are incomplete. Audio, advanced sprite effects, and many other hardware features are also missing, so many commercial games will not run correctly yet. Game Boy support has no audio, color (CGB) rendering, save-RAM persistence, or save states, and supports only ROM-only and MBC1 cartridges. Use ROMs you are legally entitled to use; no ROMs are included.

Run the focused GBA core checks on Windows with the .NET 8 SDK:

```powershell
dotnet run --project tests\PocketBoy.Tests\PocketBoy.Tests.csproj
```
