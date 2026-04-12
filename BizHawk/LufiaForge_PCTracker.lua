-- LufiaForge PC Tracker
-- Writes the live 65816 program counter into the top 3 bytes of WRAM ($7FFF00-02)
-- so the LufiaForge Monitor external tool can stream it to the disassembler.
--
-- Load via: BizHawk > Tools > Lua Console > Open Script

local WRAM_OFFSET = 0x1FF00   -- $7FFF00 relative to WRAM domain start ($7E0000)

local function writePCToWram()
    local pb = emu.getregister("PB") or 0
    local pc = emu.getregister("PC") or 0

    -- Some cores return the full 24-bit address in "PC" with no separate "PB"
    if pc > 0xFFFF then
        pb = math.floor(pc / 0x10000)
        pc = pc % 0x10000
    end

    memory.write_u8(WRAM_OFFSET,     pb,                         "WRAM")
    memory.write_u8(WRAM_OFFSET + 1, pc % 256,                   "WRAM")
    memory.write_u8(WRAM_OFFSET + 2, math.floor(pc / 256) % 256, "WRAM")
end

event.onframestart(writePCToWram)
print("[LufiaForge] PC tracker active — writing PC to WRAM $7FFF00")
