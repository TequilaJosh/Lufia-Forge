-- Creates continue_ready.State: the save-slot screen, one A-press away from loading the save.
local DIR = "C:/Users/Admin/AppData/Local/Temp/claude/D--Lufia-Forge/de85b578-325b-4922-8bd7-d0ea2372d399/scratchpad/trace/"
client.speedmode(800)
local function run(n, btn) for i = 1, n do joypad.set(btn and { ["P1 " .. btn] = (i <= 3) } or {}) emu.frameadvance() end end
run(400) run(120, "Start") run(60, "A")
savestate.save(DIR .. "continue_ready.State")
client.screenshot(DIR .. "shots/continue_ready.png")
client.exit()
