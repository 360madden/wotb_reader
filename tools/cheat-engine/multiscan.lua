--[============================================================================[
  WotB Treader — Cheat Engine Multi-Scan Discovery
  =================================================

  Interactive script for discovering unknown memory offsets using Cheat Engine's
  built-in multi-scan engine. This is the most reliable method for finding
  dynamically-allocated game values.

  HOW TO USE:
    1. Load this script in CE (Ctrl+Alt+L, then Execute)
    2. Run step 1 to begin: scanInteractive("playerPositionX", vtSingle, -500, 500)
    3. In the WoT Blitz replay, move the camera or let the tank move
    4. Run step 2: nextScan("changed") — filters to values that changed
    5. Repeat steps 3-4 until < 10 candidates remain
    6. Output results with: showCandidates() and saveDiscovered()

  VALUE TYPES:
    vtSingle  = 4-byte float (positions, angles)
    vtDword   = 4-byte integer (HP, counts)
    vtDouble  = 8-byte double (replay time)

  CE 7.5 API notes:
    firstScan(scanType, valueType, rounding, scanText1, scanText2, extraTypes,
              startAddress, stopAddress, protectionFlags, alignment, fastScan,
              writableOnly, executableOnly, copyOnWrite, isNotABoolean, isPercent,
              compareToSavedScan, savedScanName)
    nextScan(scanType, rounding, scanText1, scanText2, isNotABoolean, isPercent,
             compareToSavedScan, savedScanName)
--]============================================================================]

local LOG_ENABLED = true
local OUTPUT_PATH = "tools/cheat-engine/discovered-offsets-multiscan.json"
local scanField = nil
local scanStartTime = nil

local function log(level, msg)
  if LOG_ENABLED then
    print(string.format("[%s] %s: %s", os.date("%H:%M:%S"), level, msg))
  end
end

-- ── Attachment ─────────────────────────────────────────────────────────────

local function attach()
  local pid = getProcessIDFromProcessName("wotblitz.exe")
  if not pid or pid <= 0 then
    log("ERROR", "wotblitz.exe not found. Start the game first.")
    return false
  end
  openProcess("wotblitz.exe")
  log("INFO", "Attached (PID: " .. tostring(getOpenedProcessID()) .. ")")
  return true
end

-- ── Interactive Scan ────────────────────────────────────────────────────────

function scanInteractive(fieldName, valueType, minValue, maxValue)
  if not attach() then return end

  scanField = fieldName
  scanStartTime = os.clock()

  log("INFO", "========================================")
  log("INFO", "FIRST SCAN: " .. fieldName)
  log("INFO", "  Type: " .. tostring(valueType or "any"))
  log("INFO", "  Range: [" .. tostring(minValue or "") .. ".." .. tostring(maxValue or "") .. "]")
  log("INFO", "========================================")

  local scan = createMemScan()
  -- CE 7.5 firstScan signature:
  -- firstScan(scanType, valueType, rounding, scanText1, scanText2, extraTypes,
  --           startAddress, stopAddress, protectionFlags, alignment, fastScan,
  --           writableOnly, executableOnly, copyOnWrite, isNotABoolean, isPercent,
  --           compareToSavedScan, savedScanName)
  scan.firstScan(
    soUnknownValue,         -- Scan for unknown initial value
    valueType or vtAll,     -- Value type filter
    nil,                    -- Rounding (no truncation)
    tostring(minValue or ""),
    tostring(maxValue or ""),
    "",                     -- Extra types
    0,                      -- Start address
    0x7FFFFFFFFFFF,         -- End address
    "",                     -- Protection flags
    fsmNotAligned,          -- Alignment
    "1",                    -- Fast scan enabled
    false, false, false,    -- writableOnly, executableOnly, copyOnWrite
    false, false,           -- isNotABoolean, isPercent
    false, ""               -- compareToSavedScan, savedScanName
  )

  scan.waitTillDone()
  local count = scan.resultCount or 0
  log("INFO", string.format("Found %d addresses (%.1fs)",
    count, os.clock() - scanStartTime))
  log("INFO", "")
  log("INFO", "Now change the value in-game (move, take damage, etc.)")
  log("INFO", "Then run: nextScan('changed')")
  log("INFO", "  or:   nextScan('unchanged')")
  log("INFO", "  or:   nextScanValue(42.5, 0.5) for exact value")
end

function nextScan(mode)
  local scan = getCurrentMemScan()
  if not scan then
    log("ERROR", "No active scan. Run scanInteractive() first.")
    return
  end

  local scanType
  if mode == "changed" then
    scanType = soChangedValue
  elseif mode == "unchanged" then
    scanType = soUnchangedValue
  elseif mode == "increased" then
    scanType = soIncreasedValue
  elseif mode == "decreased" then
    scanType = soDecreasedValue
  else
    log("ERROR", "Unknown mode: " .. tostring(mode))
    log("ERROR", "Valid modes: changed, unchanged, increased, decreased")
    return
  end

  log("INFO", "Filtering to " .. mode:upper() .. " values...")

  -- CE 7.5 nextScan signature:
  -- nextScan(scanType, rounding, scanText1, scanText2, isNotABoolean, isPercent,
  --          compareToSavedScan, savedScanName)
  scan.nextScan(scanType, nil, "", "", false, false, false, "")

  scan.waitTillDone()
  local count = scan.resultCount or 0
  log("INFO", string.format("%s %d addresses remaining (%.1fs total)",
    tostring(string.char(0xE2, 0x86, 0x92)), count, os.clock() - (scanStartTime or 0)))

  if count <= 20 and count > 0 then
    log("INFO", "")
    log("INFO", "Few enough candidates to inspect manually!")
    log("INFO", "Run: showCandidates() to see them.")
  elseif count == 0 then
    log("ERROR", "No results left. The value wasn't in the scanned range.")
    log("ERROR", "Try again with different parameters or wider range.")
  end
