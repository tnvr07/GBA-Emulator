namespace PocketBoy;

internal sealed class Cpu
{
    private const byte FlagZ = 0x80;
    private const byte FlagN = 0x40;
    private const byte FlagH = 0x20;
    private const byte FlagC = 0x10;
    private readonly MemoryBus _bus;
    private bool _ime;
    private bool _halted;
    private int _imeDelay;

    public byte A { get; private set; } = 0x01;
    public byte F { get; private set; } = 0xB0;
    public byte B { get; private set; }
    public byte C { get; private set; } = 0x13;
    public byte D { get; private set; }
    public byte E { get; private set; } = 0xD8;
    public byte H { get; private set; } = 0x01;
    public byte L { get; private set; } = 0x4D;
    public ushort SP { get; private set; } = 0xFFFE;
    public ushort PC { get; private set; } = 0x0100;

    public Cpu(MemoryBus bus) => _bus = bus;

    public int Step()
    {
        byte pending = (byte)(_bus.InterruptEnable & _bus.InterruptFlags & 0x1F);
        if (pending != 0)
        {
            _halted = false;
            if (_ime)
            {
                ServiceInterrupt(pending);
                return 20;
            }
        }

        if (_halted)
        {
            _bus.Tick(4);
            return 4;
        }

        int cycles = Execute(FetchByte());
        if (_imeDelay > 0 && --_imeDelay == 0)
            _ime = true;
        _bus.Tick(cycles);
        return cycles;
    }

    private int Execute(byte opcode)
    {
        int x = opcode >> 6;
        int y = (opcode >> 3) & 7;
        int z = opcode & 7;
        int p = y >> 1;
        int q = y & 1;

        switch (x)
        {
            case 0:
                switch (z)
                {
                    case 0:
                        return y switch
                        {
                            0 => 4,
                            1 => StoreWord(FetchWord(), SP, 20),
                            2 => Stop(),
                            3 => JumpRelative(true),
                            _ => JumpRelative(Condition(y - 4))
                        };
                    case 1:
                        if (q == 0)
                        {
                            SetRp(p, FetchWord());
                            return 12;
                        }
                        AddHl(GetRp(p));
                        return 8;
                    case 2:
                        ushort address = p switch
                        {
                            0 => GetBc(),
                            1 => GetDe(),
                            _ => GetHl()
                        };
                        if (q == 0)
                            _bus.Write(address, A);
                        else
                            A = _bus.Read(address);
                        if (p == 2)
                            SetHl((ushort)(GetHl() + 1));
                        else if (p == 3)
                            SetHl((ushort)(GetHl() - 1));
                        return 8;
                    case 3:
                        SetRp(p, (ushort)(GetRp(p) + (q == 0 ? 1 : -1)));
                        return 8;
                    case 4:
                        SetR(y, Increment(GetR(y)));
                        return y == 6 ? 12 : 4;
                    case 5:
                        SetR(y, Decrement(GetR(y)));
                        return y == 6 ? 12 : 4;
                    case 6:
                        SetR(y, FetchByte());
                        return y == 6 ? 12 : 8;
                    default:
                        return ExecuteAccumulatorOperation(y);
                }
            case 1:
                if (y == 6 && z == 6)
                {
                    _halted = true;
                    return 4;
                }
                SetR(y, GetR(z));
                return y == 6 || z == 6 ? 8 : 4;
            case 2:
                Alu(y, GetR(z));
                return z == 6 ? 8 : 4;
            default:
                switch (z)
                {
                    case 0:
                        if (y < 4)
                        {
                            if (Condition(y))
                            {
                                PC = PopWord();
                                return 20;
                            }
                            return 8;
                        }
                        if (y == 4)
                        {
                            _bus.Write((ushort)(0xFF00 | FetchByte()), A);
                            return 12;
                        }
                        if (y == 5)
                        {
                            AddSignedToSp(FetchByte(), false);
                            return 16;
                        }
                        if (y == 6)
                        {
                            A = _bus.Read((ushort)(0xFF00 | FetchByte()));
                            return 12;
                        }
                        SetHl(AddSignedToSp(FetchByte(), true));
                        return 12;
                    case 1:
                        if (q == 0)
                        {
                            SetRp2(p, PopWord());
                            return 12;
                        }
                        switch (p)
                        {
                            case 0: PC = PopWord(); return 16;
                            case 1: PC = PopWord(); _ime = true; _imeDelay = 0; return 16;
                            case 2: PC = GetHl(); return 4;
                            default: SP = GetHl(); return 8;
                        }
                    case 2:
                        if (y < 4)
                        {
                            ushort target = FetchWord();
                            if (Condition(y))
                            {
                                PC = target;
                                return 16;
                            }
                            return 12;
                        }
                        if (y == 4)
                            _bus.Write((ushort)(0xFF00 | C), A);
                        else if (y == 5)
                            _bus.Write(FetchWord(), A);
                        else if (y == 6)
                            A = _bus.Read((ushort)(0xFF00 | C));
                        else
                            A = _bus.Read(FetchWord());
                        return y is 5 or 7 ? 16 : 8;
                    case 3:
                        switch (y)
                        {
                            case 0: PC = FetchWord(); return 16;
                            case 1: return ExecuteCb(FetchByte());
                            case 6: _ime = false; _imeDelay = 0; return 4;
                            case 7: _imeDelay = 2; return 4;
                            default: return 4;
                        }
                    case 4:
                        if (y >= 4)
                            return 4;
                        ushort callTarget = FetchWord();
                        if (Condition(y))
                        {
                            PushWord(PC);
                            PC = callTarget;
                            return 24;
                        }
                        return 12;
                    case 5:
                        if (q == 0)
                        {
                            PushWord(GetRp2(p));
                            return 16;
                        }
                        if (p == 0)
                        {
                            ushort callAddress = FetchWord();
                            PushWord(PC);
                            PC = callAddress;
                            return 24;
                        }
                        return 4;
                    case 6:
                        Alu(y, FetchByte());
                        return 8;
                    default:
                        PushWord(PC);
                        PC = (ushort)(y * 8);
                        return 16;
                }
        }
    }

