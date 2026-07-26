# External tool policy

The alpha starts with built-in .NET, Win32/WinRT, installed Windows SDK tools,
and Windows Performance Recorder. Add an executable tool only after documenting
a concrete capability failure.

Before downloading a tool, register its exact version, canonical source URL,
SHA-256, SPDX license, purpose, and supported platform in `tools.lock.json`.
Binaries go under ignored `tools/external/installed/`; never download or update
them at application runtime.
