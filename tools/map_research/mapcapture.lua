-- Visits maps by redirecting the "Continue" map load, and captures per-map data.
-- Usage: set MAPS (list of map numbers) in mapcapture_list.lua; outputs go to capture/.
local DIR  = "C:/Users/Admin/AppData/Local/Temp/claude/D--Lufia-Forge/de85b578-325b-4922-8bd7-d0ea2372d399/scratchpad/trace/"
local OUT  = DIR .. "capture_all/"
local MAPS = dofile(DIR .. "mapcapture_list.lua")
local WAIT = 240

local log = io.open(OUT .. "capture_log.txt", "a")
local function L(s) log:write(s, "\n"); log:flush() end

local target, armedData, armedStore = nil, false, false
local frame = 0
local events = {}

local function wram8(a) return memory.read_u8(a, "WRAM") end
local function wram16(a) return wram8(a) | (wram8(a + 1) << 8) end

local function hookBoth(addr, name, fn)
    event.onmemoryexecute(fn, addr, name .. "0", "System Bus")
    event.onmemoryexecute(fn, addr | 0x800000, name .. "8", "System Bus")
end

-- after STA $077F in $01:BD70: choose which map's data gets loaded
hookBoth(0x01BD78, "cap_sel", function()
    if armedData then
        memory.write_u8(0x077F, target, "WRAM")
        armedData = false
    end
end)
-- after STA $0D15 in the map init stream: the map number the rest of the game uses
hookBoth(0x01999B, "cap_store", function()
    if armedStore then
        memory.write_u8(0x0D15, target, "WRAM")
        armedStore = false
    end
end)
-- resources and scripts that run while the map loads
hookBoth(0x00A47D, "cap_unpack", function()
    if target then events[#events + 1] = string.format("RES f=%d id=%03X dest=%02X:%04X", frame, wram16(0x16), wram8(0x1E) | 0x7E, wram16(0x1C)) end
end)
hookBoth(0x01C63E, "cap_start", function()
    if target then
        local t = wram16(0x0D12) | ((wram8(0x0D14) & 0x7F) << 16)
        events[#events + 1] = string.format("START f=%d event=%02X file=0x%06X", frame, wram8(0x16), 0x18000 + t)
    end
end)

local function dump(domain, addr, n, path)
    local f = io.open(path, "wb")
    for base = 0, n - 1, 0x1000 do
        local len = math.min(0x1000, n - base)
        local t = memory.readbyterange(addr + base, len, domain)
        local c = {}
        for i = 0, len - 1 do c[#c + 1] = string.char(t[i] or 0) end
        f:write(table.concat(c))
    end
    f:close()
end

client.speedmode(1600)
for _, m in ipairs(MAPS) do
    savestate.load(DIR .. "continue_ready.State")
    target, armedData, armedStore, events = m, true, true, {}
    local t0 = os.clock()
    local tag = string.format("%02X", m)
    local atCapture = "??"
    for i = 1, WAIT do
        joypad.set({ ["P1 A"] = (i <= 3) })
        emu.frameadvance()
        frame = i
        if i == 80 then
            -- map data, tiles and palette are loaded by ~frame 45; capture before anything moves the party
            atCapture = string.format("%02X/%02X", wram8(0x0D15), wram8(0x077F))
            dump("CGRAM", 0, 512, OUT .. "map" .. tag .. "_cgram.bin")
            dump("VRAM", 0, 0x10000, OUT .. "map" .. tag .. "_vram.bin")
            dump("WRAM", 0, 0x2000, OUT .. "map" .. tag .. "_wram.bin")
            client.screenshot(OUT .. "map" .. tag .. ".png")
        end
    end
    L(string.format("MAP %s  at capture (map/data)=%s  at end=%02X/%02X  armed(data=%s store=%s)  %.1fs",
        tag, atCapture, wram8(0x0D15), wram8(0x077F), tostring(armedData), tostring(armedStore), os.clock() - t0))
    for _, e in ipairs(events) do L("  " .. e) end
end
target = nil
log:close()
client.exit()
