namespace PocketBoy;

internal sealed class GameBoy
{
    private const int CyclesPerFrame = 456 * 154;
    private readonly Cpu _cpu;
    private readonly MemoryBus _bus;
    private int _cycleRemainder;

    public string Title { get; }
    public Ppu Ppu => _bus.Ppu;
    public Joypad Joypad => _bus.Joypad;

    public GameBoy(byte[] rom)
    {
        Cartridge cartridge = new(rom);
        Title = cartridge.Title;
        _bus = new MemoryBus(cartridge);
        _cpu = new Cpu(_bus);
    }

    public void AdvanceFrame()
    {
        while (_cycleRemainder < CyclesPerFrame)
            _cycleRemainder += _cpu.Step();
        _cycleRemainder -= CyclesPerFrame;
    }
}