    private int ExecuteAccumulatorOperation(int operation)
    {
        switch (operation)
        {
            case 0:
                SetCarry((A & 0x80) != 0);
                A = (byte)((A << 1) | (A >> 7));
                F = (byte)(F & FlagC);
                break;
            case 1:
                SetCarry((A & 1) != 0);
                A = (byte)((A >> 1) | (A << 7));
                F = (byte)(F & FlagC);
                break;
            case 2:
                bool oldCarry = Carry;
                SetCarry((A & 0x80) != 0);
                A = (byte)((A << 1) | (oldCarry ? 1 : 0));
                F = (byte)(F & FlagC);
                break;
            case 3:
                oldCarry = Carry;
                SetCarry((A & 1) != 0);
                A = (byte)((A >> 1) | (oldCarry ? 0x80 : 0));
                F = (byte)(F & FlagC);
                break;
            case 4: DecimalAdjust(); break;
            case 5: A = (byte)~A; F = (byte)((F & (FlagZ | FlagC)) | FlagN | FlagH); break;
            case 6: F = (byte)((F & FlagZ) | FlagC); break;
            case 7: F = (byte)((F & FlagZ) | (Carry ? 0 : FlagC)); break;
        }
        return 4;
    }

    private int ExecuteCb(byte opcode)
    {
        int x = opcode >> 6;
        int y = (opcode >> 3) & 7;
        int z = opcode & 7;
        byte value = GetR(z);

        if (x == 0)
        {
            bool carry;
            switch (y)
            {
                case 0: carry = (value & 0x80) != 0; value = (byte)((value << 1) | (value >> 7)); break;
                case 1: carry = (value & 1) != 0; value = (byte)((value >> 1) | (value << 7)); break;
                case 2: bool oldCarry = Carry; carry = (value & 0x80) != 0; value = (byte)((value << 1) | (oldCarry ? 1 : 0)); break;
                case 3: oldCarry = Carry; carry = (value & 1) != 0; value = (byte)((value >> 1) | (oldCarry ? 0x80 : 0)); break;
                case 4: carry = (value & 0x80) != 0; value <<= 1; break;
                case 5: carry = (value & 1) != 0; value = (byte)((value >> 1) | (value & 0x80)); break;
                case 6: carry = false; value = (byte)((value << 4) | (value >> 4)); break;
                default: carry = (value & 1) != 0; value >>= 1; break;
            }
            SetR(z, value);
            F = (byte)((value == 0 ? FlagZ : 0) | (carry ? FlagC : 0));
        }
        else if (x == 1)
        {
            F = (byte)((F & FlagC) | FlagH | ((value & (1 << y)) == 0 ? FlagZ : 0));
        }
        else if (x == 2)
        {
            SetR(z, (byte)(value & ~(1 << y)));
        }
        else
        {
            SetR(z, (byte)(value | (1 << y)));
        }

        return z == 6 ? (x == 1 ? 12 : 16) : 8;
    }

