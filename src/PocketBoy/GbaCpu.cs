namespace PocketBoy;

internal sealed class GbaCpu
{
    private const uint Negative = 0x80000000;
    private const uint Zero = 0x40000000;
    private const uint Carry = 0x20000000;
    private const uint Overflow = 0x10000000;
    private const uint IrqReturnSentinel = 0xFFFF0000;
    private readonly GbaMemory _memory;
    private readonly uint[] _registers = new uint[16];
    private readonly uint[] _interruptedRegisters = new uint[15];
    private uint _status = 0x1F;
    private uint _interruptedStatus;
    private uint _interruptedPc;
    private uint _interruptedSp;
    private uint _interruptedLr;
    private uint _irqStackPointer = 0x03007FA0;
    private uint _interruptedIrqStackPointer;
    private ushort _interruptedInterrupts;
    private bool _inCallback;
    private bool _biosWaiting;
    private bool _biosWaitForEnabledInterrupt;
    private ushort _biosWaitMask;
    public uint ProgramCounter => _registers[15];
    public bool ThumbMode => (_status & 0x20) != 0;
    public uint ReadRegister(int index) => _registers[index];

    public GbaCpu(GbaMemory memory)
    {
        _memory = memory;
        _registers[13] = 0x03007F00;
        _registers[15] = 0x08000000;
    }

    public int Step()
    {
        if (_inCallback && _registers[15] == IrqReturnSentinel)
        {
            ReturnFromInterruptCallback();
            return 4;
        }
        if (_inCallback && _registers[15] < 0x00004000)
        {
            ReturnFromInterruptCallback();
            return 4;
        }

        ushort waitFlags = _biosWaitForEnabledInterrupt
            ? (ushort)(_memory.PendingInterrupts & _memory.EnabledInterrupts)
            : _memory.PendingInterrupts;
        if (_biosWaiting && (waitFlags & _biosWaitMask) != 0)
        {
            _biosWaiting = false;
            _biosWaitForEnabledInterrupt = false;
        }

        ushort pendingInterrupts = (ushort)(_memory.EnabledInterrupts & _memory.PendingInterrupts);
        if (_memory.InterruptsEnabled && pendingInterrupts != 0 && !_inCallback)
        {
            EnterInterruptCallback();
            return 4;
        }

        if (_biosWaiting)
            return 4;

        uint address = _registers[15];
        if ((_status & 0x20) != 0)
        {
            ushort instruction = _memory.Read16(address);
            _registers[15] += 2;
            ExecuteThumb(instruction, address);
            return _registers[15] == address + 2 ? 2 : 3;
        }

        uint armInstruction = _memory.Read32(address);
        _registers[15] += 4;
        ExecuteArm(armInstruction, address);
        return _registers[15] == address + 4 ? 3 : 4;
    }

    private bool ExecuteArm(uint instruction, uint address)
    {
        int condition = (int)(instruction >> 28);
        if (!ConditionPassed(condition))
            return false;

        if ((instruction & 0x0F000000) == 0x0F000000)
        {
            SoftwareInterrupt(instruction & 0x00FFFFFF);
            return false;
        }

        if ((instruction & 0x0E000000) == 0x0A000000)
        {
            int offset = SignExtend(instruction & 0x00FFFFFF, 24) << 2;
            if ((instruction & 0x01000000) != 0)
                _registers[14] = address + 4;
            _registers[15] = unchecked((uint)(address + 8 + offset));
            return false;
        }

        uint psrTransfer = instruction & 0x0FBF0FFF;
        if (psrTransfer is 0x010F0000 or 0x014F0000)
        {
            int destination = (int)((instruction >> 12) & 0xF);
            _registers[destination] = (instruction & 0x00400000) != 0
                ? _interruptedStatus
                : _status;
            if (destination == 15)
                AlignProgramCounter();
            return false;
        }

        bool immediatePsrValue = (instruction & 0x02000000) != 0;
        uint psrMask = immediatePsrValue ? 0x0DB0F000u : 0x0DB0FFF0u;
        uint psrPattern = immediatePsrValue ? 0x0320F000u : 0x0120F000u;
        if ((instruction & psrMask) == psrPattern)
        {
            uint value = immediatePsrValue
                ? RotateRight(instruction & 0xFF, (int)((instruction >> 8) & 0xF) * 2)
                : _registers[instruction & 0xF];
            int fields = (int)((instruction >> 16) & 0xF);
            uint fieldMask = ((fields & 1) != 0 ? 0x000000FFu : 0)
                | ((fields & 2) != 0 ? 0x0000FF00u : 0)
                | ((fields & 4) != 0 ? 0x00FF0000u : 0)
                | ((fields & 8) != 0 ? 0xFF000000u : 0);
            if ((instruction & 0x00400000) != 0)
                _interruptedStatus = (_interruptedStatus & ~fieldMask) | (value & fieldMask);
            else
                _status = (_status & ~fieldMask) | (value & fieldMask);
            return false;
        }

        if ((instruction & 0x0FFFFFF0) == 0x012FFF10)
        {
            uint target = _registers[instruction & 0xF];
            if (_inCallback && (target & ~1u) == IrqReturnSentinel)
            {
                ReturnFromInterruptCallback();
                return false;
            }
            _status = (target & 1) != 0 ? _status | 0x20 : _status & ~0x20u;
            _registers[15] = target & ((_status & 0x20) != 0 ? ~1u : ~3u);
            return false;
        }

        if ((instruction & 0x0FC000F0) == 0x00000090)
        {
            int destination = (int)((instruction >> 16) & 0xF);
            int accumulator = (int)((instruction >> 12) & 0xF);
            int left = (int)(instruction & 0xF);
            int right = (int)((instruction >> 8) & 0xF);
            uint result = unchecked(ReadRegister(left, address) * ReadRegister(right, address));
            if ((instruction & 0x00200000) != 0)
                result = unchecked(result + _registers[accumulator]);
            _registers[destination] = result;
            if ((instruction & 0x00100000) != 0)
                SetNz(result);
            return false;
        }

        if ((instruction & 0x0E000000) == 0x08000000)
        {
            ExecuteBlockTransfer(instruction, address);
            return false;
        }

        if ((instruction & 0x0C000000) == 0x04000000)
        {
            ExecuteSingleTransfer(instruction, address);
            return false;
        }

        if ((instruction & 0x0E000090) == 0x00000090)
        {
            ExecuteHalfwordTransfer(instruction, address);
            return false;
        }

        if ((instruction & 0x0C000000) == 0)
        {
            ExecuteDataProcessing(instruction, address);
            return false;
        }

        return false;
    }

