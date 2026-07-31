--[============================================================================[
  WotB Treader — Cheat Engine Multi-Scan Discovery
  =================================================

  Interactive script for discovering unknown memory offsets using Cheat Engine's
  built-in multi-scan engine. This is the most reliable method for finding
  dynamically-allocated game values.

  HOW TO USE (Interactive):
    1. Load this script in CE (Ctrl+Alt+L, then Execute)
    2. Run step 1 to begin: scanInteractive("playerPositionX", vtSingle, -500, 500)
    3. In the WoT Blitz replay, move the camera or let the tank move
    4. Run step 2: nextScan("changed") — filters to values that changed
    5. Repeat steps 3-4 until < 10 candidates remain
    6. Output results with: showCandidates() and saveDiscovered()

  HOW TO USE (Auto-Discover — unattended):
    1. Start WoT Blitz with a replay
    2. Load this script in CE (Ctrl+Alt+L, then Execute)
    3. Run: autoDiscover()
    4. Wait for completion (~30-60 seconds per field)
    5. Results auto-saved to discovered-offsets-multiscan.json

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

local function getBaseAddress()
  local modules = enumModules()
  for i = 1, #modules do
    if modules[i].Name:lower():find("wotblitz") then
      return modules[i].Address
    end
  end
  return 0
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

  scan.nextScan(scanType, nil, "", "", false, false, false, "")

  scan.waitTillDone()
  local count = scan.resultCount or 0
  log("INFO", string.format("→ %d addresses remaining (%.1fs total)",
    count, os.clock() - (scanStartTime or 0)))

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
  log("INFO", string.format("Filtering to EXACT value: %s ±%s...",
    tostring(value), tostring(tolerance)))

  scan.nextScan(soExactValue, nil,
    tostring(value - tolerance),
    tostring(value + tolerance),
    false, false, false, "")

  scan.waitTillDone()
  local count = scan.resultCount or 0
  log("INFO", string.format("→ %d addresses remaining", count))
end

function showCandidates()
  local scan = getCurrentMemScan()
  if not scan then
    log("ERROR", "No active scan. Run scanInteractive() first.")
    return
  end

  local count = math.min(scan.resultCount or 0, 30)
  log("INFO", "=== Top " .. tostring(count) .. " candidates ===")

  local baseAddr = getBaseAddress()

  for i = 1, count do
    local addr = scan.getResultAddress(i - 1)
    local value = scan.getResultValue(i - 1)
    local relOff = string.format("0x%X", addr - baseAddr)
    log("INFO", string.format("  [%2d] 0x%016X → %s  (rel: %s)",
      i, addr, tostring(value), relOff))
  end

  if (scan.resultCount or 0) > 30 then
    log("INFO", "  ... and " .. tostring(scan.resultCount - 30) .. " more")
  end
end

-- ── Shared JSON serializer (file-level, used by saveDiscovered + autoDiscover) ─

local function toJson(t, indent)
  indent = indent or 0
  local pfx = string.rep("  ", indent)
  local pfx2 = string.rep("  ", indent + 1)
  if type(t) ~= "table" then
    if type(t) == "string" then return '"' .. t:gsub('"', '\\"') .. '"'
    elseif type(t) == "number" then return tostring(t)
    elseif type(t) == "boolean" then return t and "true" or "false"
    else return "null" end
  end
  local isArray = true
  local maxIdx = 0
  for k in pairs(t) do
    if type(k) ~= "number" or k < 1 or math.floor(k) ~= k then
      isArray = false
      break
    end
    if k > maxIdx then maxIdx = k end
  end
  if isArray and maxIdx == 0 then return "[]" end
  local parts = {}
  if isArray then
    for i = 1, maxIdx do
      if t[i] ~= nil then
        parts[#parts + 1] = pfx2 .. toJson(t[i], indent + 1)
      end
    end
    return "[\n" .. table.concat(parts, ",\n") .. "\n" .. pfx .. "]"
  else
    for k, v in pairs(t) do
      local key = type(k) == "number" and "" or ('"' .. k .. '": ')
      parts[#parts + 1] = pfx2 .. key .. toJson(v, indent + 1)
    end
    return "{\n" .. table.concat(parts, ",\n") .. "\n" .. pfx .. "}"
  end
end

function saveDiscovered()
  local scan = getCurrentMemScan()
  if not scan then
    log("ERROR", "No active scan.")
    return
  end

  local baseAddr = getBaseAddress()

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
    scanMethod = "manual",
  }

  local file = io.open(OUTPUT_PATH, "w")
  if file then
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

-- ═══════════════════════════════════════════════════════════════════════════
-- AUTO-DISCOVERY ENGINE (Timer-based, no user interaction required)
-- ═══════════════════════════════════════════════════════════════════════════

-- Field definitions for auto-discovery.
-- Each field specifies the value type, scan mode, and refinement strategy.

local AUTO_FIELDS = {
  {
    fieldName = "playerHP",
    valueType = vtDword,
    description = "int32: current hit points",
    minValue = 1,
    maxValue = 4000,
    scanMode = "exact",
    filterMode = "unchanged",
    refinements = 5,      -- Number of timer-based refinement rounds
    refinementDelay = 2500, -- ms between rounds
  },
  {
    fieldName = "playerPositionX",
    valueType = vtSingle,
    description = "float: world X coordinate",
    minValue = -2000,
    maxValue = 2000,
    scanMode = "unknown",
    filterMode = "changed",
    refinements = 5,
    refinementDelay = 2000,
  },
  {
    fieldName = "playerPositionY",
    valueType = vtSingle,
    description = "float: world Y (height)",
    minValue = -500,
    maxValue = 1000,
    scanMode = "unknown",
    filterMode = "changed",
    refinements = 5,
    refinementDelay = 2000,
  },
  {
    fieldName = "playerPositionZ",
    valueType = vtSingle,
    description = "float: world Z coordinate",
    minValue = -2000,
    maxValue = 2000,
    scanMode = "unknown",
    filterMode = "changed",
    refinements = 5,
    refinementDelay = 2000,
  },
  {
    fieldName = "playerYaw",
    valueType = vtSingle,
    description = "float: turret/hull yaw angle (radians)",
    minValue = -3.15,
    maxValue = 3.15,
    scanMode = "unknown",
    filterMode = "changed",
    refinements = 5,
    refinementDelay = 2000,
  },
  {
    fieldName = "cameraPitch",
    valueType = vtSingle,
    description = "float: camera pitch angle (radians)",
    minValue = -3.15,
    maxValue = 3.15,
    scanMode = "unknown",
    filterMode = "changed",
    refinements = 5,
    refinementDelay = 2000,
  },
  {
    fieldName = "replayTime",
    valueType = vtDouble,
    description = "double: elapsed replay seconds",
    minValue = 0,
    maxValue = 900,
    scanMode = "unknown",
    filterMode = "increased",
    refinements = 5,
    refinementDelay = 2000,
  },
  {
    fieldName = "aliveTankCount",
    valueType = vtDword,
    description = "int32: number of tanks still alive",
    minValue = 0,
    maxValue = 30,
    scanMode = "unknown",
    filterMode = "decreased",
    refinements = 5,
    refinementDelay = 3000,
  },
}

-- fallback: map CE's vt enum to a readable name
local function valueTypeName(vt)
  if vt == vtSingle then return "Float"
  elseif vt == vtDword then return "Int32"
  elseif vt == vtDouble then return "Double"
  else return "Unknown" end
end

local function filterModeToScanType(mode)
  if mode == "changed" then return soChangedValue
  elseif mode == "unchanged" then return soUnchangedValue
  elseif mode == "increased" then return soIncreasedValue
  elseif mode == "decreased" then return soDecreasedValue
  else return soChangedValue
  end
end

-- Collect raw candidates for a single field using timer-based refinement.
-- Returns the candidate list or nil on failure.
local function autoScanField(field, baseAddr)
  local fn = field.fieldName
  local vt = field.valueType
  local refinements = field.refinements or 4
  local delay = field.refinementDelay or 2000

  log("INFO", "---------- " .. fn .. " ----------")
  log("INFO", "  Type: " .. valueTypeName(vt) ..
      ", range: [" .. tostring(field.minValue or "") .. ".." ..
      tostring(field.maxValue or "") .. "], filter: " .. field.filterMode)

  local scan = createMemScan()

  -- First pass: capture all values in range
  local firstType = soUnknownValue
  if field.scanMode == "exact" then
    firstType = soExactValue
  end

  scan.firstScan(
    firstType,
    vt,
    nil,
    tostring(field.minValue or ""),
    tostring(field.maxValue or ""),
    "", 0, 0x7FFFFFFFFFFF, "",
    fsmNotAligned, "1",
    false, false, false, false, false,
    false, ""
  )
  scan.waitTillDone()
  local prevCount = scan.resultCount or 0
  log("INFO", string.format("  Round 1 (first scan): %d addresses", prevCount))

  if prevCount == 0 then
    log("WARN", "  No results — field may not be in scanned memory range.")
    scan.destroy()
    return nil
  end

  if prevCount <= 10 then
    log("INFO", "  Already narrow enough, stopping refinement.")
  else
    -- Timer-based refinement rounds
    local filterType = filterModeToScanType(field.filterMode)
    for r = 2, refinements + 1 do
      if prevCount <= 10 then break end

      log("INFO", string.format("  Waiting %.1fs for replay to advance...", delay / 1000))
      sleep(delay)

      scan.nextScan(filterType, nil, "", "", false, false, false, "")
      scan.waitTillDone()
      local curCount = scan.resultCount or 0
      log("INFO", string.format("  Round %d (%s): %d → %d addresses",
        r, field.filterMode, prevCount, curCount))
      prevCount = curCount

      if prevCount <= 10 then
        log("INFO", "  Narrow enough!")
        break
      end
    end
  end

  -- Collect top candidates
  local count = math.min(prevCount, 10)
  local candidates = {}
  for i = 1, count do
    local addr = scan.getResultAddress(i - 1)
    local value = scan.getResultValue(i - 1)
    candidates[i] = {
      absoluteAddress = string.format("0x%X", addr),
      relativeOffset = string.format("0x%X", addr - baseAddr),
      relativeOffsetDecimal = addr - baseAddr,
      value = value,
    }
  end

  log("INFO", string.format("  Complete: %d candidate(s) collected", count))
  scan.destroy()
  return candidates
end

-- ── Main auto-discover entry point ─────────────────────────────────────

function autoDiscover()
  if not attach() then
    log("ERROR", "Cannot attach to game. Aborting.")
    return nil
  end

  local baseAddr = getBaseAddress()
  if baseAddr == 0 then
    log("ERROR", "Cannot resolve wotblitz.exe base address.")
    return nil
  end

  log("INFO", "========================================")
  log("INFO", "AUTO-DISCOVERY START")
  log("INFO", string.format("Module base: 0x%X", baseAddr))
  log("INFO", string.format("Process PID: %d", getOpenedProcessID()))
  log("INFO", string.format("Fields to scan: %d", #AUTO_FIELDS))
  log("INFO", "========================================")
  log("INFO", "")
  log("INFO", "Make sure a replay is actively playing and the game")
  log("INFO", "window is visible. The script will scan each field with")
  log("INFO", "timer-based refinement — no interaction needed.")
  log("INFO", "")

  local startTime = os.clock()
  local results = {}
  local fieldsScanned = 0
  local fieldsWithCandidates = 0

  for idx, field in ipairs(AUTO_FIELDS) do
    if field.fieldName ~= "playerYaw" then
      log("INFO", string.format("[%d/%d] Skipping %s — use scanInteractive() manually",
        idx, #AUTO_FIELDS, field.fieldName))
      goto continue
    end

    log("INFO", string.format("[%d/%d] Scanning %s...", idx, #AUTO_FIELDS, field.fieldName))
    local candidates = autoScanField(field, baseAddr)
    fieldsScanned = fieldsScanned + 1

    if candidates and #candidates > 0 then
      fieldsWithCandidates = fieldsWithCandidates + 1
      results[field.fieldName] = {
        fieldType = valueTypeName(field.valueType),
        description = field.description,
        totalCandidates = #candidates,
        candidates = candidates,
      }
    else
      results[field.fieldName] = {
        fieldType = valueTypeName(field.valueType),
        description = field.description,
        totalCandidates = 0,
        candidates = {},
      }
    end

    -- autoScanField already calls scan.destroy(); cleanup is a safety no-op here
    ::continue::
  end

  local elapsed = os.clock() - startTime

  -- Build output
  local output = {
    schemaVersion = "1.0",
    tool = "cheat-engine-multiscan.lua — autoDiscover()",
    generatedAtUtc = os.date("!%Y-%m-%dT%H:%M:%SZ"),
    processId = getOpenedProcessID(),
    moduleBase = string.format("0x%X", baseAddr),
    fieldsScanned = fieldsScanned,
    fieldsWithCandidates = fieldsWithCandidates,
    elapsedSeconds = math.floor(elapsed * 10) / 10,
    fieldResults = results,
  }

  -- Write combined output
  local file = io.open(OUTPUT_PATH, "w")
  if file then
    file:write(toJson(output))
    file:close()
    log("INFO", "Output saved to " .. OUTPUT_PATH)
  else
    log("ERROR", "Cannot write " .. OUTPUT_PATH)
  end

  log("INFO", "========================================")
  log("INFO", "AUTO-DISCOVERY COMPLETE")
  log("INFO", string.format("  Elapsed: %.1fs", elapsed))
  log("INFO", string.format("  Fields scanned: %d", fieldsScanned))
  log("INFO", string.format("  Fields with candidates: %d", fieldsWithCandidates))
  log("INFO", string.format("  Output: %s", OUTPUT_PATH))
  log("INFO", "========================================")

  return output
end

-- ── On-load banner ─────────────────────────────────────────────────────

log("INFO", "Multi-scan engine loaded.")
log("INFO", "Interactive: scanInteractive('playerPositionX', vtSingle, -500, 500)")
log("INFO", "          then nextScan('changed'), showCandidates(), saveDiscovered()")
log("INFO", "Auto:       autoDiscover() — unattended timer-based pipeline")
log("INFO", "Cleanup:    cleanup()")
