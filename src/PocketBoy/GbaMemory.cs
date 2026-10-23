namespace PocketBoy;

internal sealed class GbaMemory
{
    private readonly byte[] _rom;
    private readonly byte[] _externalRam = new byte[32 * 1024];
    private readonly byte[] _workRam = new byte[256 * 1024];
    private readonly byte[] _internalRam = new byte[32 * 1024];
    private readonly byte[] _io = new byte[0x400];
    private readonly byte[] _palette = new byte[0x400];
    private readonly byte[] _videoRam = new byte[0x18000];
    private readonly byte[] _oam = new byte[0x400];
    private readonly bool[] _buttons = new bool[10];
    private readonly int[] _timerCycles = new int[4];
    private readonly ushort[] _timerReload = new ushort[4];
    private int _ppuCycles;
    private int _scanline;
    private int _scanlineDot;

    public string Title { get; }
    public ReadOnlySpan<byte> Io => _io;
    public ReadOnlySpan<byte> Palette => _palette;
    public ReadOnlySpan<byte> VideoRam => _videoRam;
    public ReadOnlySpan<byte> Oam => _oam;
    public ushort DisplayControl => (ushort)(_io[0] | (_io[1] << 8));
    public ushort EnabledInterrupts => (ushort)(_io[0x200] | (_io[0x201] << 8));
    public ushort PendingInterrupts => (ushort)(_io[0x202] | (_io[0x203] << 8));
    public bool InterruptsEnabled => (_io[0x208] & 1) != 0;

    public GbaMemory(byte[] rom)
    {
        if (rom.Length < 0xC0)
            throw new InvalidDataException("The file is too small to be a Game Boy Advance ROM.");
        if (rom[3] != 0xEA)
            throw new InvalidDataException("This does not look like a Game Boy Advance ROM (the ARM entry point is missing).");

        _rom = rom;
        Title = System.Text.Encoding.ASCII.GetString(rom, 0xA0, 12).TrimEnd('\0', ' ');
        if (string.IsNullOrWhiteSpace(Title))
            Title = "Untitled GBA Game";
        _io[0x130] = 0xFF;
        _io[0x131] = 0x03;
        _io[0x300] = 1;
        _io[0x000] = 0x80;
        UpdateVCountMatch();
    }

    public byte Read8(uint address)
    {
        if (address < 0x00004000)
            return 0;
        if (address is >= 0x02000000 and < 0x02040000)
            return _workRam[(address - 0x02000000) & 0x3FFFF];
        if (address is >= 0x03000000 and < 0x03008000)
            return _internalRam[(address - 0x03000000) & 0x7FFF];
        if (address is >= 0x04000000 and < 0x04000400)
            return ReadIo8((int)(address - 0x04000000));
        if (address is >= 0x05000000 and < 0x05000400)
            return _palette[(address - 0x05000000) & 0x3FF];
        if (address is >= 0x06000000 and < 0x06018000)
            return _videoRam[NormalizeVramOffset(address - 0x06000000)];
        if (address is >= 0x07000000 and < 0x07000400)
            return _oam[(address - 0x07000000) & 0x3FF];
        if (address is >= 0x08000000 and < 0x0E000000)
            return _rom[(int)((address - 0x08000000) % (uint)_rom.Length)];
        if (address is >= 0x0E000000 and < 0x0E010000)
            return _externalRam[(address - 0x0E000000) & 0x7FFF];
        return 0;
    }

    public ushort Read16(uint address)
    {
        address &= ~1u;
        return (ushort)(Read8(address) | (Read8(address + 1) << 8));
    }

    public uint Read32(uint address)
    {
        address &= ~3u;
        return (uint)(Read8(address) | (Read8(address + 1) << 8)
            | (Read8(address + 2) << 16) | (Read8(address + 3) << 24));
    }