    private void ExecuteDataProcessing(uint instruction, uint address)
    {
        int operation = (int)((instruction >> 21) & 0xF);
        bool setFlags = (instruction & 0x00100000) != 0;
        int destination = (int)((instruction >> 12) & 0xF);
        uint left = ReadRegister((int)((instruction >> 16) & 0xF), address);
        (uint right, bool shifterCarry) = DecodeOperand2(instruction, address);
        uint result;
        bool arithmetic = false;
        bool subtract = false;

        switch (operation)
        {
            case 0: result = left & right; break;
            case 1: result = left ^ right; break;
            case 2: result = unchecked(left - right); arithmetic = true; subtract = true; break;
            case 3: result = unchecked(right - left); arithmetic = true; subtract = true; break;
            case 4: result = unchecked(left + right); arithmetic = true; break;
            case 5:
                result = unchecked(left + right + ((_status & Carry) != 0 ? 1u : 0u));
                arithmetic = true;
                break;
            case 6:
                result = unchecked(left - right - ((_status & Carry) == 0 ? 1u : 0u));
                arithmetic = true;
                subtract = true;
                break;
            case 7:
                result = unchecked(right - left - ((_status & Carry) == 0 ? 1u : 0u));
                arithmetic = true;
                subtract = true;
                break;
            case 8: result = left & right; setFlags = true; break;
            case 9: result = left ^ right; setFlags = true; break;
            case 10: result = unchecked(left - right); setFlags = true; arithmetic = true; subtract = true; break;
            case 11: result = unchecked(left + right); setFlags = true; arithmetic = true; break;
            case 12: result = left | right; break;
            case 13: result = right; break;
            case 14: result = left & ~right; break;
            default: result = ~right; break;
        }

        if (setFlags)
        {
            if (arithmetic)
                SetArithmeticFlags(left, right, result, subtract, operation is 5 or 6 or 7);
            else
            {
                SetNz(result);
                if (shifterCarry)
                    _status |= Carry;
                else
                    _status &= ~Carry;
            }
        }

        if (operation is 8 or 9 or 10 or 11)
            return;
        _registers[destination] = result;
        if (destination == 15)
            AlignProgramCounter();
    }

    private (uint Value, bool Carry) DecodeOperand2(uint instruction, uint address)
    {
        if ((instruction & 0x02000000) != 0)
        {
            int rotate = (int)((instruction >> 8) & 0xF) * 2;
            uint value = RotateRight(instruction & 0xFF, rotate);
            return (value, rotate == 0 ? (_status & Carry) != 0 : (value & Negative) != 0);
        }

        uint source = ReadRegister((int)(instruction & 0xF), address);
        int type = (int)((instruction >> 5) & 3);
        bool registerShift = (instruction & 0x10) != 0;
        int amount = registerShift
            ? (int)(_registers[(instruction >> 8) & 0xF] & 0xFF)
            : (int)((instruction >> 7) & 0x1F);
        if (registerShift && amount == 0)
            return (source, (_status & Carry) != 0);

        return Shift(source, type, amount, registerShift, (_status & Carry) != 0);
    }

