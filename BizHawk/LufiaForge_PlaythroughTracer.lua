-- ============================================================================
-- LufiaForge Playthrough Tracer  (Lufia & The Fortress of Doom, US ROM)
-- ----------------------------------------------------------------------------
-- Records what the game reads and does while you play, so Lufia Forge can build
-- editors for maps, events/dialogue, battles, graphics and music.
--
-- HOW TO USE
--   1. Core must be BSNES (Config > Preferred Cores > SNES > BSNES).
--   2. Load the Lufia ROM, then Tools > Lua Console > Script > Open Script > this file.
--      (Load your save or savestate before or after; both are fine.)
--   3. Play normally. Every launch writes NEW files next to this script, named with
--      the date and time, so sessions never overwrite each other:
--        LufiaForge_Trace_<time>.log     story/events: map changes, scripts, dialogue,
--                                        battle & menu text, monsters, RAM/flag changes
--        LufiaForge_Data_<time>.log      data locations: ROM reads during loads (bursts),
--                                        graphics/palette DMA, music/sound commands, RAM dumps
--        LufiaForge_Summary_<time>.txt   counts per command, maps visited, monsters seen
--   4. Stop the script or close BizHawk when done (files are also saved every 10 s).
--
-- PERFORMANCE: normal play runs at full speed. The first time something new loads
-- (a new map, battle graphics, a new conversation) the game slows down for a second
-- or two while its data reads are recorded. That's expected.
--
-- ENGINE NOTES (see research-script-engine.md in the Lufia Forge repo)
--   WRAM $0D15 = current map.  Map script table: file 0x18200 + map*5.
--   $01:C63E script start (T = WRAM $0D12, file = 0x18000 + T; event index in $16)
--   $01:C740 script byte read (DB:Y)   $01:C66E command loop   $01:C693 text loop
--   $00:86B1 general text printer (battle/menu): LDA [$1F],Y
--   $88:FAD0 monster record lookup (A = monster id)
--   $00:A47D resource unpacker: id = WRAM $16, pointer table at file 0x60000 + id*3,
--            output to $1E:$1C. Format: see research-script-engine.md ("Compression").
--   $00:8E13 loads resource A (< $25; probably music/sound data)
-- ============================================================================

local STAMP        = os.date("%Y%m%d_%H%M%S")
local TRACE_FILE   = "LufiaForge_Trace_" .. STAMP .. ".log"
local DATA_FILE    = "LufiaForge_Data_" .. STAMP .. ".log"
local SUMMARY_FILE = "LufiaForge_Summary_" .. STAMP .. ".txt"

local OPERAND_PEEK   = 8       -- bytes logged after each script command byte
local MAX_TEXT       = 400     -- cap on one text segment
local RAM_DIFF_EVERY = 30      -- frames between RAM change scans
local RAM_SCAN_FROM  = 0x0100  -- WRAM $0100-$1DFF is scanned for state/flag changes
local RAM_SCAN_TO    = 0x1DFF  -- ($0000-$00FF is scratch, $1E00+ is the stack)
local RAM_RARE_LIMIT = 6       -- a byte is logged until it has changed this many times
local RAM_SETTLE     = 120     -- frames after a map change before RAM changes are logged again
local MAP_ADDR       = 0x0D15  -- current map number
local MAP_DEBOUNCE   = 20      -- frames a new map number must hold before it counts
local RAM_DUMP_EVERY = 18000   -- full low-RAM dump every 5 minutes
local BURST_MAP      = 150     -- frames of ROM-read profiling after entering a new map
local BURST_LOAD     = 75      -- ... after a new large graphics upload / first battle on a map
local BURST_EVENT    = 30      -- ... after a new conversation/event starts
local MAP_PROFILE_VISITS = 1   -- profile a map's load this many times
local BURST_MAX_SECONDS  = 2.0 -- a recording never slows the game for longer than this (wall clock)
local BURST_MAX_RANGES   = 4000 -- distinct ROM ranges kept per recording

local trace = io.open(TRACE_FILE, "w")
local data  = io.open(DATA_FILE, "w")
if not trace or not data then
    console.log("[Tracer] Could not create log files next to the script.")
    return
end

