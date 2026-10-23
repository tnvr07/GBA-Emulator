namespace PocketBoy;

internal sealed class Ppu
{
    public const int Width = 160;
    public const int Height = 144;
    private static readonly Color[] Shades =
    [
        Color.FromArgb(224, 248, 208),
        Color.FromArgb(136, 192, 112),
        Color.FromArgb(52, 104, 86),
        Color.FromArgb(8, 24, 32)
    ];

    private readonly byte[] _vram = new byte[0x2000];
    private readonly byte[] _oam = new byte[0xA0];
    private readonly byte[] _pixels = new byte[Width * Height];
    private readonly Action<int> _requestInterrupt;
    private byte _lcdc = 0x91;
    private byte _stat = 0x86;
    private byte _scy;
    private byte _scx;
    private byte _ly;
    private byte _lyc;
    private byte _dma;
    private byte _bgp = 0xFC;
    private byte _obp0 = 0xFF;
    private byte _obp1 = 0xFF;
    private byte _wy;
    private byte _wx;
    private int _dot;
    private bool _statInterruptLine;

    public Ppu(Action<int> requestInterrupt) => _requestInterrupt = requestInterrupt;

    public byte[] Pixels => _pixels;
    public bool LcdEnabled => (_lcdc & 0x80) != 0;

    public byte ReadVram(ushort address) => _vram[address - 0x8000];
    public void WriteVram(ushort address, byte value) => _vram[address - 0x8000] = value;
    public byte ReadOam(ushort address) => _oam[address - 0xFE00];
    public void WriteOam(ushort address, byte value) => _oam[address - 0xFE00] = value;

    public byte ReadRegister(ushort address) => address switch
    {
        0xFF40 => _lcdc,
        0xFF41 => (byte)(_stat | 0x80),
        0xFF42 => _scy,
        0xFF43 => _scx,
        0xFF44 => _ly,
        0xFF45 => _lyc,
        0xFF46 => _dma,
        0xFF47 => _bgp,
        0xFF48 => _obp0,
        0xFF49 => _obp1,
        0xFF4A => _wy,
        0xFF4B => _wx,
        _ => 0xFF
    };

    public void WriteRegister(ushort address, byte value)
    {
        switch (address)
        {
            case 0xFF40:
                bool wasEnabled = LcdEnabled;
                _lcdc = value;
                if (wasEnabled && !LcdEnabled)
                {
                    _ly = 0;
                    _dot = 0;
                    SetMode(0);
                }
                break;
            case 0xFF41:
                _stat = (byte)((_stat & 0x07) | (value & 0x78));
                UpdateStatInterrupt();
                break;
            case 0xFF42: _scy = value; break;
            case 0xFF43: _scx = value; break;
            case 0xFF44: break;
            case 0xFF45:
                _lyc = value;
                UpdateCoincidence();
                break;
            case 0xFF46: _dma = value; break;
            case 0xFF47: _bgp = value; break;
            case 0xFF48: _obp0 = value; break;
            case 0xFF49: _obp1 = value; break;
            case 0xFF4A: _wy = value; break;
            case 0xFF4B: _wx = value; break;
        }
    }

    public void Tick(int cycles)
    {
        if (!LcdEnabled)
            return;

        _dot += cycles;
        while (_dot >= 456)
        {
            _dot -= 456;
            _ly++;
            if (_ly == 144)
                _requestInterrupt(0);
            if (_ly > 153)
                _ly = 0;
            UpdateCoincidence();
        }

        if (_ly >= 144)
        {
            SetMode(1);
        }
        else if (_dot < 80)
        {
            SetMode(2);
        }
        else if (_dot < 252)
        {
            SetMode(3);
        }
        else
        {
            if ((_stat & 0x03) == 3)
                RenderScanline();
            SetMode(0);
        }
    }

    private void SetMode(byte mode)
    {
        byte oldMode = (byte)(_stat & 0x03);
        _stat = (byte)((_stat & 0xFC) | mode);
        if (oldMode != mode)
            UpdateStatInterrupt();
    }

    private void UpdateCoincidence()
    {
        bool matches = _ly == _lyc;
        _stat = matches ? (byte)(_stat | 0x04) : (byte)(_stat & ~0x04);
        UpdateStatInterrupt();
    }