    private void ExecuteSingleTransfer(uint instruction, uint address)
    {
        bool registerOffset = (instruction & 0x02000000) != 0;
        bool preIndexed = (instruction & 0x01000000) != 0;
        bool add = (instruction & 0x00800000) != 0;
        bool byteTransfer = (instruction & 0x00400000) != 0;
        bool writeBack = (instruction & 0x00200000) != 0;
        bool load = (instruction & 0x00100000) != 0;
        int baseRegister = (int)((instruction >> 16) & 0xF);
        int transferRegister = (int)((instruction >> 12) & 0xF);
        uint baseAddress = ReadRegister(baseRegister, address);
        uint offset;
        if (registerOffset)
        {
            (offset, _) = Shift(_registers[instruction & 0xF], (int)((instruction >> 5) & 3),
                (int)((instruction >> 7) & 0x1F), false, (_status & Carry) != 0);
        }
        else
        {
            offset = instruction & 0xFFF;
        }

        uint adjusted = add ? unchecked(baseAddress + offset) : unchecked(baseAddress - offset);
        uint transferAddress = preIndexed ? adjusted : baseAddress;
        if (load)
        {
            uint value = byteTransfer
                ? _memory.Read8(transferAddress)
                : RotateRight(_memory.Read32(transferAddress), (int)(transferAddress & 3) * 8);
            _registers[transferRegister] = value;
            if (transferRegister == 15)
                AlignProgramCounter();
        }
        else if (byteTransfer)
        {
            _memory.Write8(transferAddress, (byte)ReadRegister(transferRegister, address));
        }
        else
        {
            _memory.Write32(transferAddress, ReadRegister(transferRegister, address));
        }

        if (!preIndexed || writeBack)
            _registers[baseRegister] = adjusted;
    }

    private void ExecuteHalfwordTransfer(uint instruction, uint address)
    {
        bool preIndexed = (instruction & 0x01000000) != 0;
        bool add = (instruction & 0x00800000) != 0;
        bool immediateOffset = (instruction & 0x00400000) != 0;
        bool writeBack = (instruction & 0x00200000) != 0;
        bool load = (instruction & 0x00100000) != 0;
        int baseRegister = (int)((instruction >> 16) & 0xF);
        int transferRegister = (int)((instruction >> 12) & 0xF);
        int transferType = (int)((instruction >> 5) & 3);
        uint offset = immediateOffset
            ? ((instruction >> 4) & 0xF0) | (instruction & 0xF)
            : _registers[instruction & 0xF];
        uint baseAddress = ReadRegister(baseRegister, address);
        uint adjusted = add ? baseAddress + offset : baseAddress - offset;
        uint transferAddress = preIndexed ? adjusted : baseAddress;

        if (load)
        {
            ushort value = _memory.Read16(transferAddress);
            _registers[transferRegister] = transferType switch
            {
                2 => (uint)(int)(sbyte)value,
                3 => (uint)(int)(short)value,
                _ => value
            };
        }
        else
        {
            _memory.Write16(transferAddress, (ushort)ReadRegister(transferRegister, address));
        }

        if (!preIndexed || writeBack)
            _registers[baseRegister] = adjusted;
    }

    private void ExecuteBlockTransfer(uint instruction, uint address)
    {
        bool preIndexed = (instruction & 0x01000000) != 0;
        bool add = (instruction & 0x00800000) != 0;
        bool writeBack = (instruction & 0x00200000) != 0;
        bool load = (instruction & 0x00100000) != 0;
        int baseRegister = (int)((instruction >> 16) & 0xF);
        ushort list = (ushort)instruction;
        int count = System.Numerics.BitOperations.PopCount(list);
        if (count == 0)
        {
            list = 0x8000;
            count = 16;
        }

        uint baseAddress = ReadRegister(baseRegister, address);
        uint start = add
            ? baseAddress + (preIndexed ? 4u : 0u)
            : baseAddress - (uint)(count * 4) + (preIndexed ? 0u : 4u);
        uint current = start;
        for (int register = 0; register < 16; register++)
        {
            if ((list & (1 << register)) == 0)
                continue;
            if (load)
                _registers[register] = _memory.Read32(current);
            else
                _memory.Write32(current, ReadRegister(register, address));
            current += 4;
        }

        if (writeBack)
            _registers[baseRegister] = add ? baseAddress + (uint)(count * 4) : baseAddress - (uint)(count * 4);
        if (load && (list & 0x8000) != 0)
            AlignProgramCounter();
    }

