using PocketBoy;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

if (args.Length > 0)
{
    RunRomDiagnostic(
        args[0],
        args.Length > 1 ? int.Parse(args[1]) : 60,
        args.Length > 2 && args[2].Equals("start", StringComparison.OrdinalIgnoreCase));
    return;
}

Run("ARM instructions store results in work RAM", TestArmExecution);
Run("BIOS division service returns signed quotient", TestDivideService);
Run("VBlankIntrWait resumes on the next VBlank", TestVBlankIntrWait);
Run("VBlank invokes the registered IRQ callback", TestIrqCallback);
Run("Thumb instructions store results in work RAM", TestThumbExecution);
Run("Mode 3 renders 15-bit color", TestMode3);
Run("Mode 4 uses its palette and page", TestMode4);
Run("Mode 0 renders a tiled background", TestMode0);
Run("Mode 1 renders an affine background", TestMode1);
Run("Game Boy Advance button bits are active-low", TestButtons);
Run("Display timing updates vblank, hblank, and IRQ flags", TestDisplayTiming);
Run("Game Boy Advance timers reload and raise IRQ flags", TestTimer);

static void Run(string name, Action test)
{
    test();
    Console.WriteLine($"PASS {name}");
}

static void RunRomDiagnostic(string path, int frames, bool pressStart)
{
    byte[] rom = File.ReadAllBytes(path);
    var emulator = new GameBoyAdvance(rom);
    Console.WriteLine($"ROM: {emulator.Title} ({rom.Length:N0} bytes)");
    for (int frame = 1; frame <= frames; frame++)
    {
        if (pressStart && frame == 121)
            emulator.SetButton(GameBoyButton.Start, true);
        if (pressStart && frame == 124)
            emulator.SetButton(GameBoyButton.Start, false);
        emulator.AdvanceFrame();
        if (frame % 10 != 0 && frame != frames)
            continue;
        ReadOnlySpan<byte> pixels = emulator.Ppu.Pixels;
        var colors = new HashSet<ushort>();
        int nonzeroPixels = 0;
        for (int i = 0; i < pixels.Length; i += 2)
        {
            ushort color = (ushort)(pixels[i] | (pixels[i + 1] << 8));
            colors.Add(color);
            if (color != 0)
                nonzeroPixels++;
        }
        Console.WriteLine($"Frame {frame}: PC=0x{emulator.ProgramCounter:X8} "
            + $"mode={(emulator.DisplayControl & 7)} display=0x{emulator.DisplayControl:X4} "
            + $"thumb={emulator.ThumbMode} op=0x{emulator.ReadMemory32(emulator.ProgramCounter):X8} "
            + $"R0-3={emulator.ReadRegister(0):X8}/{emulator.ReadRegister(1):X8}/"
            + $"{emulator.ReadRegister(2):X8}/{emulator.ReadRegister(3):X8} "
            + $"dispstat={emulator.ReadIo16(0x04000004):X4} "
            + $"IE/IF={emulator.ReadIo16(0x04000200):X4}/{emulator.ReadIo16(0x04000202):X4} "
            + $"IME={emulator.ReadIo16(0x04000208):X4} "
            + $"IRQHandler={emulator.ReadMemory32(0x03007FFC):X8} "
            + $"IRQWait={emulator.ReadMemory32(0x03007FF8):X8} "
            + $"colors={colors.Count} nonzero={nonzeroPixels}");
        if (frame == 40)
        {
            uint start = emulator.ProgramCounter - 16;
            for (uint address = start; address < start + 48; address += 4)
                Console.WriteLine($"  {address:X8}: {emulator.ReadMemory32(address):X8}");
            for (uint address = 0x03003114; address < 0x03003174; address += 4)
                Console.WriteLine($"  {address:X8}: {emulator.ReadMemory32(address):X8}");
        }
    }

    string imagePath = Path.Combine(Path.GetTempPath(), "pocketboy-gba-frame.png");
    using var bitmap = new Bitmap(GbaPpu.Width, GbaPpu.Height, PixelFormat.Format32bppArgb);
    var argb = new int[GbaPpu.Width * GbaPpu.Height];
    ReadOnlySpan<byte> finalPixels = emulator.Ppu.Pixels;
    for (int i = 0; i < argb.Length; i++)
    {
        int color = finalPixels[i * 2] | (finalPixels[i * 2 + 1] << 8);
        argb[i] = Color.FromArgb(
            (color & 0x1F) * 255 / 31,
            ((color >> 5) & 0x1F) * 255 / 31,
            ((color >> 10) & 0x1F) * 255 / 31).ToArgb();
    }
    BitmapData bitmapData = bitmap.LockBits(
        new Rectangle(0, 0, GbaPpu.Width, GbaPpu.Height),
        ImageLockMode.WriteOnly,
        PixelFormat.Format32bppArgb);
    try
    {
        Marshal.Copy(argb, 0, bitmapData.Scan0, argb.Length);
    }
    finally
    {
        bitmap.UnlockBits(bitmapData);
    }
    bitmap.Save(imagePath, ImageFormat.Png);
    Console.WriteLine($"Frame capture: {imagePath}");
}

