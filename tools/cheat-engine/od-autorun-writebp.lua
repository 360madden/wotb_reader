--[============================================================================[
  WotB Treader — OD Autorun Write-BP Evidence Capture
  ===================================================

  Install a copy into the Cheat Engine autorun folder:
    C:\Program Files\Cheat Engine\autorun\od-autorun-writebp.lua

  CE 7.7 executes every .lua file in that folder automatically at startup
  (the folder's own note file documents this). The pre-armed CE instance
  launched by scripts/pre-arm-debugger.ps1 therefore runs this script the
  moment it starts, replacing the operator-owned interactive
  "Find out what writes this address" step with an automated capture:

    1. Only acts during an active OD campaign session: requires
       %TEMP%\od-prearmed-debugger.json (written by pre-arm-debugger.ps1).
       Without it, exits immediately and silently. The body is wrapped in
       main() with early returns — never os.exit(), because os.exit in CE
       Lua can terminate the entire Cheat Engine process on normal launches.
    2. Attaches to the running wotblitz.exe (no-op if already attached).
    3. Polls for the rolling driver's address file
       (%TEMP%\od-survivors.txt by default) for up to 90s. The session
       driver must delete that file before rolling so stale addresses from a
       prior run are never staged.
    4. Stages each survivor into CE's address list (like prearm-attach.lua).
    5. Starts CE's Windows debugger and sets a write breakpoint on up to 4
       survivors (bptWrite, trigger 1) — the x64 DR0-DR3 hardware limit.
       replayTime advances every frame, so writes would fire immediately.
    6. Logs each breakpoint hit (raw RIP hex) to %TEMP%\od-ce-hits.log and
       the whole run to %TEMP%\od-ce-autorun.log, capturing up to MAX_HITS
       hits total, then exits its loop.

  Known negative (OD-RECOVERY-020): CE 7.7's Windows-debugger write-BPs
  (debugProcess(1) + debug_setBreakpoint) produced zero hits across three
  live runs even during active playback, matching OD-009/010/011. The
  operator-owned interactive Find-what-writes step is therefore required;
  this script's value is the staging + arming automation it still provides.

  Privacy: only raw hex RIP addresses and aggregate counts are logged to
  %TEMP% (untracked). Nothing is written inside the repo.

  This script is inert outside an active OD session (no pre-arm marker,
  no game process, no address file -> silent exit).
--]============================================================================]

local LOG_ENABLED = true
local TEMP = os.getenv("TEMP") or "C:\\Windows\\Temp"
local PREARM_MARKER = TEMP .. "\\od-prearmed-debugger.json"
local DEFAULT_ADDRESS_FILE = TEMP .. "\\od-survivors.txt"
local RUN_MARKER = TEMP .. "\\od-ce-autorun.marker"
local LOG_FILE = TEMP .. "\\od-ce-autorun.log"
local HITS_FILE = TEMP .. "\\od-ce-hits.log"
local MAX_POLL_ROUNDS = 45   -- 90s at 2s interval
local POLL_INTERVAL_SECONDS = 2
local MAX_HITS = 20
local MAX_HW_BREAKPOINTS = 4  -- x64 DR0-DR3 hardware limit
local SET_BP_CANDIDATES = { "debug_setBreakpoint", "setBreakpoint", "createBreakpoint" }
local WAIT_BP_CANDIDATES = { "waitForBreakpoint", "debug_waitForBreakpoint", "waitForBreakpointHit" }
local READ_REG_CANDIDATES = { "getRegisterValue", "readRegister", "readRIP", "readEIP" }

local hitsCaptured = 0

local function fileLog(path, msg)
  local f = io.open(path, "a")
  if f then
    f:write(os.date("%H:%M:%S") .. " " .. msg .. "\n")
    f:close()
  end
end

local function log(msg)      if LOG_ENABLED then fileLog(LOG_FILE, msg) end end
local function logInfo(msg)  log("INFO: " .. msg) end
local function logWarn(msg)  log("WARN: " .. msg) end
local function logError(msg) log("ERROR: " .. msg) end

local function markerExists(path)
  local f = io.open(path, "r")
  if f then f:close() return true end
  return false
end

local function resolveFn(name)
  if _G and _G[name] then return _G[name] end
  local t = getfenv and getfenv(0) or _G
  return t and t[name]
end

-- Only functions returning numbers are useful; getRegisters (table) is
-- deliberately excluded to avoid string.format("%X", table) errors.
local function readRip()
  for _, name in ipairs(READ_REG_CANDIDATES) do
    local fn = resolveFn(name)
    if fn then
      local ok, val = pcall(fn, "RIP")
      if ok and type(val) == "number" and val > 0 then
        return string.format("%X", val)
      end
    end
  end
  return "unknown"
end

-- CE calls this global on every breakpoint hit (documented callback).
function onBreakpoint()
  hitsCaptured = hitsCaptured + 1
  fileLog(HITS_FILE, "hit#" .. tostring(hitsCaptured) .. " rip=0x" .. readRip())
  return true  -- continue execution
end

local function attachToGame()
  if getOpenedProcessID and getOpenedProcessID() > 0 then
    logInfo("already attached pid=" .. tostring(getOpenedProcessID()))
    return true
  end
  local pid = getProcessIDFromProcessName and getProcessIDFromProcessName("wotblitz.exe")
  if not pid or pid <= 0 then
    logWarn("wotblitz.exe not found")
    return false
  end
  logInfo("found wotblitz.exe pid=" .. tostring(pid))
  local ok = pcall(openProcess, "wotblitz.exe")
  if not ok or not getOpenedProcessID or getOpenedProcessID() <= 0 then
    logError("openProcess failed")
    return false
  end
  logInfo("attached pid=" .. tostring(getOpenedProcessID()))
  return true
end

local function loadSurvivors(path)
  local f = io.open(path, "r")
  if not f then return nil end
  local lines = {}
  for line in f:lines() do
    line = line:gsub("^%s+", ""):gsub("%s+$", "")
    if line ~= "" then lines[#lines + 1] = line end
  end
  f:close()
  if #lines == 0 then return nil end
  return lines
end

local function stageInAddressList(addresses)
  local al = getAddressList()
  if not al then
    logError("getAddressList() nil")
    return 0
  end
  local added = 0
  for _, addr in ipairs(addresses) do
    local hex = addr:gsub("^0[xX]", "")
    if hex:match("^[0-9a-fA-F]+$") then
      local mr = al.createMemoryRecord()
      mr.Description = "od-survivor-" .. tostring(added + 1)
      mr.Address = "0x" .. hex
      added = added + 1
    end
  end
  logInfo("staged " .. tostring(added) .. " survivors in address list")
  return added
end

local function armBreakpoints(addresses)
  local setBp = nil
  for _, name in ipairs(SET_BP_CANDIDATES) do
    local fn = resolveFn(name)
    if fn then
      setBp = fn
      logInfo("breakpoint API resolved: " .. name)
      break
    end
  end
  if not setBp then
    logError("no breakpoint API found in candidates: " .. table.concat(SET_BP_CANDIDATES, ","))
    return 0
  end

  -- x64 exposes only 4 hardware breakpoints; arm at most 4 survivors and
  -- skip the rest (logged) so the operator knows which were BP-armed.
  local armed = 0
  for _, addr in ipairs(addresses) do
    if armed >= MAX_HW_BREAKPOINTS then
      logWarn("skipped beyond HW limit: " .. addr)
    else
      local hex = addr:gsub("^0[xX]", "")
      if hex:match("^[0-9a-fA-F]+$") then
        local bOk, bErr = pcall(setBp, "0x" .. hex, bptWrite, 1)
        if bOk and bErr ~= false then
          armed = armed + 1
        else
          logWarn("breakpoint arm failed " .. hex .. " (" .. tostring(bErr) .. ")")
        end
      end
    end
  end
  return armed
end

local function captureHits(addresses)
  local ok, err = pcall(debugProcess, 1)  -- 1 = Windows debugger
  if not ok then
    logWarn("debugProcess unavailable: " .. tostring(err))
    return
  end
  logInfo("debugger attached")

  local armed = armBreakpoints(addresses)
  logInfo("write breakpoints armed=" .. tostring(armed))
  if armed == 0 then
    logWarn("no breakpoints armed; staging only")
    return
  end

  -- If onBreakpoint is the callback mechanism, hits are already being
  -- logged. The wait-loop below is a fallback for builds without it.
  local waitFn = nil
  for _, name in ipairs(WAIT_BP_CANDIDATES) do
    local fn = resolveFn(name)
    if fn then
      waitFn = fn
      logInfo("wait API resolved: " .. name)
      break
    end
  end

  if waitFn then
    local attempts = 0
    while attempts < 30 and hitsCaptured < MAX_HITS do
      local fired = pcall(waitFn)
      attempts = attempts + 1
      if not fired then
        logWarn("wait failed; relying on onBreakpoint callback")
        break
      end
      sleep(100)
    end
  else
    -- No wait API: give the onBreakpoint callback time to collect hits.
    logInfo("no wait API; polling onBreakpoint for 20s")
    for i = 1, 200 do
      if hitsCaptured >= MAX_HITS then break end
      sleep(100)
    end
  end
  logInfo("capture complete hits=" .. tostring(hitsCaptured))
end

local function main()
  -- Gate: only run inside an active OD campaign session.
  if not markerExists(PREARM_MARKER) then
    return  -- inert on normal CE launches; never os.exit (kills CE itself)
  end
  fileLog(RUN_MARKER, "autorun started")

  logInfo("========================================")
  logInfo("OD autorun write-BP capture started")
  logInfo("address file: " .. DEFAULT_ADDRESS_FILE)
  logInfo("========================================")

  if not attachToGame() then
    logError("cannot attach; exiting")
    return
  end

  logInfo("polling for survivor address file (max " .. tostring(MAX_POLL_ROUNDS * POLL_INTERVAL_SECONDS) .. "s)...")
  local found = false
  for i = 1, MAX_POLL_ROUNDS do
    if markerExists(DEFAULT_ADDRESS_FILE) then
      found = true
      break
    end
    sleep(POLL_INTERVAL_SECONDS * 1000)
  end

  if not found then
    logWarn("address file did not appear within poll window")
    return
  end

  local addresses = loadSurvivors(DEFAULT_ADDRESS_FILE)
  if not addresses then
    logWarn("address file empty; nothing staged")
    return
  end
  logInfo("loaded " .. tostring(#addresses) .. " survivor lines")

  stageInAddressList(addresses)
  captureHits(addresses)

  logInfo("autorun complete")
end

local okRun, runErr = pcall(main)
if not okRun then
  logError("autorun failed: " .. tostring(runErr))
end