    private void ExecuteThumb(ushort instruction, uint address)
    {
        uint next = address + 2;

        if ((instruction & 0xE000) == 0)
        {
            int operation = (instruction >> 11) & 3;
            if (operation < 3)
            {
                int amount = (instruction >> 6) & 0x1F;
                int source = (instruction >> 3) & 7;
                int destination = instruction & 7;
                (uint result, bool carry) = Shift(_registers[source], operation, amount, false, (_status & Carry) != 0);
                _registers[destination] = result;
                SetNz(result);
                SetCarry(carry);
            }
            else
            {
                bool immediate = (instruction & 0x0400) != 0;
                bool subtract = (instruction & 0x0200) != 0;
                uint left = _registers[(instruction >> 3) & 7];
                uint right = immediate ? (uint)((instruction >> 6) & 7) : _registers[(instruction >> 6) & 7];
                int destination = instruction & 7;
                uint result = subtract ? unchecked(left - right) : unchecked(left + right);
                _registers[destination] = result;
                SetArithmeticFlags(left, right, result, subtract, false);
            }
            return;
        }

        if ((instruction & 0xE000) == 0x2000)
        {
            int operation = (instruction >> 11) & 3;
            int destination = (instruction >> 8) & 7;
            uint immediate = (uint)(instruction & 0xFF);
            uint left = _registers[destination];
            uint result = operation switch
            {
                0 => immediate,
                1 => unchecked(left - immediate),
                2 => unchecked(left + immediate),
                _ => unchecked(left - immediate)
            };
            if (operation != 1)
                _registers[destination] = result;
            if (operation is 1 or 3)
                SetArithmeticFlags(left, immediate, result, true, false);
            else if (operation == 2)
                SetArithmeticFlags(left, immediate, result, false, false);
            else
                SetNz(result);
            return;
        }

        if ((instruction & 0xFC00) == 0x4000)
        {
            ExecuteThumbAlu(instruction);
            return;
        }

        if ((instruction & 0xFC00) == 0x4400)
        {
            int operation = (instruction >> 8) & 3;
            int destination = (instruction & 7) | ((instruction >> 4) & 8);
            int source = (instruction >> 3) & 0xF;
            if (operation == 3 && _inCallback && (_registers[source] & ~1u) == IrqReturnSentinel)
            {
                ReturnFromInterruptCallback();
                return;
            }
            if (operation == 3)
            {
                _status = (_status & ~0x20u) | ((_registers[source] & 1) != 0 ? 0x20u : 0);
                _registers[15] = _registers[source] & ((_status & 0x20) != 0 ? ~1u : ~3u);
            }
            else if (operation == 0)
            {
                _registers[destination] = ReadThumbRegister(destination, next) + ReadThumbRegister(source, next);
                if (destination == 15)
                    AlignProgramCounter();
            }
            else if (operation == 1)
            {
                uint left = ReadThumbRegister(destination, next);
                uint right = ReadThumbRegister(source, next);
                uint result = unchecked(left - right);
                SetArithmeticFlags(left, right, result, true, false);
            }
            else
            {
                _registers[destination] = ReadThumbRegister(source, next);
                if (destination == 15)
                    AlignProgramCounter();
            }
            return;
        }

        if ((instruction & 0xF800) == 0x4800)
        {
            int destination = (instruction >> 8) & 7;
            uint baseAddress = (next + 2) & ~3u;
            _registers[destination] = _memory.Read32(baseAddress + (uint)((instruction & 0xFF) * 4));
            return;
        }

        if ((instruction & 0xF000) == 0x5000)
        {
            ExecuteThumbRegisterTransfer(instruction);
            return;
        }

        if ((instruction & 0xE000) == 0x6000)
        {
            ExecuteThumbImmediateTransfer(instruction);
            return;
        }

        if ((instruction & 0xF000) == 0x8000)
        {
            int immediate = ((instruction >> 6) & 0x1F) * 2;
            uint transferAddress = _registers[(instruction >> 3) & 7] + (uint)immediate;
            int transferRegister = instruction & 7;
            if ((instruction & 0x0800) != 0)
                _registers[transferRegister] = _memory.Read16(transferAddress);
            else
                _memory.Write16(transferAddress, (ushort)_registers[transferRegister]);
            return;
        }

        if ((instruction & 0xF000) == 0x9000)
        {
            int destination = (instruction >> 8) & 7;
            uint transferAddress = _registers[13] + (uint)((instruction & 0xFF) * 4);
            if ((instruction & 0x0800) != 0)
                _registers[destination] = _memory.Read32(transferAddress);
            else
                _memory.Write32(transferAddress, _registers[destination]);
            return;
        }

        if ((instruction & 0xF000) == 0xA000)
        {
            int destination = (instruction >> 8) & 7;
            uint baseAddress = (instruction & 0x0800) != 0 ? _registers[13] : (next + 2) & ~3u;
            _registers[destination] = baseAddress + (uint)((instruction & 0xFF) * 4);
            return;
        }

        if ((instruction & 0xFF00) == 0xB000)
        {
            int offset = (instruction & 0x7F) * 4;
            _registers[13] = (instruction & 0x80) != 0
                ? _registers[13] - (uint)offset
                : _registers[13] + (uint)offset;
            return;
        }

        if ((instruction & 0xF600) == 0xB400)
        {
            bool pop = (instruction & 0x0800) != 0;
            bool extra = (instruction & 0x0100) != 0;
            ushort list = (ushort)(instruction & 0xFF);
            if (!pop)
            {
                if (extra)
                    _memory.Write32(_registers[13] -= 4, _registers[14]);
                for (int register = 7; register >= 0; register--)
                {
                    if ((list & (1 << register)) != 0)
                        _memory.Write32(_registers[13] -= 4, _registers[register]);
                }
            }
            else
            {
                for (int register = 0; register < 8; register++)
                {
                    if ((list & (1 << register)) != 0)
                    {
                        _registers[register] = _memory.Read32(_registers[13]);
                        _registers[13] += 4;
                    }
                }
                if (extra)
                {
                    _registers[15] = _memory.Read32(_registers[13]) & ~1u;
                    _registers[13] += 4;
                }
            }
            return;
        }

        if ((instruction & 0xF000) == 0xC000)
        {
            bool load = (instruction & 0x0800) != 0;
            int baseRegister = (instruction >> 8) & 7;
            ushort list = (ushort)(instruction & 0xFF);
            uint transferAddress = _registers[baseRegister];
            for (int register = 0; register < 8; register++)
            {
                if ((list & (1 << register)) == 0)
                    continue;
                if (load)
                    _registers[register] = _memory.Read32(transferAddress);
                else
                    _memory.Write32(transferAddress, _registers[register]);
                transferAddress += 4;
            }
            _registers[baseRegister] = transferAddress;
            return;
        }

        if ((instruction & 0xF000) == 0xD000)
        {
            int condition = (instruction >> 8) & 0xF;
            if (condition == 0xF)
            {
                SoftwareInterrupt((uint)(instruction & 0xFF));
            }
            else if (condition == 0xE)
            {
                _registers[15] = unchecked((uint)(next + 2 + (SignExtend((uint)(instruction & 0xFF), 8) << 1)));
            }
            else if (ConditionPassed(condition))
            {
                _registers[15] = unchecked((uint)(next + 2 + (SignExtend((uint)(instruction & 0xFF), 8) << 1)));
            }
            return;
        }

        if ((instruction & 0xF800) == 0xE000)
        {
            _registers[15] = unchecked((uint)(next + 2 + (SignExtend((uint)(instruction & 0x7FF), 11) << 1)));
            return;
        }

        if ((instruction & 0xF000) == 0xF000)
        {
            int offset = SignExtend((uint)(instruction & 0x7FF), 11) << 12;
            if ((instruction & 0x0800) == 0)
            {
                _registers[14] = unchecked((uint)(next + 2 + offset));
            }
            else
            {
                uint target = _registers[14] + ((uint)(instruction & 0x7FF) << 1);
                _registers[14] = next | 1;
                _registers[15] = target & ~1u;
            }
        }
    }

