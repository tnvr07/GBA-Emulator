namespace PocketBoy;

internal sealed class GbaPpu
{
    public const int Width = 240;
    public const int Height = 160;
    private static readonly (int Width, int Height)[,] SpriteSizes =
    {
        { (8, 8), (16, 16), (32, 32), (64, 64) },
        { (16, 8), (32, 8), (32, 16), (64, 32) },
        { (8, 16), (8, 32), (16, 32), (32, 64) },
        { (8, 8), (8, 8), (8, 8), (8, 8) }
    };

    private readonly GbaMemory _memory;
    private readonly byte[] _pixels = new byte[Width * Height * 2];

    public GbaPpu(GbaMemory memory) => _memory = memory;

    public byte[] Pixels => _pixels;

    public void RenderFrame()
    {
        ReadOnlySpan<byte> io = _memory.Io;
        ReadOnlySpan<byte> vram = _memory.VideoRam;
        byte mode = (byte)(Read16(io, 0) & 7);
        bool frameSelect = (Read16(io, 0) & 0x10) != 0;
        Array.Clear(_pixels);

        if (mode == 3 && (Read16(io, 0) & 0x0400) != 0)
        {
            RenderBitmap(vram, 0, Width, Height);
        }
        else if (mode == 4 && (Read16(io, 0) & 0x0400) != 0)
        {
            RenderIndexedBitmap(vram, frameSelect ? 0xA000 : 0);
        }
        else if (mode == 5 && (Read16(io, 0) & 0x0400) != 0)
        {
            RenderBitmap(vram, frameSelect ? 0xA000 : 0, 160, 128);
        }
        else if (mode <= 2)
        {
            RenderBackgrounds(io, vram, mode);
        }
    }

    private void RenderBitmap(ReadOnlySpan<byte> vram, int baseOffset, int width, int height)
    {
        int xOffset = (Width - width) / 2;
        int yOffset = (Height - height) / 2;
        int destination = yOffset * Width + xOffset;
        for (int y = 0; y < height; y++)
        {
            int row = baseOffset + y * width * 2;
            for (int x = 0; x < width; x++)
                WritePixel(destination + y * Width + x, Read16(vram, row + x * 2));
        }
    }

    private void RenderIndexedBitmap(ReadOnlySpan<byte> vram, int baseOffset)
    {
        ReadOnlySpan<byte> palette = _memory.Palette;
        for (int i = 0; i < Width * Height; i++)
        {
            int colorIndex = vram[baseOffset + i];
            WritePixel(i, Read16(palette, colorIndex * 2));
        }
    }

    private void RenderBackgrounds(ReadOnlySpan<byte> io, ReadOnlySpan<byte> vram, int mode)
    {
        ushort displayControl = Read16(io, 0);
        for (int priority = 3; priority >= 0; priority--)
        {
            for (int background = 0; background < 4; background++)
            {
                if ((displayControl & (1 << (8 + background))) == 0)
                    continue;
                ushort control = Read16(io, 0x08 + background * 2);
                if ((control & 3) != priority)
                    continue;
                bool affine = background == 2 && mode is 1 or 2
                    || background == 3 && mode == 2;
                if (affine)
                    DrawAffineBackground(io, vram, control, background);
                else if (background < 2 || mode == 0)
                    DrawBackground(io, vram, control, background);
            }
            RenderObjects(io, vram, priority);
        }
    }