    public void Write8(uint address, byte value)
    {
        if (address is >= 0x02000000 and < 0x02040000)
            _workRam[(address - 0x02000000) & 0x3FFFF] = value;
        else if (address is >= 0x03000000 and < 0x03008000)
            _internalRam[(address - 0x03000000) & 0x7FFF] = value;
        else if (address is >= 0x04000000 and < 0x04000400)
            WriteIo8((int)(address - 0x04000000), value);
        else if (address is >= 0x05000000 and < 0x05000400)
            _palette[(address - 0x05000000) & 0x3FF] = value;
        else if (address is >= 0x06000000 and < 0x06018000)
            _videoRam[NormalizeVramOffset(address - 0x06000000)] = value;
        else if (address is >= 0x07000000 and < 0x07000400)
            _oam[(address - 0x07000000) & 0x3FF] = value;
        else if (address is >= 0x0E000000 and < 0x0E010000)
            _externalRam[(address - 0x0E000000) & 0x7FFF] = value;
    }

    public void Write16(uint address, ushort value)
    {
        address &= ~1u;
        Write8(address, (byte)value);
        Write8(address + 1, (byte)(value >> 8));
    }

    public void Write32(uint address, uint value)
    {
        address &= ~3u;
        Write8(address, (byte)value);
        Write8(address + 1, (byte)(value >> 8));
        Write8(address + 2, (byte)(value >> 16));
        Write8(address + 3, (byte)(value >> 24));
    }

    public void SetButton(GameBoyButton button, bool pressed)
    {
        int index = button switch
        {
            GameBoyButton.A => 0,
            GameBoyButton.B => 1,
            GameBoyButton.Select => 2,
            GameBoyButton.Start => 3,
            GameBoyButton.Right => 4,
            GameBoyButton.Left => 5,
            GameBoyButton.Up => 6,
            GameBoyButton.Down => 7,
            GameBoyButton.R => 8,
            GameBoyButton.L => 9,
            _ => -1
        };
        if (index < 0)
            return;
        _buttons[index] = pressed;
        ushort keys = 0x03FF;
        for (int i = 0; i < _buttons.Length; i++)
        {
            if (_buttons[i])
                keys &= (ushort)~(1 << i);
        }
        _io[0x130] = (byte)keys;
        _io[0x131] = (byte)(keys >> 8);
    }

    public void Tick(int cycles)
    {
        _scanlineDot += cycles;
        if (_scanlineDot < 960)
            SetHBlank(false);
        else
            SetHBlank(true);

        _ppuCycles += cycles;
        while (_ppuCycles >= 1232)
        {
            _ppuCycles -= 1232;
            _scanlineDot -= 1232;
            _scanline = (_scanline + 1) % 228;
            _io[0x006] = (byte)_scanline;
            _io[0x007] = (byte)(_scanline >> 8);
            bool vblank = _scanline >= 160;
            bool wasVblank = (_io[0x004] & 1) != 0;
            _io[0x004] = vblank ? (byte)(_io[0x004] | 1) : (byte)(_io[0x004] & ~1);
            if (vblank && !wasVblank && (_io[0x004] & 8) != 0)
                RequestInterrupt(0);
            UpdateVCountMatch();
            SetHBlank(false);
        }

        TickTimers(cycles);
    }

    public void ClearInterruptFlags(ushort mask)
    {
        Write16(0x04000202, mask);
        uint waitFlags = Read32(0x03007FF8) & ~(uint)mask;
        Write32(0x03007FF8, waitFlags);
    }

    private byte ReadIo8(int offset) => _io[offset];

    private void WriteIo8(int offset, byte value)
    {
        if (offset is 0x202 or 0x203)
        {
            _io[offset] &= (byte)~value;
            return;
        }
        if (offset is 0x004 or 0x005)
        {
            _io[offset] = offset == 0x004
                ? (byte)((_io[offset] & 7) | (value & 0x78))
                : value;
            UpdateVCountMatch();
            return;
        }
        if (offset is 0x006 or 0x007)
            return;

        _io[offset] = value;
        if (offset is 0x005 or 0x007)
            UpdateVCountMatch();
        if (offset is >= 0x100 and < 0x110 && (offset & 3) < 2)
        {
            int timer = (offset - 0x100) / 4;
            _timerReload[timer] = (ushort)(_io[0x100 + timer * 4] | (_io[0x101 + timer * 4] << 8));
        }
        if (offset is >= 0x102 and < 0x110 && (offset & 3) == 3)
        {
            int timer = (offset - 0x100) / 4;
            if ((_io[offset] & 0x80) != 0)
                _timerCycles[timer] = 0;
        }
        if (offset is >= 0x0BA and < 0x0E0 && (offset - 0x0B0) % 12 == 11)
        {
            if ((value & 0x80) != 0)
                ExecuteDma((offset - 0x0B0) / 12);
        }
    }