    private void ExecuteThumbAlu(ushort instruction)
    {
        int operation = (instruction >> 6) & 0xF;
        int destination = instruction & 7;
        uint left = _registers[destination];
        uint right = _registers[(instruction >> 3) & 7];
        uint result;
        switch (operation)
        {
            case 0: result = left & right; _registers[destination] = result; SetNz(result); break;
            case 1: result = left ^ right; _registers[destination] = result; SetNz(result); break;
            case 2: (result, bool lslCarry) = Shift(left, 0, (int)(right & 0xFF), true, (_status & Carry) != 0); _registers[destination] = result; SetNz(result); SetCarry(lslCarry); break;
            case 3: (result, bool lsrCarry) = Shift(left, 1, (int)(right & 0xFF), true, (_status & Carry) != 0); _registers[destination] = result; SetNz(result); SetCarry(lsrCarry); break;
            case 4: (result, bool asrCarry) = Shift(left, 2, (int)(right & 0xFF), true, (_status & Carry) != 0); _registers[destination] = result; SetNz(result); SetCarry(asrCarry); break;
            case 5: result = unchecked(left + right + ((_status & Carry) != 0 ? 1u : 0u)); _registers[destination] = result; SetArithmeticFlags(left, right, result, false, true); break;
            case 6: result = unchecked(left - right - ((_status & Carry) == 0 ? 1u : 0u)); _registers[destination] = result; SetArithmeticFlags(left, right, result, true, true); break;
            case 7: (result, bool rorCarry) = Shift(left, 3, (int)(right & 0xFF), true, (_status & Carry) != 0); _registers[destination] = result; SetNz(result); SetCarry(rorCarry); break;
            case 8: SetNz(left & right); break;
            case 9: result = unchecked(0u - right); _registers[destination] = result; SetArithmeticFlags(0, right, result, true, false); break;
            case 10: result = unchecked(left - right); SetArithmeticFlags(left, right, result, true, false); break;
            case 11: result = unchecked(left + right); SetArithmeticFlags(left, right, result, false, false); break;
            case 12: result = left | right; _registers[destination] = result; SetNz(result); break;
            case 13: result = unchecked(left * right); _registers[destination] = result; SetNz(result); break;
            case 14: result = left & ~right; _registers[destination] = result; SetNz(result); break;
            default: result = left | ~right; _registers[destination] = result; SetNz(result); break;
        }
    }