    private void DrawAffineBackground(ReadOnlySpan<byte> io, ReadOnlySpan<byte> vram, ushort control, int background)
    {
        int size = 128 << ((control >> 14) & 3);
        int screenBase = ((control >> 8) & 0x1F) * 0x800;
        int characterBase = ((control >> 2) & 3) * 0x4000;
        int registerOffset = 0x20 + (background - 2) * 0x10;
        int pa = (short)Read16(io, registerOffset);
        int pb = (short)Read16(io, registerOffset + 2);
        int pc = (short)Read16(io, registerOffset + 4);
        int pd = (short)Read16(io, registerOffset + 6);
        int referenceX = SignExtend28(Read32(io, registerOffset + 8));
        int referenceY = SignExtend28(Read32(io, registerOffset + 12));
        bool wrap = (control & 0x2000) != 0;
        ReadOnlySpan<byte> palette = _memory.Palette;

        for (int y = 0; y < Height; y++)
        {
            int sourceX = referenceX + pb * y;
            int sourceY = referenceY + pd * y;
            for (int x = 0; x < Width; x++)
            {
                int pixelX = sourceX >> 8;
                int pixelY = sourceY >> 8;
                sourceX += pa;
                sourceY += pc;
                if (wrap)
                {
                    pixelX &= size - 1;
                    pixelY &= size - 1;
                }
                else if ((uint)pixelX >= size || (uint)pixelY >= size)
                {
                    continue;
                }

                int tileX = pixelX >> 3;
                int tileY = pixelY >> 3;
                int mapOffset = screenBase + tileY * (size >> 3) + tileX;
                int tileNumber = ReadVram8(vram, mapOffset);
                int tileOffset = characterBase + tileNumber * 64
                    + (pixelY & 7) * 8 + (pixelX & 7);
                int paletteIndex = ReadVram8(vram, tileOffset);
                if (paletteIndex != 0)
                    WritePixel(y * Width + x, Read16(palette, paletteIndex * 2));
            }
        }
    }

    private void DrawBackground(ReadOnlySpan<byte> io, ReadOnlySpan<byte> vram, ushort control, int background)
    {
        int characterBase = ((control >> 2) & 3) * 0x4000;
        int screenBase = ((control >> 8) & 0x1F) * 0x800;
        int size = (control >> 14) & 3;
        int widthTiles = (size & 1) != 0 ? 64 : 32;
        int heightTiles = (size & 2) != 0 ? 64 : 32;
        int width = widthTiles * 8;
        int height = heightTiles * 8;
        int horizontal = Read16(io, 0x10 + background * 4) & 0x1FF;
        int vertical = Read16(io, 0x12 + background * 4) & 0x1FF;
        bool color256 = (control & 0x80) != 0;
        ReadOnlySpan<byte> palette = _memory.Palette;

        for (int y = 0; y < Height; y++)
        {
            int sourceY = (y + vertical) % height;
            int tileY = sourceY / 8;
            for (int x = 0; x < Width; x++)
            {
                int sourceX = (x + horizontal) % width;
                int tileX = sourceX / 8;
                int block = (tileY / 32) * (widthTiles / 32) + tileX / 32;
                int mapOffset = screenBase + block * 0x800 + ((tileY & 31) * 32 + (tileX & 31)) * 2;
                ushort entry = ReadVram16(vram, mapOffset);
                int tileNumber = entry & 0x3FF;
                int pixelX = sourceX & 7;
                int pixelY = sourceY & 7;
                if ((entry & 0x0400) != 0)
                    pixelX = 7 - pixelX;
                if ((entry & 0x0800) != 0)
                    pixelY = 7 - pixelY;

                int tileOffset = characterBase + tileNumber * (color256 ? 64 : 32)
                    + pixelY * (color256 ? 8 : 4);
                int paletteIndex;
                if (color256)
                {
                    paletteIndex = ReadVram8(vram, tileOffset + pixelX);
                }
                else
                {
                    byte packed = ReadVram8(vram, tileOffset + pixelX / 2);
                    paletteIndex = ((pixelX & 1) == 0 ? packed & 0x0F : packed >> 4)
                        + ((entry >> 12) & 0x0F) * 16;
                }

                if (paletteIndex != 0)
                    WritePixel(y * Width + x, Read16(palette, paletteIndex * 2));
            }
        }
    }