static void TestArmExecution()
{
    byte[] rom = CreateRom();
    Write32(rom, 0, 0xEA000000);
    Write32(rom, 8, 0xE3A00005);
    Write32(rom, 12, 0xE2801003);
    Write32(rom, 16, 0xE59F2008);
    Write32(rom, 20, 0xE5821000);
    Write32(rom, 24, 0xEAFFFFFE);
    Write32(rom, 32, 0x02000000);
    GbaMemory memory = new(rom);
    GbaCpu cpu = new(memory);

    for (int i = 0; i < 6; i++)
        cpu.Step();

    AssertEqual(8u, memory.Read32(0x02000000), "ARM result");
}

static void TestDivideService()
{
    byte[] rom = CreateRom();
    Write32(rom, 0, 0xEA00002E);
    Write32(rom, 0xC0, 0xE3A0002A);
    Write32(rom, 0xC4, 0xE3A01005);
    Write32(rom, 0xC8, 0xEF000006);
    Write32(rom, 0xCC, 0xE59F2004);
    Write32(rom, 0xD0, 0xE5820000);
    Write32(rom, 0xD4, 0xEAFFFFFE);
    Write32(rom, 0xD8, 0x02000000);
    GbaMemory memory = new(rom);
    GbaCpu cpu = new(memory);

    for (int i = 0; i < 6; i++)
        cpu.Step();

    AssertEqual(8u, memory.Read32(0x02000000), "BIOS division result");
}

static void TestIrqCallback()
{
    byte[] rom = CreateRom();
    Write32(rom, 0x100, 0xE59F001C);
    Write32(rom, 0x104, 0xE3A01001);
    Write32(rom, 0x108, 0xE1C010B0);
    Write32(rom, 0x10C, 0xE59F000C);
    Write32(rom, 0x110, 0xE5901000);
    Write32(rom, 0x114, 0xE2811001);
    Write32(rom, 0x118, 0xE5801000);
    Write32(rom, 0x11C, 0xE12FFF1E);
    Write32(rom, 0x120, 0x03001000);
    Write32(rom, 0x124, 0x04000202);
    GbaMemory memory = new(rom);
    GbaCpu cpu = new(memory);
    memory.Write32(0x03007FFC, 0x08000100);
    memory.Write16(0x04000200, 1);
    memory.Write16(0x04000208, 1);
    memory.Write16(0x04000004, 8);
    memory.Tick(1232 * 160);

    for (int i = 0; i < 9; i++)
        cpu.Step();

    AssertEqual(1u, memory.Read32(0x03001000), "VBlank callback count");
    AssertEqual((ushort)0, memory.PendingInterrupts, "Acknowledged VBlank interrupt");
}

static void TestVBlankIntrWait()
{
    byte[] rom = CreateRom();
    Write32(rom, 0, 0xEA000000);
    Write32(rom, 8, 0xEF000005);
    Write32(rom, 12, 0xE3A0002A);
    GbaMemory memory = new(rom);
    GbaCpu cpu = new(memory);

    cpu.Step();
    cpu.Step();
    AssertEqual(0u, cpu.ReadRegister(0), "CPU stays asleep before VBlank");

    memory.Tick(1232 * 160);
    cpu.Step();
    cpu.Step();
    AssertEqual(42u, cpu.ReadRegister(0), "CPU resumes after VBlank");
}

static void TestThumbExecution()
{
    byte[] rom = CreateRom();
    Write32(rom, 0, 0xEA000000);
    Write32(rom, 8, 0xE59F0008);
    Write32(rom, 12, 0xE12FFF10);
    Write32(rom, 24, 0x08000021);
    Write16(rom, 32, 0x212A);
    Write16(rom, 34, 0x4801);
    Write16(rom, 36, 0x6001);
    Write16(rom, 38, 0xE7FE);
    Write32(rom, 40, 0x02000000);
    GbaMemory memory = new(rom);
    GbaCpu cpu = new(memory);

    for (int i = 0; i < 7; i++)
        cpu.Step();

    AssertEqual(42u, memory.Read32(0x02000000), "Thumb result");
}