    private void ExecuteThumbRegisterTransfer(ushort instruction)
    {
        int operation = (instruction >> 9) & 7;
        uint address = _registers[(instruction >> 3) & 7] + _registers[(instruction >> 6) & 7];
        int transferRegister = instruction & 7;
        switch (operation)
        {
            case 0: _memory.Write32(address, _registers[transferRegister]); break;
            case 1: _memory.Write16(address, (ushort)_registers[transferRegister]); break;
            case 2: _memory.Write8(address, (byte)_registers[transferRegister]); break;
            case 3: _registers[transferRegister] = (uint)(int)(sbyte)_memory.Read8(address); break;
            case 4: _registers[transferRegister] = _memory.Read32(address); break;
            case 5: _registers[transferRegister] = _memory.Read16(address); break;
            case 6: _registers[transferRegister] = _memory.Read8(address); break;
            case 7: _registers[transferRegister] = (uint)(int)(short)_memory.Read16(address); break;
        }
    }

    private void ExecuteThumbImmediateTransfer(ushort instruction)
    {
        bool byteTransfer = (instruction & 0x1000) != 0;
        bool load = (instruction & 0x0800) != 0;
        int offset = (instruction >> 6) & 0x1F;
        uint address = _registers[(instruction >> 3) & 7] + (uint)(byteTransfer ? offset : offset * 4);
        int transferRegister = instruction & 7;
        if (load)
            _registers[transferRegister] = byteTransfer ? _memory.Read8(address) : _memory.Read32(address);
        else if (byteTransfer)
            _memory.Write8(address, (byte)_registers[transferRegister]);
        else
            _memory.Write32(address, _registers[transferRegister]);
    }

    private uint ReadRegister(int index, uint address) => index == 15 ? address + 8 : _registers[index];
    private uint ReadThumbRegister(int index, uint next) => index == 15 ? (next + 2) & ~3u : _registers[index];

    private bool ConditionPassed(int condition)
    {
        bool negative = (_status & Negative) != 0;
        bool zero = (_status & Zero) != 0;
        bool carry = (_status & Carry) != 0;
        bool overflow = (_status & Overflow) != 0;
        return condition switch
        {
            0 => zero,
            1 => !zero,
            2 => carry,
            3 => !carry,
            4 => negative,
            5 => !negative,
            6 => overflow,
            7 => !overflow,
            8 => carry && !zero,
            9 => !carry || zero,
            10 => negative == overflow,
            11 => negative != overflow,
            12 => !zero && negative == overflow,
            13 => zero || negative != overflow,
            14 => true,
            _ => false
        };
    }

    private (uint Value, bool Carry) Shift(uint value, int type, int amount, bool registerShift, bool oldCarry)
    {
        if (amount == 0)
        {
            if (registerShift)
                return (value, oldCarry);
            if (type == 0)
                return (value, oldCarry);
            if (type == 1)
                return (0, (value & Negative) != 0);
            if (type == 2)
                return ((uint)((int)value >> 31), (value & Negative) != 0);
            return ((oldCarry ? Negative : 0) | (value >> 1), (value & 1) != 0);
        }

        return type switch
        {
            0 when amount < 32 => (value << amount, ((value >> (32 - amount)) & 1) != 0),
            0 when amount == 32 => (0, (value & 1) != 0),
            0 => (0, false),
            1 when amount < 32 => (value >> amount, ((value >> (amount - 1)) & 1) != 0),
            1 when amount == 32 => (0, (value & Negative) != 0),
            1 => (0, false),
            2 when amount < 32 => ((uint)((int)value >> amount), ((value >> (amount - 1)) & 1) != 0),
            2 => ((value & Negative) != 0 ? uint.MaxValue : 0, (value & Negative) != 0),
            _ when amount % 32 == 0 => (value, (value & Negative) != 0),
            _ => (RotateRight(value, amount & 31), (RotateRight(value, amount & 31) & Negative) != 0)
        };
    }

    private void SetNz(uint result)
    {
        _status = (_status & ~(Negative | Zero)) | (result & Negative) | (result == 0 ? Zero : 0);
    }

    private void SetCarry(bool carry) => _status = carry ? _status | Carry : _status & ~Carry;

    private void SetArithmeticFlags(uint left, uint right, uint result, bool subtract, bool includeCarry)
    {
        SetNz(result);
        ulong carryInput = includeCarry && (_status & Carry) != 0 ? 1ul : 0ul;
        bool carry = subtract
            ? (ulong)left >= (ulong)right + (includeCarry && (_status & Carry) == 0 ? 1ul : 0ul)
            : (ulong)left + right + carryInput > uint.MaxValue;
        SetCarry(carry);
        bool overflow = subtract
            ? (((left ^ right) & (left ^ result) & Negative) != 0)
            : (((~(left ^ right)) & (left ^ result) & Negative) != 0);
        _status = overflow ? _status | Overflow : _status & ~Overflow;
    }

