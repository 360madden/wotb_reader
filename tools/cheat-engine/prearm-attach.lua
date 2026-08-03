--[============================================================================[
  WotB Treader — Pre-Arm Attach (OD-RECOVERY-017)
  =================================================

  Pair this with scripts/roll-replay-time-increased.ps1 -AddressFile <path>.

  Load it in Cheat Engine (Ctrl+Alt+L, then Execute) as soon as the offline
  gate is green (OfflineReplayVerified). It:
    1. Attaches to the running wotblitz.exe (process PID is logged only).
    2. Waits (polling) for the rolling driver's address file
       (%TEMP%\od-survivors.txt by default) to appear.
    3. Adds each survivor absolute address into CE's address list with a
       friendly description, so "Find out what writes this address" is one
       right-click away for the operator.

  Why: OD-RECOVERY-016 lost the interactive root window because the debugger
  was not pre-armed before the 120s research lease flipped EvidenceStale.
  This script replaces the version-dependent `cheatengine-x86_64.exe -p <pid>`
  command-line attach with CE's native Lua attach.

  Privacy: addresses are loaded into CE's in-memory address list only; nothing
  is written to disk by this script.

  Prerequisites:
    - WoT Blitz running with a positively verified offline replay
      (Host reports OfflineReplayVerified).
    - The rolling driver has been started with -AddressFile (or the address
      file path below matches what the driver will write).
--]============================================================================]

local LOG_ENABLED = true
local DEFAULT_ADDRESS_FILE = (os.getenv("TEMP") or "C:\\Windows\\Temp") .. "\\od-survivors.txt"
local ADDRESS_FILE = DEFAULT_ADDRESS_FILE
local MAX_POLL_ROUNDS = 30   -- ~60s at 2s interval, inside the 120s lease
local POLL_INTERVAL_SECONDS = 2

local function log(level, msg)
  if LOG_ENABLED then
    print(string.format("[%s] %s: %s", os.date("%H:%M:%S"), level, msg))
  end
end

local function log_info(msg)  log("INFO", msg) end
local function log_warn(msg)  log("WARN", msg) end
local function log_error(msg) log("ERROR", msg) end

-- ── Process Attachment ─────────────────────────────────────────────────────

local function attachToGame()
  local pid = getProcessIDFromProcessName("wotblitz.exe")
  if not pid or pid <= 0 then
    log_error("wotblitz.exe not found. Is the game running?")
    return false
  end

  log_info("Found wotblitz.exe PID: " .. tostring(pid))

  local success = openProcess("wotblitz.exe")
  if not success then
    log_error("Failed to open wotblitz.exe (run CE as Administrator if needed).")
    return false
  end

  log_info("Attached (PID: " .. tostring(getOpenedProcessID()) .. ")")
  return true
end

-- ── Survivor Address Loading ───────────────────────────────────────────────

local function addressFileExists(path)
  local f = io.open(path, "r")
  if f then f:close() return true end
  return false
end

local function loadSurvivors(path)
  local f = io.open(path, "r")
  if not f then return nil end

  local lines = {}
  for line in f:lines() do
    line = line:gsub("^%s+", ""):gsub("%s+$", "")
    if line ~= "" then
      lines[#lines + 1] = line
    end
  end
  f:close()

  if #lines == 0 then return nil end
  return lines
end

local function addToAddressList(addresses)
  local al = getAddressList()
  if not al then
    log_error("getAddressList() returned nil; cannot stage addresses.")
    return false
  end

  local added = 0
  for _, addr in ipairs(addresses) do
    -- Normalize to a plain 0x hex string CE understands.
    local hex = addr:gsub("^0[xX]", "")
    if hex:match("^[0-9a-fA-F]+$") then
      local mr = al.createMemoryRecord()
      mr.Description = "od-survivor-" .. tostring(added + 1)
      mr.Address = "0x" .. hex
      added = added + 1
    else
      log_warn("skipped non-hex survivor line: " .. addr)
    end
  end

  log_info("staged " .. tostring(added) .. " survivor addresses in CE address list")
  return added > 0
end

-- ── Entry Point ────────────────────────────────────────────────────────────

local function main()
  log_info("========================================")
  log_info("WotB Treader — Pre-Arm Attach (OD-017)")
  log_info("Address file: " .. ADDRESS_FILE)
  log_info("========================================")

  if not attachToGame() then
    log_error("Cannot attach to game process. Exiting.")
    return
  end

  log_info("Waiting for survivor address file (max " .. tostring(MAX_POLL_ROUNDS * POLL_INTERVAL_SECONDS) .. "s)...")
  local found = false
  for i = 1, MAX_POLL_ROUNDS do
    if addressFileExists(ADDRESS_FILE) then
      found = true
      break
    end
    sleep(POLL_INTERVAL_SECONDS * 1000)
  end

  if not found then
    log_warn("Address file did not appear within the poll window.")
    log_warn("Start the rolling driver with -AddressFile and run this script again,")
    log_warn("or add survivor addresses to CE manually.")
    return
  end

  local addresses = loadSurvivors(ADDRESS_FILE)
  if not addresses then
    log_warn("Address file is empty; nothing staged.")
    return
  end

  if addToAddressList(addresses) then
    log_info("READY: right-click any staged address -> 'Find out what writes this address'.")
    log_info("Note: staged lines are whatever the driver wrote; sanity-check the count against")
    log_info("the rolling driver's reported retained count before trusting them as survivors.")
  else
    log_error("No addresses were staged.")
  end
end

local ok, err = pcall(main)
if not ok then
  log_error("Script failed: " .. tostring(err))
end
