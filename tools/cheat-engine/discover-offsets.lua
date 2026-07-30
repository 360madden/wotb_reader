--[============================================================================[
  WotB Treader — Cheat Engine Offset Discovery Script
  ====================================================

  Load this script in Cheat Engine (Ctrl+Alt+L, then Execute).
  Auto-attaches to wotblitz.exe, reads memory around the known playerYaw
  offset, and reports all neighboring float/int32/double values.

  Saves results relative to CE's working directory. For project-relative
  output, start CE from the repo root or adjust OUTPUT_PATH below.

  Prerequisites:
    - WoT Blitz running with a replay actively playing
    - Cheat Engine 7.5+ attached to wotblitz.exe

  Known offset: playerYaw = 0x0317A810 (module-relative)
  Module base address is resolved at runtime (ASLR-aware).
--]============================================================================]

local KNOWN_OFFSETS = {
  playerYaw = 0x0317A810,  -- Confirmed by Ghidra static analysis (2026-07-30)
}

local WINDOW_SIZE = 1024  -- Bytes around each known offset to scan
local OUTPUT_PATH = "tools/cheat-engine/discovered-offsets.json"
local LOG_ENABLED = true

-- ── Logging ────────────────────────────────────────────────────────────────

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

  log_info("Found process PID: " .. tostring(pid))

  local success = openProcess("wotblitz.exe")
  if not success then
    log_error("Failed to open wotblitz.exe")
    log_error("Run Cheat Engine as Administrator and ensure no anti-cheat blocks access.")
    return false
  end

  log_info("Attached (PID: " .. tostring(getOpenedProcessID()) .. ")")
  return true
end

-- ── Module Resolution ──────────────────────────────────────────────────────

local function getModuleBase(moduleName)
  local modules = enumModules()
  for i = 1, #modules do
    local mod = modules[i]
    if mod.Name:lower():find(moduleName:lower(), 1, true) then
      log_info(string.format("Module %s: base=0x%X, size=0x%X",
        mod.Name, mod.Address, mod.Size))
      return mod.Address
    end
  end
  return nil
end

-- ── Plausibility Filters ───────────────────────────────────────────────────

local function isPlausibleFloat(value, fieldName)
  if fieldName:find("[Pp]osition") then
    return value >= -2000 and value <= 2000
  end
  if fieldName:find("[Yy]aw") or fieldName:find("[Pp]itch") then
    return value >= -3.15 and value <= 3.15
  end
  return value >= -10000 and value <= 10000
end

local function isPlausibleInt(value, fieldName)
  if fieldName:find("[Hh][Pp]") then
    return value >= 1 and value <= 4000
  end
  if fieldName:find("[Tt]ank") or fieldName:find("[Aa]live") then
    return value >= 0 and value <= 30
  end
  return value >= -1000000 and value <= 1000000
end

-- ── Neighborhood Scanner ────────────────────────────────────────────────────
-- Uses CE's built-in readFloat / readInteger / readDouble globals directly.

local function scanNeighborhood(baseAddress, refOffset, windowSize)
  local candidates = {}
  local absRef = baseAddress + refOffset
  local startAddr = absRef - windowSize
  local endAddr = absRef + windowSize

  log_info(string.format("Neighborhood scan at 0x%X (+-%d bytes)", absRef, windowSize))

  local refFloat = readFloat(absRef)
  local refInt = readInteger(absRef)
  log_info(string.format("Reference: float=%.6f, int32=%d", refFloat or 0, refInt or 0))

  -- Scan 4-byte aligned addresses (float and int32)
  for addr = startAddr, endAddr, 4 do
    local delta = addr - absRef

    local fVal = readFloat(addr)
    if fVal and fVal == fVal then
      candidates[#candidates + 1] = {
        absoluteAddress = string.format("0x%X", addr),
        relativeOffset = string.format("0x%X", addr - baseAddress),
        deltaFromRef = delta,
        valueType = "float",
        value = fVal,
      }
    end

    local iVal = readInteger(addr)
    if iVal and iVal == iVal then
      candidates[#candidates + 1] = {
        absoluteAddress = string.format("0x%X", addr),
        relativeOffset = string.format("0x%X", addr - baseAddress),
        deltaFromRef = delta,
        valueType = "int32",
        value = iVal,
      }
    end
  end

  -- Scan 8-byte aligned addresses (double)
  for addr = startAddr, endAddr, 8 do
    local delta = addr - absRef
    local dVal = readDouble(addr)
    if dVal and dVal == dVal then
      candidates[#candidates + 1] = {
        absoluteAddress = string.format("0x%X", addr),
        relativeOffset = string.format("0x%X", addr - baseAddress),
        deltaFromRef = delta,
        valueType = "double",
        value = dVal,
      }
    end
  end

  return candidates
end

-- ── JSON Output ────────────────────────────────────────────────────────────

