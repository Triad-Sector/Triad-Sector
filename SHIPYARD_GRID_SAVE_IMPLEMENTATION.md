## Shipyard grid save/load: implementation notes

This document summarizes how ship saving and loading works through shipyard consoles, and the key safety measures added to prevent runtime crashes.

### High-level goals

- Allow players to save a shuttle/ship from a shipyard console to a client-side YAML file.
- Allow loading a locally saved YAML back through a shipyard console.
- Sanitize on save/load so only safe data is preserved.
- After a successful server-side load, instruct the client to delete the local file (cleanup handshake).
- Avoid any destructive operations on live maps during save (no temp map shuffling or deletes).

### Key systems and message types

- Client
	- `Content.Client/Shuttles/Save/ShipFileManagementSystem` writes YAML under user data and lists available ships.
	- Shipyard UI: `Content.Client/_NF/Shipyard/UI/ShipyardConsoleMenu.*`
	- BUI: `Content.Client/_NF/Shipyard/BUI/ShipyardConsoleBoundUserInterface.*`
	- Receives `DeleteLocalShipFileMessage` and deletes the corresponding local YAML on success.

- Server
	- Save orchestration: `Content.Server/_NF/Shipyard/Systems/ShipyardGridSaveSystem`
	- Load + deed: `Content.Server/_NF/Shipyard/Systems/ShipyardSystem(.Consoles)`
	- Serialization: `Content.Server/Shuttles/ShipSerializationSystem`

- Shared messages
	- Load request: `ShipyardConsoleLoadMessage` (includes `SourceFilePath` to identify the client file to delete on success).
	- Post-load cleanup: `DeleteLocalShipFileMessage` (server -> client).
	- Save data to client: `SendShipSaveDataClientMessage` (server -> client YAML payload).

Paths/namespaces for cleanup:
- `Content.Shared/_NF/Shuttles/Save/DeleteLocalShipFileMessage.cs` (single authoritative definition)
- `Content.Shared/_NF/Shipyard/Events/ShipyardConsoleLoadMessage.cs` (has `SourceFilePath`)

### Save flow (non-destructive)

1. Player presses Save in shipyard console UI.
2. Server handles via `ShipyardGridSaveSystem.TrySaveGridAsShip(...)`.
3. The save is performed in-place using `ShipSerializationSystem`:
	 - Serialize the target grid and children without moving entities or maps.
	 - No background threads; everything runs on the main thread.
4. Server sends the resulting YAML to the client via `SendShipSaveDataClientMessage`.
5. Client writes YAML under user data (see path below) and updates the UI cache/list.

Why non-destructive? Earlier implementations temporarily moved the grid to a new map and deleted objects, which could invalidate PVS and other ECS systems mid-frame. The new path only serializes; it does not change the live world.

### Load flow (+ cleanup handshake)

1. Player selects a local YAML in the shipyard console UI and presses Load.
2. Client sends `ShipyardConsoleLoadMessage` containing the YAML and the local `SourceFilePath`.
3. Server sanitizes and spawns the ship; on success it sends `DeleteLocalShipFileMessage` back to that client with the `SourceFilePath`.
4. Client receives the delete message and removes the local YAML, then refreshes its index/UI.

This ensures client-side files are cleaned up after a successful import, keeping the list tidy and avoiding accidental double-imports.

### Sanitization

Sanitization happens both during save and load:
- `ShipyardGridSaveSystem.CleanGridForSaving(gridUid)` performs entity-level cleanups (e.g., removing vending machines) before serialization.
- `ShipSerializationSystem`’s serialization logic omits or normalizes problematic or identity-dependent data.

The cleaning pass is intentionally conservative: it should never remove critical physics or core components needed to re-spawn the ship. There are integration tests to guard this.

### File locations and naming

- Client save directory: `IResourceManager.UserData` under an `Exports`-style subfolder. The exact subpath is maintained in `ShipFileManagementSystem`.
- On successful server load, the server sends `DeleteLocalShipFileMessage` with the file path string the client originally provided via `ShipyardConsoleLoadMessage.SourceFilePath`.

### Deed handling

When loading succeeds, the shipyard system integrates with shuttle deeds as expected by existing content logic. Deeds are consumed/assigned according to the console’s rules; this part was left functionally equivalent to the earlier console workflow.

### Edge cases and limits

- Initialized maps/ships that cannot be safely serialized are skipped with a user-facing error in the console.
- Any component found to be unsafe to serialize is pruned. If future content introduces new problematic components, add them to the cleaning/serialization filters.
- No off-thread ECS access is used. Do not reintroduce background Task usage for serialization.

### Manual smoke test