    private void AlignProgramCounter() => _registers[15] &= (_status & 0x20) != 0 ? ~1u : ~3u;

    private void EnterInterruptCallback()
    {
        uint handler = _memory.Read32(0x03007FFC);
        if (handler == 0)
            return;

        _interruptedInterrupts = (ushort)(_memory.EnabledInterrupts & _memory.PendingInterrupts);
        Array.Copy(_registers, _interruptedRegisters, _interruptedRegisters.Length);
        _interruptedStatus = _status;
        _interruptedPc = _registers[15];
        _interruptedSp = _registers[13];
        _interruptedLr = _registers[14];
        _interruptedIrqStackPointer = _irqStackPointer;
        _registers[13] = _irqStackPointer - 24;
        for (int register = 0; register <= 3; register++)
            _memory.Write32(_registers[13] + (uint)(register * 4), _registers[register]);
        _memory.Write32(_registers[13] + 16, _registers[12]);
        _memory.Write32(_registers[13] + 20, _registers[15] + 4);
        _registers[14] = IrqReturnSentinel | ((_status & 0x20) != 0 ? 1u : 0u);
        _status = (_status & ~0x3Fu) | 0x92;
        if ((handler & 1) != 0)
            _status |= 0x20;
        _registers[15] = handler & ((_status & 0x20) != 0 ? ~1u : ~3u);
        _inCallback = true;
    }

    private void ReturnFromInterruptCallback()
    {
        _memory.ClearInterruptFlags(_interruptedInterrupts);
        _irqStackPointer = _interruptedIrqStackPointer;
        Array.Copy(_interruptedRegisters, _registers, _interruptedRegisters.Length);
        _registers[13] = _interruptedSp;
        _registers[14] = _interruptedLr;
        _registers[15] = _interruptedPc;
        _status = _interruptedStatus;
        _inCallback = false;
    }

    private void SoftwareInterrupt(uint number)
    {
        switch (number)
        {
            case 0x02:
                BeginBiosWait(0x3FFF, false, true);
                break;
            case 0x03:
                BeginBiosWait(0x3FFF, false, true);
                break;
            case 0x04:
                BeginBiosWait((ushort)_registers[1], _registers[0] != 0);
                break;
            case 0x05:
                BeginBiosWait(1, true);
                break;
            case 0x00:
                _registers[13] = 0x03007F00;
                _registers[15] = 0x08000000;
                _status = 0x1F;
                break;
            case 0x01:
                ResetMemoryRegions(_registers[0]);
                break;
            case 0x06:
                Divide(_registers[0], _registers[1]);
                break;
            case 0x07:
                Divide(_registers[1], _registers[0]);
                break;
            case 0x08:
                _registers[0] = (uint)Math.Sqrt(_registers[0]);
                break;
            case 0x09:
                _registers[0] = unchecked((uint)(int)Math.Round(
                    Math.Atan(unchecked((int)_registers[0]) / 16384.0) * 16384.0));
                break;
            case 0x0A:
                double x = unchecked((int)_registers[0]);
                double y = unchecked((int)_registers[1]);
                _registers[0] = unchecked((uint)(int)Math.Round(Math.Atan2(y, x) * 32768.0 / Math.PI));
                _registers[1] = unchecked((uint)(int)Math.Round(Math.Sqrt(x * x + y * y)));
                break;
            case 0x0B:
            case 0x0C:
                CpuSet(number == 0x0C);
                break;
            case 0x11:
            case 0x12:
                Lz77Decompress(number == 0x12);
                break;
            case 0x14:
            case 0x15:
                RunLengthDecompress(number == 0x15);
                break;
        }
    }

    private void BeginBiosWait(ushort mask, bool clearPending, bool waitForEnabledInterrupt = false)
    {
        if (mask == 0)
            mask = 1;
        if (clearPending)
            _memory.ClearInterruptFlags(mask);
        else if (((waitForEnabledInterrupt
            ? (ushort)(_memory.PendingInterrupts & _memory.EnabledInterrupts)
            : _memory.PendingInterrupts) & mask) != 0)
            return;

        _biosWaitMask = mask;
        _biosWaitForEnabledInterrupt = waitForEnabledInterrupt;
        _biosWaiting = true;
    }

    private void Divide(uint numerator, uint denominator)
    {
        int dividend = unchecked((int)numerator);
        int divisor = unchecked((int)denominator);
        int quotient = divisor == 0
            ? dividend < 0 ? 1 : -1
            : dividend == int.MinValue && divisor == -1
                ? int.MinValue
                : dividend / divisor;
        int remainder = divisor == 0 ? dividend : dividend - quotient * divisor;
        _registers[0] = unchecked((uint)quotient);
        _registers[1] = unchecked((uint)remainder);
        _registers[3] = (uint)Math.Abs((long)quotient);
    }

