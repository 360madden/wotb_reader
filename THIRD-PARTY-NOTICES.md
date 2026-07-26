# Third-party notices

WotB Treader is an independent project and is not affiliated with, endorsed
by, or sponsored by Wargaming Group Limited.

World of Tanks Blitz, its names, resources, replay contents, and related marks
belong to their respective owners. The project reads a user's local files and
does not redistribute installed game resources.

## Reference implementations

- `wotbreplay-parser` by eigenein is MIT licensed. It is used as an attributed
  format reference and test oracle:
  <https://github.com/eigenein/wotbreplay-parser>
- WotbTools by A158Coke is MIT licensed. Its replay-data documentation and
  parser behavior are used as attributed evidence:
  <https://github.com/A158Coke/WotbTools>

No substantial source from either project may be copied without retaining all
license notices required by its license.

## Runtime packages

Runtime and test package names, pinned versions, and transitive license data
are recorded by `Directory.Packages.props`, NuGet lock files, and release
dependency reports. Any external executable must be registered in
`tools/external/tools.lock.json` before installation.

## Replay fixtures

Replay fixtures are not covered by the project MIT license. Each committed
fixture must have a separate provenance, permission, sanitization, and residual
risk notice. Private originals and game-derived resources must never be
committed.