    private void Alu(int operation, byte value)
    {
        int result;
        switch (operation)
        {
            case 0:
            case 1:
                result = A + value + (operation == 1 && Carry ? 1 : 0);
                F = (byte)(((byte)result == 0 ? FlagZ : 0)
                    | ((((A & 0x0F) + (value & 0x0F) + (operation == 1 && Carry ? 1 : 0)) > 0x0F) ? FlagH : 0)
                    | (result > 0xFF ? FlagC : 0));
                A = (byte)result;
                break;
            case 2:
            case 3:
            case 7:
                int borrow = operation == 3 && Carry ? 1 : 0;
                result = A - value - borrow;
                F = (byte)(FlagN | ((byte)result == 0 ? FlagZ : 0)
                    | ((A & 0x0F) < ((value & 0x0F) + borrow) ? FlagH : 0)
                    | (result < 0 ? FlagC : 0));
                if (operation != 7)
                    A = (byte)result;
                break;
            case 4: A &= value; F = (byte)((A == 0 ? FlagZ : 0) | FlagH); break;
            case 5: A ^= value; F = A == 0 ? FlagZ : (byte)0; break;
            case 6: A |= value; F = A == 0 ? FlagZ : (byte)0; break;
        }
    }

    private byte Increment(byte value)
    {
        byte result = (byte)(value + 1);
        F = (byte)((F & FlagC) | (result == 0 ? FlagZ : 0) | ((value & 0x0F) == 0x0F ? FlagH : 0));
        return result;
    }

    private byte Decrement(byte value)
    {
        byte result = (byte)(value - 1);
        F = (byte)((F & FlagC) | FlagN | (result == 0 ? FlagZ : 0) | ((value & 0x0F) == 0 ? FlagH : 0));
        return result;
    }

    private void AddHl(ushort value)
    {
        int result = GetHl() + value;
        F = (byte)((F & FlagZ) | (((GetHl() & 0x0FFF) + (value & 0x0FFF) > 0x0FFF) ? FlagH : 0)
            | (result > 0xFFFF ? FlagC : 0));
        SetHl((ushort)result);
    }

    private ushort AddSignedToSp(byte raw, bool storeInHl)
    {
        int signed = (sbyte)raw;
        ushort result = (ushort)(SP + signed);
        F = (byte)(((SP & 0x0F) + (raw & 0x0F) > 0x0F ? FlagH : 0)
            | ((SP & 0xFF) + raw > 0xFF ? FlagC : 0));
        if (!storeInHl)
            SP = result;
        return result;
    }

    private void DecimalAdjust()
    {
        int correction = 0;
        bool carry = Carry;
        if ((F & FlagN) == 0)
        {
            if (Carry || A > 0x99)
            {
                correction |= 0x60;
                carry = true;
            }
            if ((F & FlagH) != 0 || (A & 0x0F) > 9)
                correction |= 0x06;
            A = (byte)(A + correction);
        }
        else
        {
            if (Carry) correction |= 0x60;
            if ((F & FlagH) != 0) correction |= 0x06;
            A = (byte)(A - correction);
        }
        F = (byte)((F & FlagN) | (A == 0 ? FlagZ : 0) | (carry ? FlagC : 0));
    }

