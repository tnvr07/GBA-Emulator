# PocketBoy

PocketBoy is a Windows desktop Game Boy emulator starter built with C# and Windows Forms. It runs original Game Boy cartridges in DMG mode and accepts `.gb` and `.gbc` ROM files.

## Run

Install the .NET 8 SDK on Windows, then run:

```powershell
dotnet run --project src\PocketBoy\PocketBoy.csproj
```

Use **Open ROM** to load a ROM file. Controls are:

| Game Boy | Keyboard |
| --- | --- |
| D-pad | Arrow keys (or WASD) |
| A | Z |
| B | X |
| Start | Enter |
| Select | Backspace |
| Pause / resume | Esc |

## Current support

- LR35902 CPU base and CB-prefixed instruction sets
- ROM-only and MBC1 cartridges, including cartridge RAM while the game is running
- DMG memory map, interrupts, timer, joypad, OAM DMA, and background/window/sprite rendering
- Original Game Boy palette and frame timing

This is an early emulator implementation, not a compatibility-certified replacement for dedicated emulators. It currently has no audio, color (CGB) rendering, save-RAM persistence, save states, or support for cartridge mappers other than ROM-only and MBC1. Games that rely on those features may not run correctly. Use ROMs you are legally entitled to use; no ROMs are included.
