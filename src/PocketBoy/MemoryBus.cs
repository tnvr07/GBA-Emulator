namespace PocketBoy;

internal sealed class MemoryBus
{
    private readonly byte[] _workRam = new byte[0x2000];
    private readonly byte[] _highRam = new byte[0x7F];
    private readonly Cartridge _cartridge;
    private readonly Ppu _ppu;
    private readonly Joypad _joypad;
    private byte _interruptFlags = 0xE1;
    private byte _interruptEnable;
    private byte _divider;
    private byte _timerCounter;
    private byte _timerModulo;
    private byte _timerControl;
    private int _dividerCycles;
    private int _timerCycles;

    public Ppu Ppu => _ppu;
    public byte InterruptEnable
    {
        get => _interruptEnable;
        set => _interruptEnable = value;
    }
    public byte InterruptFlags
    {
        get => _interruptFlags;
        set => _interruptFlags = (byte)(value | 0xE0);
    }
    public Joypad Joypad => _joypad;

    public MemoryBus(Cartridge cartridge)
    {
        _cartridge = cartridge;
        _ppu = new Ppu(RequestInterrupt);
        _joypad = new Joypad(RequestInterrupt);
    }

    public byte Read(ushort address)
    {
        return address switch
        {
            < 0x8000 => _cartridge.ReadRom(address),
            < 0xA000 => _ppu.ReadVram(address),
            < 0xC000 => _cartridge.ReadRam(address),
            < 0xE000 => _workRam[address - 0xC000],
            < 0xFE00 => _workRam[address - 0xE000],
            < 0xFEA0 => _ppu.ReadOam(address),
            < 0xFF00 => 0xFF,
            0xFF00 => _joypad.Read(),
            0xFF04 => _divider,
            0xFF05 => _timerCounter,
            0xFF06 => _timerModulo,
            0xFF07 => (byte)(_timerControl | 0xF8),
            0xFF0F => _interruptFlags,
            >= 0xFF40 and <= 0xFF4B => _ppu.ReadRegister(address),
            >= 0xFF80 and <= 0xFFFE => _highRam[address - 0xFF80],
            0xFFFF => _interruptEnable,
            _ => 0xFF
        };
    }

    public void Write(ushort address, byte value)
    {
        switch (address)
        {
            case < 0x8000:
                _cartridge.WriteControl(address, value);
                break;
            case < 0xA000:
                _ppu.WriteVram(address, value);
                break;
            case < 0xC000:
                _cartridge.WriteRam(address, value);
                break;
            case < 0xE000:
                _workRam[address - 0xC000] = value;
                break;
            case < 0xFE00:
                _workRam[address - 0xE000] = value;
                break;
            case < 0xFEA0:
                _ppu.WriteOam(address, value);
                break;
            case < 0xFF00:
                break;
            case 0xFF00:
                _joypad.Write(value);
                break;
            case 0xFF04:
                _divider = 0;
                _dividerCycles = 0;
                break;
            case 0xFF05:
                _timerCounter = value;
                break;
            case 0xFF06:
                _timerModulo = value;
                break;
            case 0xFF07:
                _timerControl = (byte)(value & 0x07);
                break;
            case 0xFF0F:
                InterruptFlags = value;
                break;
            case 0xFF46:
                for (int i = 0; i < 0xA0; i++)
                    _ppu.WriteOam((ushort)(0xFE00 + i), Read((ushort)((value << 8) | i)));
                break;
            case >= 0xFF40 and <= 0xFF4B:
                _ppu.WriteRegister(address, value);
                break;
            case >= 0xFF80 and <= 0xFFFE:
                _highRam[address - 0xFF80] = value;
                break;
            case 0xFFFF:
                _interruptEnable = value;
                break;
        }
    }

    public void Tick(int cycles)
    {
        _ppu.Tick(cycles);
        _dividerCycles += cycles;
        while (_dividerCycles >= 256)
        {
            _dividerCycles -= 256;
            _divider++;
        }

        if ((_timerControl & 0x04) == 0)
            return;

        int period = (_timerControl & 0x03) switch
        {
            0 => 1024,
            1 => 16,
            2 => 64,
            _ => 256
        };
        _timerCycles += cycles;
        while (_timerCycles >= period)
        {
            _timerCycles -= period;
            _timerCounter++;
            if (_timerCounter == 0)
            {
                _timerCounter = _timerModulo;
                RequestInterrupt(2);
            }
        }
    }

    public void RequestInterrupt(int bit) => _interruptFlags |= (byte)(1 << bit);
}