Use a local client/server session:
1. Interact with a shipyard console and select a grid to save.
2. Press Save. Confirm a YAML appears under the client’s user data folder and the console lists it.
3. In the same session, choose the saved YAML and press Load.
4. Verify the spawned ship appears, deed handling runs, and there’s no map/entity deletion during save.
5. Verify the local YAML disappears shortly after load (post-load delete handshake).

### Tests and validation

- Integration tests: `Content.IntegrationTests/Tests/_NF/Shipyard/ShipyardGridSaveTest.cs`
	- Ambition ship cleaning removes vending machines.
	- Physics components are preserved during cleaning.

### Troubleshooting

- “Saving deleted my ship/map”: Ensure you are on the non-destructive path. There should be no map move or delete during save; if you see references to temporary maps in save code, that’s a regression.
- “YAML not appearing on client”: Check the client logs for `ShipFileManagementSystem` and verify `SendShipSaveDataClientMessage` is received.
- “File not deleting after load”: Confirm `ShipyardConsoleLoadMessage.SourceFilePath` is set by the client and that the server sends `DeleteLocalShipFileMessage` back to the same session.

### Recent performance & logging adjustments (Sept 2025)

To mitigate save-time lag spikes caused primarily by synchronous per-entity Info logging, the following adjustments were made:

1. Ship serialization logging changes (`ShipSerializationSystem`):
	- Per-entity skip messages for vending machines downgraded from Info → Debug.
	- Grid rotation normalization / restoration downgraded from Info → Debug.
	- Final success line consolidated into a single summary including entity count, tile count, and decal presence.
2. Enumeration: Continued usage of the grid's direct `ChildEnumerator` to avoid global queries; contained-entity traversal uses a queue with a HashSet to prevent duplicate serialization (kept for correctness, minimal overhead relative to log I/O savings).
3. Client file management system (`ShipFileManagementSystem`) retains reduced startup logging to avoid multi-instance spam; only essential Info or Warning level messages remain.

If you need verbose diagnostics while developing serialization:
```
set_log_level ship-serialization Debug
```
Revert when finished to avoid performance degradation on production servers.

Future optional improvements (not yet implemented):
- Config CVar (e.g. `shipyard.saveVerbose`) to gate debug serialization output without changing global sawmill level.
- Batched progress notifications to the client UI (estimated % based on enumerated children vs total) if ships become significantly larger.

These changes intentionally do not alter YAML schema or entity filtering semantics; only log levels and aggregation were adjusted.

## Refactored serializer (engine-based) – Sept 2025

### Overview
The default ship save path now uses the engine's recursive entity serializer (`MapLoaderSystem.SerializeEntitiesRecursive`) instead of the bespoke manual traversal. This removes redundant per-entity loops and eliminates the primary source of save-time lag (string formatting + logging at scale).

### CVars
| Name | Default | Purpose |
| ---- | ------- | ------- |
| `shipyard.use_legacy_serializer` | false | When true, restores the legacy manual serializer (full component capture). |
| `shipyard.save_verbose` | false | Enables detailed Debug logs for both legacy and refactored paths. |
| `shipyard.save_progress_interval` | 0 | When > 0 and verbose, logs a progress line every N entities during refactored extraction. |

### Behavioral differences
| Aspect | Refactored | Legacy |
| ------ | ---------- | ------ |
| Entity discovery | Engine recursive traversal | Manual grid child + container BFS |
| Vending machines | Skipped by veto hook pre-traversal | Skipped inside loops |
| Component payloads | Minimal (components map present but empty) | Selective component serialization (solutions, etc.) |
| Rotation handling | Grid temporarily zeroed, restored | Same |
| Log noise | Single summary + optional gated debug | Many per-entity debug lines (now gated) |
| Save performance | Improved (no per-entity YAML build) | Higher overhead |

### Minimal component strategy
Refactored saves intentionally omit serialized component state to optimize performance and file size. The `Components` field remains for schema stability (empty map / list). If you need historical component data (e.g. chemical solutions) switch to legacy with the feature flag. A future enhancement may add an allowlist for critical components without regressing performance.

### Rollback / diagnostics
1. Set `shipyard.use_legacy_serializer=true`.
2. Trigger a save; component payloads appear again.
3. After investigation, revert to `false`.

### Summary log format
`[Refactored] Ship serialized: <entities> entities, <tiles> tiles, decals=<true|false>, skippedVend=<count>`

When verbose & progress interval set: periodic `[Refactored] Serialized <n> entities so far...` lines.

### Legacy path status
Legacy remains for one transition cycle; after stability confirmation it may be marked `[Obsolete]` or removed. Use only when rich component state is essential.

### Future enhancements (planned / optional)
* Selective component extraction (solutions, stacks, battery charge) without full legacy overhead.
* Optional compression layer post-YAML if file size escalates.
* Client progress UI hook keyed off `shipyard.save_progress_interval`.