    private void ResetMemoryRegions(uint flags)
    {
        if ((flags & 0x01) != 0)
        {
            for (uint address = 0x02000000; address < 0x02040000; address += 4)
                _memory.Write32(address, 0);
        }
        if ((flags & 0x02) != 0)
        {
            for (uint address = 0x03000000; address < 0x03008000; address += 4)
                _memory.Write32(address, 0);
        }
        if ((flags & 0x04) != 0)
        {
            for (uint address = 0x05000000; address < 0x05000400; address += 2)
                _memory.Write16(address, 0);
            for (uint address = 0x06000000; address < 0x06018000; address += 4)
                _memory.Write32(address, 0);
            for (uint address = 0x07000000; address < 0x07000400; address += 2)
                _memory.Write16(address, 0);
        }
        if ((flags & 0x08) != 0)
        {
            _memory.Write16(0x04000200, 0);
            _memory.Write16(0x04000208, 0);
            _memory.Write16(0x04000000, 0x0080);
        }
        if ((flags & 0x80) != 0)
        {
            for (uint address = 0x03007E00; address < 0x03008000; address += 4)
                _memory.Write32(address, 0);
        }
    }

    private void CpuSet(bool fast)
    {
        uint source = _registers[0];
        uint destination = _registers[1];
        uint control = _registers[2];
        bool fill = (control & (1u << 24)) != 0;
        bool word = fast || (control & (1u << 26)) != 0;
        int unit = word ? 4 : 2;
        int count = (int)(control & 0x1FFFFF);
        if (fast)
            count = (count + 7) & ~7;
        uint value = word ? _memory.Read32(source) : _memory.Read16(source);
        for (int i = 0; i < count; i++)
        {
            if (word)
                _memory.Write32(destination, value);
            else
                _memory.Write16(destination, (ushort)value);
            destination += (uint)unit;
            if (!fill)
            {
                source += (uint)unit;
                value = word ? _memory.Read32(source) : _memory.Read16(source);
            }
        }
    }

    private void Lz77Decompress(bool toVram)
    {
        uint source = _registers[0];
        uint destination = _registers[1];
        if ((_memory.Read8(source) & 0xF0) != 0x10)
            return;
        int length = _memory.Read8(source + 1) | (_memory.Read8(source + 2) << 8) | (_memory.Read8(source + 3) << 16);
        if (length > 16 * 1024 * 1024)
            return;
        source += 4;
        var output = new byte[length];
        int written = 0;
        while (written < length)
        {
            byte flags = _memory.Read8(source++);
            for (int bit = 7; bit >= 0 && written < length; bit--)
            {
                if ((flags & (1 << bit)) == 0)
                {
                    output[written++] = _memory.Read8(source++);
                    continue;
                }

                int first = _memory.Read8(source++);
                int second = _memory.Read8(source++);
                int count = (first >> 4) + 3;
                int displacement = ((first & 0x0F) << 8 | second) + 1;
                for (int i = 0; i < count && written < length; i++)
                {
                    int readAt = written - displacement;
                    output[written++] = readAt < 0 ? (byte)0 : output[readAt];
                }
            }
        }
        WriteDecompressed(destination, output, toVram);
    }

    private void RunLengthDecompress(bool toVram)
    {
        uint source = _registers[0];
        uint destination = _registers[1];
        if ((_memory.Read8(source) & 0xF0) != 0x30)
            return;
        int length = _memory.Read8(source + 1) | (_memory.Read8(source + 2) << 8) | (_memory.Read8(source + 3) << 16);
        if (length > 16 * 1024 * 1024)
            return;
        source += 4;
        var output = new byte[length];
        int written = 0;
        while (written < length)
        {
            byte header = _memory.Read8(source++);
            if ((header & 0x80) != 0)
            {
                int count = (header & 0x7F) + 3;
                byte value = _memory.Read8(source++);
                for (int i = 0; i < count && written < length; i++)
                    output[written++] = value;
            }
            else
            {
                int count = (header & 0x7F) + 1;
                for (int i = 0; i < count && written < length; i++)
                    output[written++] = _memory.Read8(source++);
            }
        }
        WriteDecompressed(destination, output, toVram);
    }

    private void WriteDecompressed(uint destination, byte[] output, bool toVram)
    {
        if (!toVram)
        {
            for (int i = 0; i < output.Length; i++)
                _memory.Write8(destination + (uint)i, output[i]);
            return;
        }

        for (int i = 0; i < output.Length; i += 2)
        {
            ushort value = output[i];
            if (i + 1 < output.Length)
                value |= (ushort)(output[i + 1] << 8);
            _memory.Write16(destination + (uint)i, value);
        }
    }

    private static uint RotateRight(uint value, int amount)
    {
        amount &= 31;
        return amount == 0 ? value : (value >> amount) | (value << (32 - amount));
    }

    private static int SignExtend(uint value, int bitCount) =>
        (int)(value << (32 - bitCount)) >> (32 - bitCount);
}