-- ── state ──────────────────────────────────────────────────────────────────
local frame          = 0
local curMap         = nil
local scriptBank     = nil     -- tracked DB of the script reader (nil = unknown)
local lastScriptFile = nil
local inText, textStart, textBytes = false, nil, {}
local lastCmdOff, repeatCount = nil, 0
local lastStartKey   = nil
local counts = { start = 0, cmd = 0, text = 0, print = 0, monster = 0, map = 0, ram = 0, burst = 0, dma = 0 }
local summary  = {}            -- "CMD 1A" -> { n, ex }
local mapVisits, monstersSeen, eventsSeen = {}, {}, {}
local printRun, printLogged = nil, {}
local lastMonsterFrame = -10000
local ramPrev, ramChanges = nil, {}
local dmaRegs, vramAddr, cgAddr = {}, 0, 0
local dmaSeen, apuSeen, apuLast = {}, {}, {}
local largeLoadsSeen, eventsProfiled, battleProfiled = {}, {}, {}
local pendingMap, pendingSince, lastMapChange = nil, 0, -100000
local ramRing, ramRingPos = {}, 0   -- recent snapshots of WRAM $0000-$0FFF (for map exits)
local SNAP_SIZE = 0x1000             -- includes the actor table at $0800 (32 bytes per actor)
local burst = nil              -- { reason, untilFrame, startFrame, runs }

-- ── helpers ────────────────────────────────────────────────────────────────
local function busToFile(addr) return ((addr >> 16) & 0x7F) * 0x8000 + (addr & 0x7FFF) end
local function isRom(addr) return ((addr >> 16) & 0x7F) < 0x20 and (addr & 0xFFFF) >= 0x8000 end
local function rom8(fileOff) return memory.read_u8(fileOff, "CARTROM") end
local function wram8(a)  return memory.read_u8(a, "WRAM") end
local function wram16(a) return wram8(a) | (wram8(a + 1) << 8) end
local function wram24(a) return wram16(a) | (wram8(a + 2) << 16) end
local function reg(r) return emu.getregister(r) or 0 end