    private void ServiceInterrupt(byte pending)
    {
        int bit = 0;
        while ((pending & (1 << bit)) == 0)
            bit++;
        _ime = false;
        _imeDelay = 0;
        _halted = false;
        _bus.InterruptFlags = (byte)(_bus.InterruptFlags & ~(1 << bit));
        PushWord(PC);
        PC = (ushort)(0x40 + bit * 8);
        _bus.Tick(20);
    }

    private int Stop()
    {
        FetchByte();
        _halted = true;
        return 4;
    }

    private int StoreWord(ushort address, ushort value, int cycles)
    {
        _bus.Write(address, (byte)value);
        _bus.Write((ushort)(address + 1), (byte)(value >> 8));
        return cycles;
    }

    private int JumpRelative(bool take)
    {
        sbyte offset = (sbyte)FetchByte();
        if (take)
        {
            PC = (ushort)(PC + offset);
            return 12;
        }
        return 8;
    }

    private bool Condition(int condition) => condition switch
    {
        0 => (F & FlagZ) == 0,
        1 => (F & FlagZ) != 0,
        2 => (F & FlagC) == 0,
        _ => (F & FlagC) != 0
    };

    private byte FetchByte()
    {
        byte value = _bus.Read(PC);
        PC++;
        return value;
    }

    private ushort FetchWord()
    {
        byte low = FetchByte();
        return (ushort)(low | (FetchByte() << 8));
    }

    private ushort PopWord()
    {
        byte low = _bus.Read(SP++);
        return (ushort)(low | (_bus.Read(SP++) << 8));
    }

    private void PushWord(ushort value)
    {
        _bus.Write(--SP, (byte)(value >> 8));
        _bus.Write(--SP, (byte)value);
    }

    private byte GetR(int index) => index switch
    {
        0 => B, 1 => C, 2 => D, 3 => E, 4 => H, 5 => L,
        6 => _bus.Read(GetHl()), _ => A
    };

    private void SetR(int index, byte value)
    {
        switch (index)
        {
            case 0: B = value; break;
            case 1: C = value; break;
            case 2: D = value; break;
            case 3: E = value; break;
            case 4: H = value; break;
            case 5: L = value; break;
            case 6: _bus.Write(GetHl(), value); break;
            case 7: A = value; break;
        }
    }

    private ushort GetRp(int index) => index switch
    {
        0 => GetBc(), 1 => GetDe(), 2 => GetHl(), _ => SP
    };

    private void SetRp(int index, ushort value)
    {
        switch (index)
        {
            case 0: SetBc(value); break;
            case 1: SetDe(value); break;
            case 2: SetHl(value); break;
            case 3: SP = value; break;
        }
    }

    private ushort GetRp2(int index) => index == 3 ? (ushort)((A << 8) | F) : GetRp(index);

    private void SetRp2(int index, ushort value)
    {
        if (index == 3)
        {
            A = (byte)(value >> 8);
            F = (byte)(value & 0xF0);
        }
        else
        {
            SetRp(index, value);
        }
    }

    private ushort GetBc() => (ushort)((B << 8) | C);
    private ushort GetDe() => (ushort)((D << 8) | E);
    private ushort GetHl() => (ushort)((H << 8) | L);
    private void SetBc(ushort value) { B = (byte)(value >> 8); C = (byte)value; }
    private void SetDe(ushort value) { D = (byte)(value >> 8); E = (byte)value; }
    private void SetHl(ushort value) { H = (byte)(value >> 8); L = (byte)value; }
    private bool Carry => (F & FlagC) != 0;
    private void SetCarry(bool carry) => F = carry ? (byte)(F | FlagC) : (byte)(F & ~FlagC);
}