end

function nextScanValue(value, tolerance)
  local scan = getCurrentMemScan()
  if not scan then
    log("ERROR", "No active scan. Run scanInteractive() first.")
    return
  end

  tolerance = tolerance or 0
  log("INFO", string.format("Filtering to EXACT value: %s +-%s...",
    tostring(value), tostring(tolerance)))

  scan.nextScan(soExactValue, nil,
    tostring(value - tolerance),
    tostring(value + tolerance),
    false, false, false, "")

  scan.waitTillDone()
  local count = scan.resultCount or 0
  log("INFO", string.format("%s %d addresses remaining",
    tostring(string.char(0xE2, 0x86, 0x92)), count))
end

function showCandidates()
  local scan = getCurrentMemScan()
  if not scan then
    log("ERROR", "No active scan. Run scanInteractive() first.")
    return
  end

  local count = math.min(scan.resultCount or 0, 30)
  log("INFO", "=== Top " .. tostring(count) .. " candidates ===")

  local baseAddr = 0
  local modules = enumModules()
  for i = 1, #modules do
    if modules[i].Name:lower():find("wotblitz") then
      baseAddr = modules[i].Address
      break
    end
  end

  for i = 1, count do
    local addr = scan.getResultAddress(i - 1)
    local value = scan.getResultValue(i - 1)
    local relOff = string.format("0x%X", addr - baseAddr)
    log("INFO", string.format("  [%2d] 0x%016X %s %s  (rel: %s)",
      i, addr, tostring(string.char(0xE2, 0x86, 0x92)), tostring(value), relOff))
  end

  if (scan.resultCount or 0) > 30 then
    log("INFO", "  ... and " .. tostring(scan.resultCount - 30) .. " more")
  end
end

function saveDiscovered()
  local scan = getCurrentMemScan()
  if not scan then
    log("ERROR", "No active scan.")
    return
  end

  local baseAddr = 0
  local modules = enumModules()
  for i = 1, #modules do
    if modules[i].Name:lower():find("wotblitz") then
      baseAddr = modules[i].Address
      break
    end
  end

  local candidates = {}
  local count = math.min(scan.resultCount or 0, 50)
  for i = 1, count do
    local addr = scan.getResultAddress(i - 1)
    candidates[i] = {
      absoluteAddress = string.format("0x%X", addr),
      relativeOffset = string.format("0x%X", addr - baseAddr),
      value = scan.getResultValue(i - 1),
    }
  end

  local output = {
    schemaVersion = "1.0",
    tool = "cheat-engine-multiscan.lua",
    generatedAtUtc = os.date("!%Y-%m-%dT%H:%M:%SZ"),
    fieldName = scanField,
    processId = getOpenedProcessID(),
    moduleBase = string.format("0x%X", baseAddr),
    totalCandidates = scan.resultCount or 0,
    candidates = candidates,
  }

  local file = io.open(OUTPUT_PATH, "w")
  if file then
    local function toJson(t, indent)
      indent = indent or 0
      local pfx = string.rep("  ", indent)
      local pfx2 = string.rep("  ", indent + 1)
      if type(t) ~= "table" then
        if type(t) == "string" then return '"' .. t:gsub('"', '\\"') .. '"'
        elseif type(t) == "number" then return tostring(t)
        else return "null" end
      end
      local parts = {}
      for k, v in pairs(t) do
        local key = type(k) == "number" and "" or ('"' .. k .. '": ')
        parts[#parts + 1] = pfx2 .. key .. toJson(v, indent + 1)
      end
      return "{\n" .. table.concat(parts, ",\n") .. "\n" .. pfx .. "}"
    end
    file:write(toJson(output))
    file:close()
    log("INFO", "Saved to " .. OUTPUT_PATH)
  else
    log("ERROR", "Cannot write " .. OUTPUT_PATH)
  end
end

function cleanup()
  local scan = getCurrentMemScan()
  if scan then scan.destroy() end
  scanField = nil
  scanStartTime = nil
  log("INFO", "Scan cleaned up.")
end

log("INFO", "Multi-scan engine loaded.")
log("INFO", "Usage: scanInteractive('playerPositionX', vtSingle, -500, 500)")
log("INFO", "Then:  nextScan('changed')")
log("INFO", "Then:  showCandidates()")
log("INFO", "Then:  saveDiscovered()")
log("INFO", "Clean: cleanup()")
