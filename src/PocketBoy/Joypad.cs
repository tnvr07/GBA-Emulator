namespace PocketBoy;

internal sealed class Joypad
{
    private readonly Action<int> _requestInterrupt;
    private byte _select = 0x30;
    private byte _buttons = 0xFF;
    private byte _directions = 0xFF;

    public Joypad(Action<int> requestInterrupt) => _requestInterrupt = requestInterrupt;

    public byte Read()
    {
        byte result = (byte)(0xC0 | _select | 0x0F);
        if ((_select & 0x10) == 0)
            result &= _directions;
        if ((_select & 0x20) == 0)
            result &= _buttons;
        return result;
    }

    public void Write(byte value) => _select = (byte)(value & 0x30);

    public void SetButton(GameBoyButton button, bool pressed)
    {
        ref byte group = ref (button is GameBoyButton.Up or GameBoyButton.Down or GameBoyButton.Left or GameBoyButton.Right
            ? ref _directions
            : ref _buttons);
        int bit = button switch
        {
            GameBoyButton.Right or GameBoyButton.A => 0,
            GameBoyButton.Left or GameBoyButton.B => 1,
            GameBoyButton.Up or GameBoyButton.Select => 2,
            _ => 3
        };
        byte previous = Read();
        group = pressed ? (byte)(group & ~(1 << bit)) : (byte)(group | (1 << bit));
        if ((previous & 0x0F & ~Read()) != 0)
            _requestInterrupt(4);
    }
}

internal enum GameBoyButton
{
    Right,
    Left,
    Up,
    Down,
    A,
    B,
    Select,
    Start,
    L,
    R
}