local function hexRom(fileOff, n)
    local t = {}
    for i = 0, n - 1 do t[#t + 1] = string.format("%02X", rom8(fileOff + i)) end
    return table.concat(t, " ")
end

local function hexWram(a, n)
    local bytes = memory.readbyterange(a, n, "WRAM")
    local t = {}
    for i = 0, n - 1 do t[#t + 1] = string.format("%02X", bytes[i] or 0) end
    return table.concat(t)
end

local function render(bytes)
    local hex, asc = {}, {}
    for i, b in ipairs(bytes) do
        hex[i] = string.format("%02X", b)
        asc[i] = (b >= 0x20 and b < 0x7F) and string.char(b) or "."
    end
    return table.concat(hex, " "), table.concat(asc)
end

local function T(s) if trace then trace:write(s, "\n") end end
local function D(s) if data then data:write(s, "\n") end end
local function mapStr() return curMap and string.format("%02X", curMap) or "??" end

local function note(key, fileOff)
    local s = summary[key]
    if not s then s = { n = 0, ex = {} }; summary[key] = s end
    s.n = s.n + 1
    if fileOff and #s.ex < 6 then
        local ex = string.format("0x%06X", fileOff)
        for _, e in ipairs(s.ex) do if e == ex then return end end
        s.ex[#s.ex + 1] = ex
    end
end

-- ── burst profiler (ROM data reads, only while a burst is active) ─────────
local function burstRead(addr, val, flags)
    if not burst or not isRom(addr) then return end
    local pc = reg("PC")
    local bank = (addr >> 16) & 0x7F
    -- skip instruction fetches/operands and the readers that are traced elsewhere
    if ((pc >> 16) & 0x7F) == bank and math.abs((pc & 0xFFFF) - (addr & 0xFFFF)) < 5 then return end
    local pcl = pc & 0xFFFF
    if pcl == 0xC743 or pcl == 0x86B3 then return end
    local f = busToFile(addr)
    local r = burst.runs[pc]
    if not r then r = { list = {} }; burst.runs[pc] = r end
    if r.start and f == r.last + 1 then r.last = f
    elseif r.start and f == r.last then
    else
        if r.start then
            -- numeric key (start, length); formatted only when the recording ends
            local k = r.start * 0x10000 + (r.last - r.start + 1)
            local c = r.list[k]
            if c then r.list[k] = c + 1
            elseif burst.ranges < BURST_MAX_RANGES then r.list[k] = 1; burst.ranges = burst.ranges + 1 end
        end
        r.start, r.last = f, f
    end
end

local function endBurst(why)
    if not burst then return end
    while event.unregisterbyname("lf_burst_read") do end
    D(string.format("BURST end f=%d  #%d  reason=%s  map=%s  frames=%d-%d%s%s",
        frame, counts.burst, burst.reason, mapStr(), burst.startFrame, frame,
        why and ("  (" .. why .. ")") or "",
        burst.ranges >= BURST_MAX_RANGES and "  (range limit reached)" or ""))
    local pcs = {}
    for pc, r in pairs(burst.runs) do
        if r.start then
            local k = r.start * 0x10000 + (r.last - r.start + 1)
            r.list[k] = (r.list[k] or 0) + 1
        end
        pcs[#pcs + 1] = pc
    end
    table.sort(pcs)
    for _, pc in ipairs(pcs) do
        local keys = {}
        for k in pairs(burst.runs[pc].list) do keys[#keys + 1] = k end
        table.sort(keys)
        local items = {}
        for _, k in ipairs(keys) do
            local n = burst.runs[pc].list[k]
            items[#items + 1] = string.format("%06X+%d", k // 0x10000, k % 0x10000) .. (n > 1 and ("x" .. n) or "")
        end
        local total = #items
        if total > 60 then
            local t = {}
            for i = 1, 60 do t[i] = items[i] end
            items = t
            items[#items + 1] = string.format("...(%d ranges)", total)
        end
        D(string.format("  READ pc=%06X  %s", pc, table.concat(items, " ")))
    end
    burst = nil
end

local function startBurst(reason, frames)
    if burst then
        burst.untilFrame = math.max(burst.untilFrame, frame + frames)
        if not burst.reason:find(reason, 1, true) then burst.reason = burst.reason .. "+" .. reason end
        return
    end
    counts.burst = counts.burst + 1
    burst = { reason = reason, untilFrame = frame + frames, startFrame = frame, runs = {}, ranges = 0, t0 = os.clock() }
    D(string.format("BURST start f=%d  #%d  reason=%s  map=%s", frame, counts.burst, reason, mapStr()))
    event.onmemoryread(burstRead, nil, "lf_burst_read", "System Bus")
end

-- ── script engine ──────────────────────────────────────────────────────────
local function flushRepeats()
    if repeatCount > 0 then
        T(string.format("      (previous repeated %d more times)", repeatCount))
        repeatCount = 0
    end
end

local function flushText(cutoff)
    if not inText then return end
    inText = false
    if textStart == nil or #textBytes == 0 then return end
    if cutoff and cutoff > textStart and cutoff - textStart < #textBytes then
        for i = #textBytes, cutoff - textStart + 1, -1 do textBytes[i] = nil end
    end
    flushRepeats()
    lastCmdOff, lastStartKey = nil, nil
    local hex, asc = render(textBytes)
    T(string.format("TEXT  f=%d  map=%s  file=0x%06X  len=%d  speaker=%d  [%s]  \"%s\"",
        frame, mapStr(), textStart, #textBytes, wram8(0x0D0D), hex, asc))
    counts.text = counts.text + 1
    textStart, textBytes = nil, {}
end

local function onScriptStart()
    flushText()
    local t = wram24(0x0D12) & 0x7FFFFF
    local fileOff = 0x18000 + t
    scriptBank = 3 + (t >> 15)
    local s = reg("S")
    local caller = (wram16((s + 4) & 0xFFFF) + 1) & 0xFFFF
    local ev = wram8(0x16)
    counts.start = counts.start + 1
    local key = fileOff * 0x10000 + caller
    if key == lastStartKey then repeatCount = repeatCount + 1; return end
    flushRepeats()
    lastStartKey, lastCmdOff = key, nil
    T(string.format("START f=%d  map=%s  event=%02X  file=0x%06X  T=0x%06X  block=0x%06X  caller=$%04X  bytes: %s",
        frame, mapStr(), ev, fileOff, t, 0x18000 + (wram24(0x0D0F) & 0x7FFFFF), caller, hexRom(fileOff, 16)))
    note(string.format("START caller %04X", caller), fileOff)
    if caller ~= 0x8429 then
        local ek = string.format("%s:%02X:%06X", mapStr(), ev, fileOff)
        eventsSeen[ek] = (eventsSeen[ek] or 0) + 1
        if not eventsProfiled[ek] then
            -- actor table at the moment of a new conversation/event: ties the event to a character
            D(string.format("ACTORS f=%d  map=%s  event=%02X  script=0x%06X  %s", frame, mapStr(), ev, fileOff, hexWram(0x0800, 0x400)))
            eventsProfiled[ek] = true
            startBurst(string.format("event map=%s ev=%02X", mapStr(), ev), BURST_EVENT)
        end
    end
end

local function onScriptBank() scriptBank = reg("A") & 0xFF end

-- $01:C743 runs right after LDA $0000,Y: A = byte read, Y = its address in bank DB.
local function onScriptByte()
    local a = reg("A") & 0xFF
    local y = reg("Y") & 0xFFFF
    local fileOff = scriptBank and ((scriptBank & 0x7F) * 0x8000 + (y & 0x7FFF)) or nil
    if not fileOff or rom8(fileOff) ~= a then
        -- lost track of the bank (e.g. a nested script returned): find the bank holding this byte
        local found = nil
        for b = 3, 0x1F do
            if rom8(b * 0x8000 + (y & 0x7FFF)) == a then
                if found then found = nil; break end
                found = b
            end
        end
        scriptBank = found
        fileOff = found and (found * 0x8000 + (y & 0x7FFF)) or nil
    end
    lastScriptFile = fileOff
    if inText and fileOff then
        if textStart == nil then textStart = fileOff end
        if #textBytes < MAX_TEXT then textBytes[#textBytes + 1] = a end
    end
end

local function logCommand(kind, prefix)
    if not lastScriptFile then return end
    local fileOff = lastScriptFile
    flushText(fileOff)
    local id = rom8(fileOff)
    note(string.format("%s %02X", prefix, id), fileOff)
    counts.cmd = counts.cmd + 1
    if fileOff == lastCmdOff then repeatCount = repeatCount + 1; return end
    flushRepeats()
    lastCmdOff = fileOff
    if not lastStartKey or fileOff ~= lastStartKey // 0x10000 then lastStartKey = nil end
    T(string.format("%s f=%d  map=%s  file=0x%06X  id=%02X  bytes: %s",
        kind, frame, mapStr(), fileOff, id, hexRom(fileOff, 1 + OPERAND_PEEK)))
end

local function onCommand()     logCommand("CMD  ", "CMD") end
local function onHighCommand() logCommand("HCMD ", "HCMD") end
local function onTextLoop()
    if not inText then inText = true; textStart, textBytes = nil, {} end
end
local function onTextControl()
    note(string.format("TEXTCTL %02X", (reg("X") & 0xFF) >> 1), lastScriptFile)
end

-- ── general text printer (battle / menu messages) ──────────────────────────
local function flushPrint()
    local r = printRun
    printRun = nil
    if not r or #r.bytes < 3 then return end
    local letters = 0
    for _, b in ipairs(r.bytes) do
        if (b >= 0x41 and b <= 0x5A) or (b >= 0x61 and b <= 0x7A) then letters = letters + 1 end
    end
    if letters < 2 then return end
    local last = printLogged[r.start]
    printLogged[r.start] = frame
    if last and frame - last < 1800 then return end
    flushRepeats()
    lastCmdOff, lastStartKey = nil, nil
    local hex, asc = render(r.bytes)
    T(string.format("PRINT f=%d  map=%s  file=0x%06X  len=%d  [%s]  \"%s\"", frame, mapStr(), r.start, #r.bytes, hex, asc))
    counts.print = counts.print + 1
    note("PRINT", r.start)
    if r.start == 0x0412B4 then startBurst("level up", 60) end   -- "'s level is up!"
end

local function onPrintChar()
    local d = reg("D")
    local ptr = wram24((d + 0x1F) & 0xFFFF)
    local addr = (ptr + reg("Y")) & 0xFFFFFF
    if not isRom(addr) then return end
    local f = busToFile(addr)
    if printRun and f == printRun.last + 1 then
        printRun.last = f
    elseif printRun and f == printRun.last then
        return
    else
        flushPrint()
        printRun = { start = f, last = f, bytes = {} }
    end
    local b = rom8(f)
    if #printRun.bytes < MAX_TEXT then printRun.bytes[#printRun.bytes + 1] = b end
    if b == 0 then flushPrint() end
end

-- ── monsters ───────────────────────────────────────────────────────────────
local function onMonster()
    local id = reg("A") & 0xFF
    local caller = (wram16((reg("S") + 1) & 0xFFFF) + 1) & 0xFFFF
    if frame - lastMonsterFrame > 300 then
        -- also happens outside battle (title demo, some menus); caller tells them apart
        T(string.format("MONSTERS f=%d  map=%s  monster data loading (caller $%04X)", frame, mapStr(), caller))
        if not battleProfiled[mapStr()] then
            battleProfiled[mapStr()] = true
            startBurst("battle", BURST_LOAD)
        end
    end
    lastMonsterFrame = frame
    local base = 0x5800
    local ptr = rom8(base + id * 2) | (rom8(base + id * 2 + 1) << 8)
    local name = {}
    for i = 0, 9 do
        local c = rom8(base + ptr + i)
        name[#name + 1] = (c >= 0x20 and c < 0x7F) and string.char(c) or "."
    end
    local key = string.format("%s:%02X", mapStr(), id)
    if not monstersSeen[key] then
        monstersSeen[key] = 0
        T(string.format("MONSTER f=%d  map=%s  id=%02X  \"%s\"  caller=$%04X", frame, mapStr(), id, table.concat(name), caller))
    end
    monstersSeen[key] = monstersSeen[key] + 1
    counts.monster = counts.monster + 1
end

-- ── compressed resources ───────────────────────────────────────────────────
local function onUnpack()
    local id = wram16(0x16)
    local p = 0x60000 + id * 3
    local ptr = rom8(p) | (rom8(p + 1) << 8) | (rom8(p + 2) << 16)
    local dest = wram16(0x1C) | ((wram8(0x1E) | 0x7E) << 16)
    local caller = (wram16((reg("S") + 1) & 0xFFFF) + 1) & 0xFFFF   -- JSL: low word of return address
    local srcFile = isRom(ptr) and busToFile(ptr) or -1
    local size = srcFile >= 0 and (rom8(srcFile) | (rom8(srcFile + 1) << 8)) or 0
    T(string.format("RES  f=%d  map=%s  id=%02X  file=0x%06X  unpacked=%d  -> $%06X  caller=$%04X",
        frame, mapStr(), id, srcFile, size, dest, caller))
    note(string.format("RES %02X", id), srcFile)
end

local function onLoad8E13()
    local id = reg("A") & 0xFF
    D(string.format("LOAD8E13 f=%d  map=%s  id=%02X  caller=$%04X", frame, mapStr(), id,
        (wram16((reg("S") + 1) & 0xFFFF) + 1) & 0xFFFF))
end

-- ── hardware writes: DMA, VRAM/CGRAM address, APU ──────────────────────────
local function onDmaStart(addr, val)
    if not val or val == 0 then return end
    for ch = 0, 7 do
        if (val >> ch) & 1 == 1 then
            local r = dmaRegs[ch]
            local src = (r[2] or 0) | ((r[3] or 0) << 8) | ((r[4] or 0) << 16)
            local n = (r[5] or 0) | ((r[6] or 0) << 8)
            if n == 0 then n = 0x10000 end
            local dst = r[1] or 0
            local target = (dst == 0x18 or dst == 0x19) and string.format("VRAM:%04X", vramAddr)
                        or (dst == 0x22) and string.format("CGRAM:%02X", cgAddr)
                        or (dst == 0x04) and "OAM" or string.format("B:%02X", dst)
            local key = string.format("%s|%06X|%s|%d", mapStr(), src, target, n)
            -- small uploads from RAM (text box tiles, animation frames) carry no ROM location
            if not isRom(src) and n < 256 then key = nil end
            if key and not dmaSeen[key] then
                dmaSeen[key] = true
                counts.dma = counts.dma + 1
                local srcs = string.format("$%06X", src)
                if isRom(src) then srcs = srcs .. string.format("=file:%06X", busToFile(src)) end
                D(string.format("DMA f=%d  map=%s  mode=%02X  %s -> %s  len=%d", frame, mapStr(), r[0] or 0, srcs, target, n))
                if (dst == 0x18 or dst == 0x19) and n >= 0x800 then
                    local lk = string.format("%06X|%d", src, n)
                    if not largeLoadsSeen[lk] then
                        largeLoadsSeen[lk] = true
                        startBurst("gfx load", BURST_LOAD)
                    end
                end
            end
        end
    end
end

local function onApu(addr, val)
    local port = addr & 3
    if apuLast[port] == val then return end
    apuLast[port] = val
    local key = string.format("%s|%d|%02X", mapStr(), port, val)
    if apuSeen[key] then return end
    apuSeen[key] = true
    D(string.format("APU f=%d  map=%s  $214%d=%02X  (ports %02X %02X %02X %02X)", frame, mapStr(), port, val,
        apuLast[0] or 0, apuLast[1] or 0, apuLast[2] or 0, apuLast[3] or 0))
end

-- ── map changes and RAM state ──────────────────────────────────────────────
local function onMapChange(old, new)
    flushText(); flushPrint(); flushRepeats()
    lastCmdOff, lastStartKey = nil, nil
    counts.map = counts.map + 1
    mapVisits[new] = (mapVisits[new] or 0) + 1
    dmaSeen, apuSeen = {}, {}
    T(string.format("MAP f=%d  %s -> %02X  (visit %d)  script table entry: %s  leader: %s",
        frame, old and string.format("%02X", old) or "??", new, mapVisits[new], hexRom(0x18200 + new * 5, 5), hexWram(0x0800, 4)))
    D(string.format("MAP f=%d  %s -> %02X", frame, old and string.format("%02X", old) or "??", new))
    -- newest snapshot taken before the map number started changing = where the player left
    local before = nil
    for _, snap in pairs(ramRing) do
        if snap.f < pendingSince and (not before or snap.f > before.f) then before = snap end
    end
    if before then
        local t = {}
        for i = 0, SNAP_SIZE - 1 do t[#t + 1] = string.format("%02X", before.bytes[i] or 0) end
        D(string.format("  BEFORE f=%d RAM0000 %s", before.f, table.concat(t)))
    end
    D("  RAM0000 " .. hexWram(0x0000, SNAP_SIZE))
    D("  RAM0D00 " .. hexWram(0x0D00, 0x100))
    if mapVisits[new] <= MAP_PROFILE_VISITS then startBurst(string.format("map %02X", new), BURST_MAP) end
    lastMapChange = frame
    ramPrev = nil
end

local function scanRam()
    if frame - lastMapChange < RAM_SETTLE then ramPrev = nil; return end
    local n = RAM_SCAN_TO - RAM_SCAN_FROM + 1
    local now = memory.readbyterange(RAM_SCAN_FROM, n, "WRAM")
    if ramPrev then
        local changes = {}
        for i = 0, n - 1 do
            local o, v = ramPrev[i], now[i]
            if o ~= v then
                local a = RAM_SCAN_FROM + i
                local c = (ramChanges[a] or 0) + 1
                ramChanges[a] = c
                if c <= RAM_RARE_LIMIT then
                    changes[#changes + 1] = string.format("$%04X:%02X>%02X%s", a, o or 0, v or 0, c == RAM_RARE_LIMIT and "(muted)" or "")
                end
            end
        end
        if #changes > 0 then
            counts.ram = counts.ram + #changes
            for k = 1, #changes, 40 do
                local part = {}
                for q = k, math.min(k + 39, #changes) do part[#part + 1] = changes[q] end
                T(string.format("RAM f=%d  map=%s  %s", frame, mapStr(), table.concat(part, " ")))
            end
        end
    end
    ramPrev = now
end

-- ── summary ────────────────────────────────────────────────────────────────
local function writeSummary()
    local f = io.open(SUMMARY_FILE, "w")
    if not f then return end
    f:write(string.format("LufiaForge playthrough trace summary  (frame %d, session %s)\n", frame, STAMP))
    f:write(string.format("script starts %d  commands %d  dialogue %d  printed %d  monster lookups %d  map changes %d  RAM changes %d  bursts %d  DMA kinds %d\n\n",
        counts.start, counts.cmd, counts.text, counts.print, counts.monster, counts.map, counts.ram, counts.burst, counts.dma))
    local maps = {}
    for m in pairs(mapVisits) do maps[#maps + 1] = m end
    table.sort(maps)
    f:write("MAPS VISITED (map: visits)\n")
    for _, m in ipairs(maps) do f:write(string.format("  %02X: %d\n", m, mapVisits[m])) end
    f:write("\nEVENTS STARTED (map:event:script file -> times)\n")
    local ev = {}
    for k in pairs(eventsSeen) do ev[#ev + 1] = k end
    table.sort(ev)
    for _, k in ipairs(ev) do f:write(string.format("  %s -> %d\n", k, eventsSeen[k])) end
    f:write("\nMONSTERS (map:id -> lookups)\n")
    local ms = {}
    for k in pairs(monstersSeen) do ms[#ms + 1] = k end
    table.sort(ms)
    for _, k in ipairs(ms) do f:write(string.format("  %s -> %d\n", k, monstersSeen[k])) end
    f:write("\nCOMMANDS\n")
    local keys = {}
    for k in pairs(summary) do keys[#keys + 1] = k end
    table.sort(keys)
    for _, k in ipairs(keys) do
        local s = summary[k]
        f:write(string.format("  %-18s count=%-8d examples: %s\n", k, s.n, table.concat(s.ex, ", ")))
    end
    f:close()
end

-- ── hook registration ──────────────────────────────────────────────────────
local hookNames = {}
local function hookExec(fn, banks, addr, name)
    for _, bank in ipairs(banks) do
        local ok, err = pcall(event.onmemoryexecute, fn, (bank << 16) | addr, name, "System Bus")
        if not ok then console.log(string.format("[Tracer] exec hook %02X:%04X failed: %s", bank, addr, tostring(err))) end
    end
    hookNames[#hookNames + 1] = name
end
local function hookWrite(fn, addr, name)
    local ok, err = pcall(event.onmemorywrite, fn, addr, name, "System Bus")
    if not ok then console.log(string.format("[Tracer] write hook %06X failed: %s", addr, tostring(err))) end
    hookNames[#hookNames + 1] = name
end

local B01, B00, B08 = { 0x01, 0x81 }, { 0x00, 0x80 }, { 0x08, 0x88 }
hookExec(onScriptStart, B01, 0xC63E, "lf_start")
hookExec(onScriptBank,  B01, 0xC650, "lf_bank_start")
hookExec(onScriptBank,  B01, 0xC71B, "lf_bank_jump")
hookExec(onScriptBank,  B01, 0xC74E, "lf_bank_wrap")
hookExec(onScriptByte,  B01, 0xC743, "lf_byte")
hookExec(onCommand,     B01, 0xC67A, "lf_cmd")
hookExec(onHighCommand, B01, 0xC67D, "lf_hcmd")
hookExec(onTextLoop,    B01, 0xC693, "lf_textloop")
hookExec(onTextControl, B01, 0xC6BA, "lf_textctl")
hookExec(onPrintChar,   B00, 0x86B1, "lf_print")
hookExec(onMonster,     B08, 0xFAD0, "lf_monster")
hookExec(onUnpack,      B00, 0xA47D, "lf_unpack")
hookExec(onLoad8E13,    B00, 0x8E13, "lf_load8e13")

hookWrite(function(a, v) vramAddr = (vramAddr & 0xFF00) | v end, 0x002116, "lf_vram_lo")
hookWrite(function(a, v) vramAddr = (vramAddr & 0x00FF) | (v << 8) end, 0x002117, "lf_vram_hi")
hookWrite(function(a, v) cgAddr = v end, 0x002121, "lf_cgaddr")
for ch = 0, 7 do
    dmaRegs[ch] = {}
    for r = 0, 6 do
        local c, rr = ch, r
        hookWrite(function(a, v) dmaRegs[c][rr] = v end, 0x004300 + ch * 0x10 + r, string.format("lf_dma%d%d", ch, r))
    end
end
hookWrite(onDmaStart, 0x00420B, "lf_dma_go")
for p = 0, 3 do hookWrite(onApu, 0x002140 + p, "lf_apu" .. p) end

event.onloadstate(function()
    flushText(); flushPrint(); flushRepeats()
    scriptBank, lastScriptFile, lastCmdOff, lastStartKey = nil, nil, nil, nil
    ramPrev = nil
    lastMapChange = frame
    T(string.format("STATELOAD f=%d  (savestate loaded; map=%02X)", frame, wram8(MAP_ADDR)))
    D(string.format("STATELOAD f=%d", frame))
end, "lf_loadstate")

T("# LufiaForge playthrough trace " .. STAMP .. ". Offsets are headerless ROM file offsets.")
T("# MAP = map change (WRAM $0D15). START = script begins (event = WRAM $16, caller = what started it:")
T("#   $C275 new event/talk, $8429 resume, $8997 after battle). CMD/HCMD = script command.")
T("# TEXT = dialogue (speaker = actor slot, WRAM $0D0D). PRINT = battle/menu text.")
T("# MONSTERS/MONSTER = monster records looked up (battles, but also the title demo); first time per map+id.")
T("# RES = compressed resource unpacked (id, ROM location, unpacked size, destination RAM) - map/tileset/graphics data.")
T("# RAM = rarely-changing RAM bytes ($0100-$1DFF) that changed: $addr:old>new (story flags, items, party...).")
D("# LufiaForge data trace " .. STAMP .. ". BURST = ROM reads by routine (pc) while loading something new.")
D("# DMA = graphics/palette uploads (source -> VRAM word address / CGRAM color). APU = sound/music commands.")
D("# MAP: BEFORE RAM0000 = WRAM $0000-$0FFF just before leaving the old map; RAM0000/RAM0D00 = after arriving.")
D("# ACTORS = WRAM $0800-$0BFF (actor table, 32 bytes each; tile x/y at +0F/+11) when an event first starts.")
D("# RAMDUMP = full WRAM $0000-$1FFF every 5 minutes, for offline analysis.")

local function shutdown()
    flushText(); flushPrint(); flushRepeats()
    endBurst()
    writeSummary()
    if trace then trace:close(); trace = nil end
    if data then data:close(); data = nil end
    for _, n in ipairs(hookNames) do while event.unregisterbyname(n) do end end
    while event.unregisterbyname("lf_loadstate") do end
    console.log("[Tracer] Stopped. Logs saved next to the script (" .. STAMP .. ").")
end
event.onexit(shutdown)

console.log("[Tracer] Recording to LufiaForge_*_" .. STAMP .. ". Play normally; stop the script when done.")

-- ── main loop ──────────────────────────────────────────────────────────────
local function tick()
    frame = frame + 1
    local m = wram8(MAP_ADDR)
    if m ~= curMap then
        if m ~= pendingMap then pendingMap, pendingSince = m, frame
        elseif frame - pendingSince >= MAP_DEBOUNCE then
            local old = curMap
            curMap = m
            onMapChange(old, m)
        end
    else
        pendingMap = nil
    end
    if burst then
        if frame >= burst.untilFrame then endBurst()
        elseif os.clock() - burst.t0 > BURST_MAX_SECONDS then endBurst("cut short: time limit") end
    end
    if frame % 8 == 0 then
        ramRingPos = ramRingPos % 6 + 1
        ramRing[ramRingPos] = { f = frame, bytes = memory.readbyterange(0, SNAP_SIZE, "WRAM") }
    end
    if frame % RAM_DIFF_EVERY == 0 then scanRam() end
    if frame % RAM_DUMP_EVERY == 1 then D(string.format("RAMDUMP f=%d  map=%s  %s", frame, mapStr(), hexWram(0, 0x2000))) end
    if frame % 600 == 0 then
        writeSummary()
        if trace then trace:flush() end
        if data then data:flush() end
    end
    gui.text(2, 2, string.format("Trace map %s | %d scripts %d texts %d battles%s",
        mapStr(), counts.start, counts.text + counts.print, counts.monster, burst and " | recording load..." or ""))
end

while true do
    tick()
    emu.frameadvance()
end
