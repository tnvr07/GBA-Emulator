namespace PocketBoy;

internal sealed class Cartridge
{
    private readonly byte[] _rom;
    private readonly byte[] _ram;
    private readonly int _ramBankCount;
    private readonly int _mapper;
    private int _romBank = 1;
    private int _romBankUpper;
    private int _ramBank;
    private bool _ramEnabled;
    private bool _bankingMode;

    public string Title { get; }

    public Cartridge(byte[] rom)
    {
        if (rom.Length < 0x150)
            throw new InvalidDataException("The file is too small to be a Game Boy ROM.");

        _mapper = rom[0x147] switch
        {
            0x00 => 0,
            0x01 or 0x02 or 0x03 => 1,
            _ => throw new NotSupportedException(
                $"Cartridge type 0x{rom[0x147]:X2} is not supported. PocketBoy supports ROM-only and MBC1 cartridges.")
        };

        int declaredRomSize = rom[0x148] switch
        {
            <= 8 => 32 * 1024 << rom[0x148],
            0x52 => 72 * 16 * 1024,
            0x53 => 80 * 16 * 1024,
            0x54 => 96 * 16 * 1024,
            _ => rom.Length
        };
        if (rom.Length < declaredRomSize)
            throw new InvalidDataException("The ROM file is shorter than the size declared in its header.");

        _rom = rom;
        int ramSize = rom[0x149] switch
        {
            0 => 0,
            1 => 2 * 1024,
            2 => 8 * 1024,
            3 => 32 * 1024,
            4 => 128 * 1024,
            5 => 64 * 1024,
            _ => throw new InvalidDataException($"Unknown cartridge RAM size code 0x{rom[0x149]:X2}.")
        };
        if (_mapper == 0 && ramSize > 8 * 1024)
            throw new NotSupportedException("ROM-only cartridges with more than 8 KB of RAM are not supported.");

        _ram = new byte[ramSize];
        _ramBankCount = ramSize <= 8 * 1024 ? 1 : ramSize / (8 * 1024);
        Title = System.Text.Encoding.ASCII.GetString(rom, 0x134, 16).TrimEnd('\0', ' ');
        if (string.IsNullOrWhiteSpace(Title))
            Title = "Untitled Game";
    }

    public byte ReadRom(ushort address)
    {
        if (address < 0x4000)
        {
            int bank = _mapper == 1 && _bankingMode ? (_romBankUpper << 5) : 0;
            return _rom[(bank * 0x4000 + address) % _rom.Length];
        }

        int selectedBank = _mapper == 0
            ? 1
            : (_bankingMode ? 0 : _romBankUpper << 5) | _romBank;
        if (selectedBank == 0)
            selectedBank = 1;
        return _rom[(selectedBank * 0x4000 + address - 0x4000) % _rom.Length];
    }

    public void WriteControl(ushort address, byte value)
    {
        if (_mapper == 0)
            return;

        switch (address)
        {
            case < 0x2000:
                _ramEnabled = (value & 0x0F) == 0x0A;
                break;
            case < 0x4000:
                _romBank = value & 0x1F;
                if (_romBank == 0)
                    _romBank = 1;
                break;
            case < 0x6000:
                _romBankUpper = value & 0x03;
                _ramBank = value & 0x03;
                break;
            default:
                _bankingMode = (value & 0x01) != 0;
                break;
        }
    }

    public byte ReadRam(ushort address)
    {
        if (!_ramEnabled || _ram.Length == 0)
            return 0xFF;

        int bank = _ram.Length <= 8 * 1024 || !_bankingMode ? 0 : _ramBank % _ramBankCount;
        int offset = bank * 0x2000 + address - 0xA000;
        return offset < _ram.Length ? _ram[offset] : (byte)0xFF;
    }

    public void WriteRam(ushort address, byte value)
    {
        if (!_ramEnabled || _ram.Length == 0)
            return;

        int bank = _ram.Length <= 8 * 1024 || !_bankingMode ? 0 : _ramBank % _ramBankCount;
        int offset = bank * 0x2000 + address - 0xA000;
        if (offset < _ram.Length)
            _ram[offset] = value;
    }
}