    private void SetHBlank(bool enabled)
    {
        bool wasEnabled = (_io[0x004] & 2) != 0;
        _io[0x004] = enabled ? (byte)(_io[0x004] | 2) : (byte)(_io[0x004] & ~2);
        if (enabled && !wasEnabled && (_io[0x004] & 0x10) != 0)
            RequestInterrupt(1);
    }

    private void UpdateVCountMatch()
    {
        bool matches = _scanline == _io[0x005];
        bool wasMatching = (_io[0x004] & 4) != 0;
        _io[0x004] = matches ? (byte)(_io[0x004] | 4) : (byte)(_io[0x004] & ~4);
        if (matches && !wasMatching && (_io[0x004] & 0x20) != 0)
            RequestInterrupt(2);
    }

    private void TickTimers(int cycles)
    {
        int previousOverflowCount = 0;
        for (int timer = 0; timer < 4; timer++)
        {
            int offset = 0x100 + timer * 4;
            ushort control = (ushort)(_io[offset + 2] | (_io[offset + 3] << 8));
            if ((control & 0x80) == 0)
            {
                previousOverflowCount = 0;
                continue;
            }

            int increments;
            if (timer > 0 && (control & 4) != 0)
            {
                increments = previousOverflowCount;
            }
            else
            {
                int period = (control & 3) switch { 0 => 1, 1 => 64, 2 => 256, _ => 1024 };
                _timerCycles[timer] += cycles;
                increments = _timerCycles[timer] / period;
                _timerCycles[timer] %= period;
            }

            int overflowCount = 0;
            for (int i = 0; i < increments; i++)
            {
                ushort count = (ushort)(_io[offset] | (_io[offset + 1] << 8));
                count++;
                if (count == 0)
                {
                    count = _timerReload[timer];
                    overflowCount++;
                    if ((control & 0x40) != 0)
                        RequestInterrupt(3 + timer);
                }
                _io[offset] = (byte)count;
                _io[offset + 1] = (byte)(count >> 8);
            }
            previousOverflowCount = overflowCount;
        }
    }

    private void RequestInterrupt(int bit)
    {
        ushort flags = (ushort)(_io[0x202] | (_io[0x203] << 8));
        flags |= (ushort)(1 << bit);
        _io[0x202] = (byte)flags;
        _io[0x203] = (byte)(flags >> 8);
        uint biosFlags = Read32(0x03007FF8) | (1u << bit);
        Write32(0x03007FF8, biosFlags);
    }

    private void ExecuteDma(int channel)
    {
        int offset = 0x0B0 + channel * 12;
        uint source = (uint)(_io[offset] | (_io[offset + 1] << 8)
            | (_io[offset + 2] << 16) | (_io[offset + 3] << 24));
        uint destination = (uint)(_io[offset + 4] | (_io[offset + 5] << 8)
            | (_io[offset + 6] << 16) | (_io[offset + 7] << 24));
        int count = _io[offset + 8] | (_io[offset + 9] << 8);
        ushort control = (ushort)(_io[offset + 10] | (_io[offset + 11] << 8));
        if ((control & 0x3000) != 0)
            return;
        if (count == 0)
            count = channel == 3 ? 0x10000 : 0x4000;

        bool word = (control & 0x0400) != 0;
        int increment = word ? 4 : 2;
        source &= word ? ~3u : ~1u;
        destination &= word ? ~3u : ~1u;
        int destinationMode = (control >> 5) & 3;
        for (int i = 0; i < count; i++)
        {
            if (word)
                Write32(destination, Read32(source));
            else
                Write16(destination, Read16(source));

            source += (control & 0x0800) != 0 ? unchecked((uint)-increment) : (uint)increment;
            if (destinationMode == 0)
                destination += (uint)increment;
            else if (destinationMode == 1)
                destination += unchecked((uint)-increment);
        }

        _io[offset + 10] &= 0x7F;
        _io[offset + 11] &= 0x7F;
    }

    private static int NormalizeVramOffset(uint offset)
    {
        int normalized = (int)(offset & 0x1FFFF);
        return normalized >= 0x18000 ? normalized - 0x8000 : normalized;
    }
}