static void TestMode3()
{
    GbaMemory memory = new(CreateRom());
    GbaPpu ppu = new(memory);
    memory.Write16(0x04000000, 0x0403);
    memory.Write16(0x06000000, 0x7C00);
    ppu.RenderFrame();

    AssertEqual((byte)0x00, ppu.Pixels[0], "Mode 3 low byte");
    AssertEqual((byte)0x7C, ppu.Pixels[1], "Mode 3 high byte");
}

static void TestMode4()
{
    GbaMemory memory = new(CreateRom());
    GbaPpu ppu = new(memory);
    memory.Write16(0x04000000, 0x0404);
    memory.Write16(0x05000002, 0x001F);
    memory.Write8(0x06000000, 1);
    ppu.RenderFrame();

    AssertEqual((byte)0x1F, ppu.Pixels[0], "Mode 4 low byte");
    AssertEqual((byte)0x00, ppu.Pixels[1], "Mode 4 high byte");
}

static void TestMode0()
{
    GbaMemory memory = new(CreateRom());
    GbaPpu ppu = new(memory);
    memory.Write16(0x04000000, 0x0100);
    memory.Write16(0x04000008, 0x0100);
    memory.Write8(0x06000000, 0x01);
    memory.Write16(0x06000800, 0);
    memory.Write16(0x05000002, 0x03E0);
    AssertEqual((ushort)0x0100, memory.Read16(0x04000000), "Mode 0 display control");
    AssertEqual((byte)0x01, memory.Read8(0x06000000), "Mode 0 tile data");
    AssertEqual((ushort)0x03E0, memory.Read16(0x05000002), "Mode 0 palette");
    ppu.RenderFrame();

    AssertEqual((byte)0xE0, ppu.Pixels[0], "Mode 0 low byte");
    AssertEqual((byte)0x03, ppu.Pixels[1], "Mode 0 high byte");
}

static void TestMode1()
{
    GbaMemory memory = new(CreateRom());
    GbaPpu ppu = new(memory);
    memory.Write16(0x04000000, 0x0401);
    memory.Write16(0x0400000C, 0x0100);
    memory.Write16(0x04000020, 0x0100);
    memory.Write16(0x04000026, 0x0100);
    memory.Write16(0x05000002, 0x001F);
    memory.Write8(0x06000800, 0);
    memory.Write8(0x06000000, 1);
    ppu.RenderFrame();

    AssertEqual((byte)0x1F, ppu.Pixels[0], "Mode 1 low byte");
    AssertEqual((byte)0x00, ppu.Pixels[1], "Mode 1 high byte");
}

static void TestButtons()
{
    GbaMemory memory = new(CreateRom());
    AssertEqual((ushort)0x03FF, memory.Read16(0x04000130), "Released buttons");
    memory.SetButton(GameBoyButton.A, true);
    memory.SetButton(GameBoyButton.L, true);
    AssertEqual((ushort)0x02EF, memory.Read16(0x04000130), "Pressed buttons");
}

static void TestDisplayTiming()
{
    GbaMemory memory = new(CreateRom());
    memory.Write16(0x04000004, 0x0018);
    memory.Tick(960);
    AssertEqual(true, (memory.Read16(0x04000004) & 2) != 0, "HBlank status");
    AssertEqual(true, (memory.Read16(0x04000202) & 2) != 0, "HBlank IRQ");

    memory.Tick(1232 * 160 - 960);
    AssertEqual((ushort)160, memory.Read16(0x04000006), "VCOUNT");
    AssertEqual(true, (memory.Read16(0x04000004) & 1) != 0, "VBlank status");
    AssertEqual(true, (memory.Read16(0x04000202) & 1) != 0, "VBlank IRQ");
}

static void TestTimer()
{
    GbaMemory memory = new(CreateRom());
    memory.Write16(0x04000100, 0xFFFE);
    memory.Write16(0x04000102, 0x00C0);
    memory.Tick(2);

    AssertEqual((ushort)0xFFFE, memory.Read16(0x04000100), "Timer reload");
    AssertEqual(true, (memory.Read16(0x04000202) & 8) != 0, "Timer IRQ");
}

static byte[] CreateRom()
{
    byte[] rom = new byte[32 * 1024];
    Write32(rom, 0, 0xEA000000);
    System.Text.Encoding.ASCII.GetBytes("POCKETBOYTEST").CopyTo(rom, 0xA0);
    return rom;
}

static void Write16(byte[] data, int offset, ushort value)
{
    data[offset] = (byte)value;
    data[offset + 1] = (byte)(value >> 8);
}

static void Write32(byte[] data, int offset, uint value)
{
    Write16(data, offset, (ushort)value);
    Write16(data, offset + 2, (ushort)(value >> 16));
}

static void AssertEqual<T>(T expected, T actual, string subject)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{subject}: expected {expected}, got {actual}.");
}