    private void RenderObjects(ReadOnlySpan<byte> io, ReadOnlySpan<byte> vram, int priority)
    {
        ushort displayControl = Read16(io, 0);
        if ((displayControl & 0x1000) == 0)
            return;

        bool oneDimensional = (displayControl & 0x40) != 0;
        bool object256 = (displayControl & 0x2000) != 0;
        int objectUnit = object256 ? 64 : 32;
        ReadOnlySpan<byte> oam = _memory.Oam;
        ReadOnlySpan<byte> palette = _memory.Palette;

        for (int sprite = 127; sprite >= 0; sprite--)
        {
                int offset = sprite * 8;
                ushort attribute0 = Read16(oam, offset);
                ushort attribute1 = Read16(oam, offset + 2);
                ushort attribute2 = Read16(oam, offset + 4);
                if ((attribute0 & 0x0200) != 0 || ((attribute2 >> 10) & 3) != priority)
                    continue;
                if ((attribute0 & 0x0100) != 0)
                    continue;

                int shape = (attribute0 >> 14) & 3;
                int size = (attribute1 >> 14) & 3;
                (int spriteWidth, int spriteHeight) = SpriteSizes[shape, size];
                int spriteX = attribute1 & 0x1FF;
                int spriteY = attribute0 & 0xFF;
                if (spriteX >= 256)
                    spriteX -= 512;
                if (spriteY >= 160)
                    spriteY -= 256;

                int tile = attribute2 & 0x3FF;
                bool color256 = (attribute0 & 0x2000) != 0;
                bool flipHorizontal = (attribute1 & 0x1000) != 0;
                bool flipVertical = (attribute1 & 0x2000) != 0;
                int tileBytes = color256 ? 64 : 32;
                if (color256)
                    tile &= ~1;

                for (int y = 0; y < spriteHeight; y++)
                {
                    int screenY = spriteY + y;
                    if ((uint)screenY >= Height)
                        continue;
                    int sourceY = flipVertical ? spriteHeight - 1 - y : y;
                    for (int x = 0; x < spriteWidth; x++)
                    {
                        int screenX = spriteX + x;
                        if ((uint)screenX >= Width)
                            continue;
                        int sourceX = flipHorizontal ? spriteWidth - 1 - x : x;
                        int tileIndex;
                        if (oneDimensional)
                            tileIndex = tile + (sourceY / 8) * (spriteWidth / 8) + sourceX / 8;
                        else
                            tileIndex = tile + (sourceY / 8) * (color256 ? 16 : 32)
                                + (sourceX / 8) * (color256 ? 2 : 1);
                        int tileAddress = 0x10000 + tileIndex * objectUnit
                            + (sourceY & 7) * (color256 ? 8 : 4);
                        int paletteIndex;
                        if (color256)
                        {
                            paletteIndex = ReadVram8(vram, tileAddress + (sourceX & 7));
                        }
                        else
                        {
                            byte packed = ReadVram8(vram, tileAddress + (sourceX & 7) / 2);
                            paletteIndex = ((sourceX & 1) == 0 ? packed & 0x0F : packed >> 4)
                                + (attribute2 >> 8 & 0x0F) * 16;
                        }

                        if (paletteIndex != 0)
                            WritePixel(screenY * Width + screenX, Read16(palette, 0x200 + paletteIndex * 2));
                }
            }
        }
    }

    private void WritePixel(int index, ushort color)
    {
        _pixels[index * 2] = (byte)color;
        _pixels[index * 2 + 1] = (byte)(color >> 8);
    }

    private static ushort Read16(ReadOnlySpan<byte> data, int offset) =>
        (ushort)(data[offset] | (data[offset + 1] << 8));

    private static ushort ReadVram16(ReadOnlySpan<byte> data, int offset) =>
        (ushort)(ReadVram8(data, offset) | (ReadVram8(data, offset + 1) << 8));

    private static byte ReadVram8(ReadOnlySpan<byte> data, int offset) =>
        offset >= 0 && offset < data.Length ? data[offset] : (byte)0;

    private static uint Read32(ReadOnlySpan<byte> data, int offset) =>
        (uint)(data[offset] | (data[offset + 1] << 8)
            | (data[offset + 2] << 16) | (data[offset + 3] << 24));

    private static int SignExtend28(uint value) => (int)(value << 4) >> 4;
}
