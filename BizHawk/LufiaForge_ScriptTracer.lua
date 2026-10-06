-- ============================================================================
-- LufiaForge Script Tracer  (Lufia & The Fortress of Doom, US ROM)
-- ----------------------------------------------------------------------------
-- Records every event-script command and text segment the game runs, with the
-- exact ROM offset, so Lufia Forge can map the script language and link
-- dialogue to the characters/events that trigger it. Also captures battle
-- messages ("Surprise attack!", "... runs away!") from the battle text bank.
--
-- HOW TO USE
--   1. Core must be BSNES (Config > Preferred Cores > SNES > BSNES).
--   2. Load the Lufia ROM (in-game save via Continue is fine), then
--      Tools > Lua Console > Script > Open Script > this file.
--   3. Play normally: talk to townspeople, open chests, shop, rest at an inn,
--      enter a cutscene, fight a battle. The more variety, the better.
--      Tip: also talk to the same person twice, and to someone before and
--      after a story event.
--   4. Stop the script (or close BizHawk). Logs are written next to this script:
--        LufiaForge_ScriptTrace.log      every script start/command/text in order
--        LufiaForge_ScriptSummary.txt    one line per command id with examples
--
-- Expect the game to run a little slower while tracing.
--
-- ENGINE NOTES (from disassembly)
--   $01:C2DF  "run script" routine; its caller tells what triggered the script
--   $01:C63E  script start: offset T = WRAM $0D12-$0D14, ROM file offset
--             = 0x18000 + T. Script block base at $0D0F-$0D11.
--   $01:C66E  command loop: read byte; < $80 -> JMP ($C78C,X); else ranges at $C868
--   $01:C693  text loop: $00-$0F control (JSR ($C75C,X)), $10-$1F/$80-$FF MTE,
--             $20-$7F characters. Control $04 ends the text and returns to the script.
--   $01:C740  reads one script byte from DB:Y (wraps into the next bank)
--   Battle messages: bank $08 (file 0x40000-0x47FFF), 00-terminated, separate printer.
-- ============================================================================

local LOG_FILE     = "LufiaForge_ScriptTrace.log"
local SUMMARY_FILE = "LufiaForge_ScriptSummary.txt"
local OPERAND_PEEK = 8      -- bytes logged after each command byte
local MAX_TEXT     = 400    -- cap on bytes collected for one text segment

local log = io.open(LOG_FILE, "w")
if not log then
    console.log("[ScriptTracer] Could not open " .. LOG_FILE .. " for writing.")
    return
end

local frame          = 0
local lastScriptAddr = nil  -- bus address of the last byte read by $01:C740
local inText         = false
local textStart      = nil
local textBytes      = {}
local textCount, cmdCount, startCount, battleCount = 0, 0, 0, 0
local summary        = {}   -- key "CMD 1A" -> { n = count, ex = { "0x01A2F3", ... } }
local lastCmdOff     = nil  -- collapses commands that re-run every frame (waits)
local repeatCount    = 0
local lastStartKey   = nil  -- a paused script is resumed every frame; logged once
local startRepeats   = 0

-- battle text capture state (sequential reads from banks $08-$09 by one routine)
local bt             = nil  -- { pc, start, last, bytes }
local btLogged       = {}   -- start offset -> frame last logged (suppresses per-frame redraws)

-- ── helpers ────────────────────────────────────────────────────────────────

local function busToFile(addr)
    local bank = (addr >> 16) & 0x7F
    return bank * 0x8000 + (addr & 0x7FFF)
end