local function tableToJson(tbl, indent)
  indent = indent or 0
  local prefix = string.rep("  ", indent)
  local prefixInner = string.rep("  ", indent + 1)

  if type(tbl) ~= "table" then
    if type(tbl) == "string" then
      return string.format('"%s"', tbl:gsub('"', '\\"'):gsub("\n", "\\n"))
    elseif type(tbl) == "number" then
      -- Distinguish integer from float for clean JSON
      if tbl == math.floor(tbl) then return string.format("%d", tbl)
      else return string.format("%.6f", tbl) end
    elseif type(tbl) == "boolean" then
      return tbl and "true" or "false"
    else
      return "null"
    end
  end

  -- Determine if this is a sequential array (ipairs-compatible)
  local isArray = true
  local count = 0
  for k, _ in pairs(tbl) do
    if type(k) ~= "number" or k < 1 or math.floor(k) ~= k then
      isArray = false
      break
    end
    if k > count then count = k end
  end
  -- Verify no gaps: sequential keys 1..count
  if isArray then
    for i = 1, count do
      if tbl[i] == nil then isArray = false; break end
    end
  end

  if isArray and count == 0 then
    return "[]"
  end

  local parts = {}
  if isArray then
    for i = 1, count do
      parts[#parts + 1] = prefixInner .. tableToJson(tbl[i], indent + 1)
    end
    return "[\n" .. table.concat(parts, ",\n") .. "\n" .. prefix .. "]"
  else
    for k, v in pairs(tbl) do
      local key = type(k) == "string" and string.format('"%s"', k) or string.format('"%s"', tostring(k))
      parts[#parts + 1] = prefixInner .. key .. ": " .. tableToJson(v, indent + 1)
    end
    return "{\n" .. table.concat(parts, ",\n") .. "\n" .. prefix .. "}"
  end
end

local function saveJson(filename, data)
  local file, err = io.open(filename, "w")
  if not file then
    log_error("Cannot write " .. filename .. ": " .. tostring(err))
    return false
  end

  local json = tableToJson(data)
  file:write(json)
  file:close()

  log_info("Saved " .. tostring(#json) .. " bytes to " .. filename)
  return true
end

-- ── Main Discovery Pipeline ────────────────────────────────────────────────

local function discoverNeighborhood(baseAddress)
  local allResults = {}
  local scanCount = 0

  for fieldName, refOffset in pairs(KNOWN_OFFSETS) do
    log_info("Scanning neighborhood of " .. fieldName .. " (0x" ..
      string.format("%X", refOffset) .. ")")

    local candidates = scanNeighborhood(baseAddress, refOffset, WINDOW_SIZE)

    local plausible = {}
    for _, c in ipairs(candidates) do
      if c.valueType == "float" and isPlausibleFloat(c.value, fieldName) then
        plausible[#plausible + 1] = c
      elseif c.valueType == "int32" and isPlausibleInt(c.value, fieldName) then
        plausible[#plausible + 1] = c
      elseif c.valueType == "double" and c.value >= -10000 and c.value <= 10000 then
        plausible[#plausible + 1] = c
      end
    end

    log_info(string.format("  %d total candidates, %d plausible",
      #candidates, #plausible))

    -- Sort by absolute delta from reference (closest first)
    table.sort(plausible, function(a, b)
      return math.abs(a.deltaFromRef) < math.abs(b.deltaFromRef)
    end)

    -- Take top 30 closest candidates
    local top = {}
    for i = 1, math.min(30, #plausible) do
      top[i] = plausible[i]
    end

    allResults[fieldName] = {
      referenceOffset = string.format("0x%X", refOffset),
      absoluteAddress = string.format("0x%X", baseAddress + refOffset),
      windowSize = WINDOW_SIZE,
      totalCandidates = #candidates,
      plausibleCandidates = #plausible,
      topCandidates = top,
    }

    scanCount = scanCount + 1
  end

  return allResults, scanCount
end

-- ── Entry Point ────────────────────────────────────────────────────────────

local function main()
  log_info("========================================")
  log_info("WotB Treader — Cheat Engine Offset Discovery")
  log_info("========================================")

  if not attachToGame() then
    log_error("Cannot attach to game process. Exiting.")
    return
  end

  local baseAddress = getModuleBase("wotblitz.exe")
  if not baseAddress then
    log_error("Cannot find wotblitz.exe module. Exiting.")
    return
  end

  log_info(string.format("Module base: 0x%X", baseAddress))
  log_info("Starting neighborhood scan...")

  local results, scanCount = discoverNeighborhood(baseAddress)

  local output = {
    schemaVersion = "1.0",
    tool = "cheat-engine-discover-offsets.lua",
    generatedAtUtc = os.date("!%Y-%m-%dT%H:%M:%SZ"),
    processId = getOpenedProcessID(),
    moduleBase = string.format("0x%X", baseAddress),
    moduleName = "wotblitz.exe",
    windowsScanned = scanCount,
    windowSize = WINDOW_SIZE,
    scanResults = results,
  }

  local saved = saveJson(OUTPUT_PATH, output)

  log_info("========================================")
  log_info("SCAN COMPLETE")
  log_info(string.format("  Process PID: %d", getOpenedProcessID()))
  log_info(string.format("  Module base: 0x%X", baseAddress))
  log_info(string.format("  Windows scanned: %d", scanCount))
  log_info(string.format("  Output: %s", OUTPUT_PATH))

  for fieldName, result in pairs(results) do
    log_info(string.format("  %s: %d plausible (of %d total)",
      fieldName, result.plausibleCandidates, result.totalCandidates))
    if result.topCandidates and #result.topCandidates > 0 then
      local top = result.topCandidates[1]
      log_info(string.format("    Top: delta=%+d, type=%s, value=%s",
        top.deltaFromRef, top.valueType, tostring(top.value)))
    end
  end

  if saved then
    log_info("Results saved to " .. OUTPUT_PATH)
  end
  log_info("========================================")
end

local ok, err = pcall(main)
if not ok then
  log_error("Script failed: " .. tostring(err))
end