    private void UpdateStatInterrupt()
    {
        bool request = ((_stat & 0x40) != 0 && (_stat & 0x04) != 0)
            || ((_stat & 0x20) != 0 && (_stat & 0x03) == 2)
            || ((_stat & 0x10) != 0 && (_stat & 0x03) == 1)
            || ((_stat & 0x08) != 0 && (_stat & 0x03) == 0);
        if (request && !_statInterruptLine)
            _requestInterrupt(1);
        _statInterruptLine = request;
    }

    private void RenderScanline()
    {
        int y = _ly;
        if (y >= Height)
            return;

        Span<byte> backgroundColors = stackalloc byte[Width];
        bool backgroundEnabled = (_lcdc & 0x01) != 0;
        for (int x = 0; x < Width; x++)
        {
            int color;
            if (backgroundEnabled)
                color = GetBackgroundColor(x, y, out backgroundColors[x]);
            else
            {
                color = 0;
                backgroundColors[x] = 0;
            }
            _pixels[y * Width + x] = MapShade(_bgp, color);
        }

        if ((_lcdc & 0x02) == 0)
            return;

        int spriteHeight = (_lcdc & 0x04) != 0 ? 16 : 8;
        Span<int> spriteIndices = stackalloc int[10];
        int spriteCount = 0;
        for (int i = 0; i < 40 && spriteCount < 10; i++)
        {
            int spriteY = _oam[i * 4] - 16;
            if (y >= spriteY && y < spriteY + spriteHeight)
                spriteIndices[spriteCount++] = i;
        }
        for (int i = 1; i < spriteCount; i++)
        {
            int selected = spriteIndices[i];
            int j = i - 1;
            while (j >= 0 && HasHigherPriority(selected, spriteIndices[j]))
            {
                spriteIndices[j + 1] = spriteIndices[j];
                j--;
            }
            spriteIndices[j + 1] = selected;
        }

        for (int n = spriteCount - 1; n >= 0; n--)
        {
            int i = spriteIndices[n];
            int offset = i * 4;
            int spriteX = _oam[offset + 1] - 8;
            int spriteY = _oam[offset] - 16;
            byte tile = _oam[offset + 2];
            byte flags = _oam[offset + 3];
            int row = y - spriteY;
            if ((flags & 0x40) != 0)
                row = spriteHeight - 1 - row;
            if (spriteHeight == 16)
            {
                tile &= 0xFE;
                tile += (byte)(row / 8);
                row %= 8;
            }

            int tileAddress = tile * 16 + row * 2;
            for (int px = 0; px < 8; px++)
            {
                int screenX = spriteX + px;
                if (screenX < 0 || screenX >= Width)
                    continue;
                int bit = (flags & 0x20) != 0 ? px : 7 - px;
                int color = ((_vram[tileAddress + 1] >> bit) & 1) << 1
                    | ((_vram[tileAddress] >> bit) & 1);
                if (color == 0 || ((flags & 0x80) != 0 && backgroundColors[screenX] != 0))
                    continue;
                byte palette = (flags & 0x10) != 0 ? _obp1 : _obp0;
                _pixels[y * Width + screenX] = MapShade(palette, color);
            }
        }
    }

    private int GetBackgroundColor(int x, int y, out byte color)
    {
        bool window = (_lcdc & 0x20) != 0 && y >= _wy && x >= _wx - 7;
        int mapBase = window
            ? ((_lcdc & 0x40) != 0 ? 0x1C00 : 0x1800)
            : ((_lcdc & 0x08) != 0 ? 0x1C00 : 0x1800);
        int pixelX = window ? x - (_wx - 7) : (x + _scx) & 0xFF;
        int pixelY = window ? y - _wy : (y + _scy) & 0xFF;
        int tileIndex = mapBase + (pixelY / 8) * 32 + pixelX / 8;
        int tile = _vram[tileIndex];
        int tileAddress = (_lcdc & 0x10) != 0
            ? tile * 16
            : 0x1000 + (sbyte)tile * 16;
        int bit = 7 - pixelX % 8;
        int rowAddress = tileAddress + (pixelY % 8) * 2;
        color = (byte)(((_vram[rowAddress + 1] >> bit) & 1) << 1
            | ((_vram[rowAddress] >> bit) & 1));
        return color;
    }

    private bool HasHigherPriority(int left, int right)
    {
        int leftX = _oam[left * 4 + 1];
        int rightX = _oam[right * 4 + 1];
        return leftX < rightX || (leftX == rightX && left < right);
    }

    private static byte MapShade(byte palette, int color) => (byte)((palette >> (color * 2)) & 0x03);
}