local function hexBytes(fileOff, n)
    local ok, bytes = pcall(memory.readbyterange, fileOff, n, "CARTROM")
    if not ok or not bytes then return "?" end
    local parts = {}
    for i = 0, n - 1 do
        local b = bytes[i]
        if b == nil then break end
        parts[#parts + 1] = string.format("%02X", b)
    end
    return table.concat(parts, " ")
end

local function wram8(addr)  return memory.read_u8(addr, "WRAM") end
local function wram16(addr) return wram8(addr) | (wram8(addr + 1) << 8) end
local function wram24(addr) return wram16(addr) | (wram8(addr + 2) << 16) end

local function note(key, fileOff)
    local s = summary[key]
    if not s then s = { n = 0, ex = {} }; summary[key] = s end
    s.n = s.n + 1
    if #s.ex < 6 then
        local ex = string.format("0x%06X", fileOff)
        for _, e in ipairs(s.ex) do if e == ex then return end end
        s.ex[#s.ex + 1] = ex
    end
end

local function writeLine(s)
    if log then log:write(s, "\n") end
end

local function render(bytes)
    local hex, asc = {}, {}
    for i, b in ipairs(bytes) do
        hex[i] = string.format("%02X", b)
        asc[i] = (b >= 0x20 and b < 0x7F) and string.char(b) or "."
    end
    return table.concat(hex, " "), table.concat(asc)
end

local function flushRepeats()
    if repeatCount > 0 then
        writeLine(string.format("      (previous command repeated %d more times)", repeatCount))
        repeatCount = 0
    end
end

-- cutoff: file offset of the byte that ended the text (the text loop peeks at it); dropped.
local function flushText(cutoff)
    if not inText then return end
    inText = false
    if textStart == nil or #textBytes == 0 then return end
    if cutoff and cutoff > textStart and cutoff - textStart < #textBytes then
        for i = #textBytes, cutoff - textStart + 1, -1 do textBytes[i] = nil end
    end
    flushRepeats()
    lastCmdOff = nil
    local hex, asc = render(textBytes)
    lastStartKey = nil
    writeLine(string.format("TEXT  f=%d  file=0x%06X  len=%d  [%s]  \"%s\"",
        frame, textStart, #textBytes, hex, asc))
    textCount = textCount + 1
    textStart, textBytes = nil, {}
end

local function flushBattle()
    if not bt then return end
    local b = bt
    bt = nil
    if #b.bytes < 4 then return end
    local printable = 0
    for _, v in ipairs(b.bytes) do
        if v >= 0x20 and v < 0x7F then printable = printable + 1 end
    end
    if printable * 10 < #b.bytes * 6 then return end          -- mostly not text
    local seen = btLogged[b.start]
    if seen and frame - seen < 120 then btLogged[b.start] = frame; return end
    btLogged[b.start] = frame
    flushRepeats()
    lastCmdOff = nil
    local hex, asc = render(b.bytes)
    writeLine(string.format("BATTLE f=%d  file=0x%06X  len=%d  pc=%04X  [%s]  \"%s\"",
        frame, b.start, #b.bytes, b.pc, hex, asc))
    note(string.format("BATTLE pc %04X", b.pc), b.start)
    battleCount = battleCount + 1
end

local function writeSummary()
    local f = io.open(SUMMARY_FILE, "w")
    if not f then return end
    f:write(string.format("LufiaForge script trace summary  (frame %d)\n", frame))
    f:write(string.format("scripts started: %d   commands: %d   text segments: %d   battle texts: %d\n\n",
        startCount, cmdCount, textCount, battleCount))
    local keys = {}
    for k in pairs(summary) do keys[#keys + 1] = k end
    table.sort(keys)
    for _, k in ipairs(keys) do
        local s = summary[k]
        f:write(string.format("%-16s count=%-7d examples: %s\n", k, s.n, table.concat(s.ex, ", ")))
    end
    f:close()
end

-- ── hooks ──────────────────────────────────────────────────────────────────

local function onRead(addr, val, flags)
    local bank = (addr >> 16) & 0x7F
    if (addr & 0xFFFF) < 0x8000 or bank < 0x03 or bank > 0x1F then return end
    local pc = emu.getregister("PC") or 0

    -- Battle text bank: collect sequential runs read by the same routine.
    if bank == 0x08 or bank == 0x09 then
        local fileOff = busToFile(addr)
        if bt and bt.pc == pc and fileOff == bt.last + 1 then
            bt.last = fileOff
            if #bt.bytes < MAX_TEXT then bt.bytes[#bt.bytes + 1] = memory.read_u8(fileOff, "CARTROM") end
        elseif not (bt and bt.pc == pc and fileOff == bt.last) then
            flushBattle()
            bt = { pc = pc & 0xFFFF, start = fileOff, last = fileOff,
                   bytes = { memory.read_u8(fileOff, "CARTROM") } }
        end
        return
    end

    -- Event scripts: track the address of every byte read by $01:C740.
    if (pc & 0xFFFF) ~= 0xC743 then return end
    lastScriptAddr = addr
    if inText then
        local fileOff = busToFile(addr)
        if textStart == nil then textStart = fileOff end
        if #textBytes < MAX_TEXT then
            textBytes[#textBytes + 1] = memory.read_u8(fileOff, "CARTROM")
        end
    end
end

-- $01:C63E: a script starts (NPC talk, event tile, map entry, cutscene, ...)
local function onScriptStart()
    flushText()
    flushBattle()
    local t    = wram24(0x0D12) & 0x7FFFFF
    local base = wram24(0x0D0F) & 0x7FFFFF
    local fileOff = 0x18000 + t
    -- Stack at $C63E entry: [S+1..2] return into $C2DF, [S+3] P pushed by $C2DF,
    -- [S+4..5] return address of whoever called $C2DF (identifies the trigger).
    local s = emu.getregister("S") or 0
    local caller = (wram16((s + 4) & 0xFFFF) + 1) & 0xFFFF
    startCount = startCount + 1
    -- A paused script (e.g. waiting) is resumed every frame; log it once.
    local key = fileOff * 0x10000 + caller
    if key == lastStartKey then startRepeats = startRepeats + 1; return end
    flushRepeats()
    lastStartKey, lastCmdOff = key, nil
    writeLine(string.format(
        "START f=%d  file=0x%06X  T=0x%06X  block=0x%06X  caller=$%04X  map?=%04X  bytes: %s",
        frame, fileOff, t, 0x18000 + base, caller, wram16(0x0010), hexBytes(fileOff, 16)))
    note(string.format("START caller %04X", caller), fileOff)
end

local function logCommand(kind, keyPrefix)
    if not lastScriptAddr then return end
    local fileOff = busToFile(lastScriptAddr)
    flushText(fileOff)
    local id = memory.read_u8(fileOff, "CARTROM")
    note(string.format("%s %02X", keyPrefix, id), fileOff)
    cmdCount = cmdCount + 1
    if fileOff == lastCmdOff then repeatCount = repeatCount + 1; return end
    flushRepeats()
    lastCmdOff = fileOff
    -- keep the start marker while the resumed script sits on its first command (a wait)
    if not lastStartKey or fileOff ~= lastStartKey // 0x10000 then lastStartKey = nil end
    writeLine(string.format("%s f=%d  file=0x%06X  id=%02X  bytes: %s",
        kind, frame, fileOff, id, hexBytes(fileOff, 1 + OPERAND_PEEK)))
end

-- $01:C67A: command < $80 about to dispatch through $C78C
local function onCommand() logCommand("CMD  ", "CMD") end

-- $01:C67D: command >= $80 (range-dispatched through $C868)
local function onHighCommand() logCommand("HCMD ", "HCMD") end

-- $01:C693: text loop iteration. The first one after a command starts a text segment.
local function onTextLoop()
    if not inText then
        inText = true
        textStart, textBytes = nil, {}
    end
end

-- $01:C6BA: text control code $00-$0F (X = code*2)
local function onTextControl()
    local x = emu.getregister("X") or 0
    local code = (x & 0xFF) >> 1
    note(string.format("TEXTCTL %02X", code), lastScriptAddr and busToFile(lastScriptAddr) or 0)
end

local hookNames = {}
local function hookExec(fn, addr, name)
    for _, bank in ipairs({ 0x01, 0x81 }) do
        local full = (bank << 16) | addr
        local ok, err = pcall(event.onmemoryexecute, fn, full, name, "System Bus")
        if not ok then
            console.log(string.format("[ScriptTracer] exec hook $%06X failed: %s", full, tostring(err)))
        end
    end
    hookNames[#hookNames + 1] = name
end

local okRead, readErr = pcall(event.onmemoryread, onRead, nil, "lf_script_read", "System Bus")
if not okRead then
    console.log("[ScriptTracer] Read hook failed (is the core BSNES?): " .. tostring(readErr))
    log:close()
    return
end
hookExec(onScriptStart, 0xC63E, "lf_script_start")
hookExec(onCommand,     0xC67A, "lf_script_cmd")
hookExec(onHighCommand, 0xC67D, "lf_script_hcmd")
hookExec(onTextLoop,    0xC693, "lf_text_loop")
hookExec(onTextControl, 0xC6BA, "lf_text_ctl")

writeLine("# LufiaForge script trace. Offsets are headerless ROM file offsets.")
writeLine("# START = script begins (caller = routine that started it), CMD/HCMD = script command,")
writeLine("# TEXT = dialogue segment, BATTLE = battle message (raw bytes; compressed words show as '.').")

local function shutdown()
    flushText()
    flushBattle()
    flushRepeats()
    writeSummary()
    if log then log:close(); log = nil end
    while event.unregisterbyname("lf_script_read") do end
    for _, n in ipairs(hookNames) do
        while event.unregisterbyname(n) do end
    end
    console.log("[ScriptTracer] Stopped. Logs saved next to the script.")
end
event.onexit(shutdown)

console.log("[ScriptTracer] Tracing. Play normally; stop the script when done.")

while true do
    frame = frame + 1
    gui.text(2, 2, string.format("Script trace: %d scripts  %d cmds  %d texts  %d battle",
        startCount, cmdCount, textCount, battleCount))
    if frame % 600 == 0 then
        writeSummary()
        if log then log:flush() end
    end
    emu.frameadvance()
end
