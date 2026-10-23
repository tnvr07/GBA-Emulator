namespace PocketBoy;

internal sealed class GameBoyAdvance
{
    private const int CyclesPerFrame = 280_896;
    private readonly GbaMemory _memory;
    private readonly GbaCpu _cpu;
    private readonly GbaPpu _ppu;
    private int _cycleRemainder;

    public string Title => _memory.Title;
    public GbaPpu Ppu => _ppu;
    public uint ProgramCounter => _cpu.ProgramCounter;
    public bool ThumbMode => _cpu.ThumbMode;
    public ushort DisplayControl => _memory.DisplayControl;
    public uint ReadMemory32(uint address) => _memory.Read32(address);
    public uint ReadRegister(int index) => _cpu.ReadRegister(index);
    public ushort ReadIo16(uint address) => _memory.Read16(address);

    public GameBoyAdvance(byte[] rom)
    {
        _memory = new GbaMemory(rom);
        _cpu = new GbaCpu(_memory);
        _ppu = new GbaPpu(_memory);
    }

    public void SetButton(GameBoyButton button, bool pressed) => _memory.SetButton(button, pressed);

    public void AdvanceFrame()
    {
        while (_cycleRemainder < CyclesPerFrame)
        {
            int cycles = _cpu.Step();
            _memory.Tick(cycles);
            _cycleRemainder += cycles;
        }
        _cycleRemainder -= CyclesPerFrame;
        _ppu.RenderFrame();
    }
}
