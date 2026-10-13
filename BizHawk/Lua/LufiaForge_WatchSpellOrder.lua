-- Lufia Forge: logs the spell order lists, the party names, the first 24 inventory slots and the field menu's swap rows
-- whenever they change,
-- so a test can be followed from outside BizHawk. Output: LufiaForge_SpellOrder.log next to this script (a new
-- section every time a ROM starts; the file is trimmed when it grows past about 2 MB).
local dir = (debug.getinfo(1, "S").source:match("^@(.*[/\\])") or "")
local path = dir .. "LufiaForge_SpellOrder.log"
local f = io.open(path, "r")
local size = f and f:seek("end") or 0
if f then f:close() end
local out = io.open(path, size > 2000000 and "w" or "a")
local function hex(dom, a, len)
  local t = {}
  for i = 0, len - 1 do t[#t + 1] = string.format("%02X", memory.read_u8(a + i, dom)) end
  return table.concat(t, " ")
end
local function names()
  local s = ""
  for i = 0, 23 do local c = memory.read_u8(0x14B6 + i, "WRAM"); s = s .. ((c >= 32 and c < 127) and string.char(c) or ".") end
  return s
end
local last = ""
out:write(string.format("=== %s  %s\n", os.date("%Y-%m-%d %H:%M:%S"), gameinfo.getromname())); out:flush()
while true do
  local ok, s = pcall(function()
    local snap = {}
    for k = 0, 3 do snap[#snap + 1] = "order " .. k .. ": " .. hex("CARTRAM", 0x1A00 + k * 64, 24) end
    snap[#snap + 1] = "names: " .. names()
    snap[#snap + 1] = "items (id qty): " .. hex("WRAM", 0x14F7, 48)
    snap[#snap + 1] = "swap rows $11/$1955/$1958/$1959: " .. hex("WRAM", 0x11, 1) .. " / " .. hex("WRAM", 0x1955, 1) .. " / " .. hex("WRAM", 0x1958, 2)
    return table.concat(snap, "\n")
  end)
  if ok and s ~= last then
    out:write(string.format("--- frame %d\n%s\n", emu.framecount(), s)); out:flush()
    last = s
  end
  emu.frameadvance()
end
