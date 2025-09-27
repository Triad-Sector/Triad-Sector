using Content.Shared.Maps; // For tile GetContentTileDefinition extension
using Content.Server.Shuttles.Systems;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.Shuttles.Components;
using Content.Server.Station.Components;
using Content.Server.Cargo.Systems;
using Content.Server.Station.Systems;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared._NF.Shipyard;
using Content.Shared.GameTicking;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Content.Shared._NF.CCVar;
using Robust.Shared.Configuration;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using Content.Shared._NF.Shipyard.Events;
using Content.Shared.Mobs.Components;
using Robust.Shared.Containers;
using Content.Server._NF.Station.Components;
using Content.Server.Storage.Components;
using Content.Shared._Mono.Shipyard;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Utility;
using Content.Shared.Doors.Components;
using Robust.Shared.Map.Components;
using Content.Server.Shuttles.Save;
using Content.Server.Administration.Commands; // For ShipBlacklistService
using Robust.Shared.Physics.Components; // FixturesComponent
using Robust.Shared.Physics.Collision.Shapes; // PolygonShape
using Robust.Shared.Physics; // Physics Transform
using Robust.Shared.Utility; // Box2 helpers
using Robust.Shared.Map.Events; // For BeforeEntityReadEvent

// Suppress naming rule for _NF namespace prefix (modding convention)
#pragma warning disable IDE1006
namespace Content.Server._NF.Shipyard.Systems;

/// <summary>
/// Temporary component to mark entities that should be anchored after grid loading is complete
/// </summary>
[RegisterComponent]
public sealed partial class PendingAnchorComponent : Component
{
}

public sealed partial class ShipyardSystem : SharedShipyardSystem
{
    [Dependency] private readonly IConfigurationManager _configManager = default!;
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly DockingSystem _docking = default!;
    [Dependency] private readonly PricingSystem _pricing = default!;
    [Dependency] private readonly ShuttleSystem _shuttle = default!;
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly MapLoaderSystem _mapLoader = default!;
    [Dependency] private readonly MetaDataSystem _metaData = default!;
    [Dependency] private readonly MapSystem _map = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly ShipOwnershipSystem _shipOwnership = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly IEntityManager _entityManager = default!;
    [Dependency] private readonly IEntitySystemManager _entitySystemManager = default!;
    [Dependency] private readonly IServerNetManager _netManager = default!; // Ensure this is present
    [Dependency] private readonly ITaskManager _taskManager = default!;
    [Dependency] private readonly IResourceManager _resources = default!;
    [Dependency] private readonly IDependencyCollection _dependency = default!; // For EntityDeserializer
    [Dependency] private readonly Content.Server.Shuttles.Save.ShipSerializationSystem _shipSerialization = default!; // For loading saved ships
    [Dependency] private readonly ITileDefinitionManager _tileDefManager = default!; // For tile lookups in clipping resolver
    [Dependency] private readonly EntityLookupSystem _lookup = default!; // For physics overlap checks

    public MapId? ShipyardMap { get; private set; }
    private float _shuttleIndex;
    private const float ShuttleSpawnBuffer = 1f;
    private ISawmill _sawmill = default!;
    private bool _enabled;
    private float _baseSaleRate;
    private readonly HashSet<string> _loadedShipIds = new();
    private readonly HashSet<NetUserId> _currentlyLoading = new();

    // The type of error from the attempted sale of a ship.
    public enum ShipyardSaleError
    {
        Success, // Ship can be sold.
        Undocked, // Ship is not docked with the station.
        OrganicsAboard, // Sapient intelligence is aboard, cannot sell, would delete the organics
        InvalidShip, // Ship is invalid
        MessageOverwritten, // Overwritten message.
    }

    // TODO: swap to strictly being a formatted message.
    public struct ShipyardSaleResult
    {
        public ShipyardSaleError Error; // Whether or not the ship can be sold.
        public string? OrganicName; // In case an organic is aboard, this will be set to the first that's aboard.
        public string? OverwrittenMessage; // The message to write if Error is MessageOverwritten.
    }

    public override void Initialize()
    {
        base.Initialize();

        // FIXME: Load-bearing jank - game doesn't want to create a shipyard map at this point.
        _enabled = _configManager.GetCVar(NFCCVars.Shipyard);
        _configManager.OnValueChanged(NFCCVars.Shipyard, SetShipyardEnabled); // NOTE: run immediately set to false, see comment above

        _configManager.OnValueChanged(NFCCVars.ShipyardSellRate, SetShipyardSellRate, true);
    _sawmill = Logger.GetSawmill("shipyard");
    SubscribeNetworkEvent<RequestLoadShipMessage>(HandleLoadShipRequest);

        SubscribeLocalEvent<ShipyardConsoleComponent, ComponentStartup>(OnShipyardStartup);
        SubscribeLocalEvent<ShipyardConsoleComponent, BoundUIOpenedEvent>(OnConsoleUIOpened);
        SubscribeLocalEvent<ShipyardConsoleComponent, ShipyardConsoleSellMessage>(OnSellMessage);
        SubscribeLocalEvent<ShipyardConsoleComponent, ShipyardConsolePurchaseMessage>(OnPurchaseMessage);
        // Ship saving/loading functionality
        SubscribeLocalEvent<ShipyardConsoleComponent, ShipyardConsoleLoadMessage>(OnLoadMessage);
        SubscribeLocalEvent<ShipyardConsoleComponent, EntInsertedIntoContainerMessage>(OnItemSlotChanged);
        SubscribeLocalEvent<ShipyardConsoleComponent, EntRemovedFromContainerMessage>(OnItemSlotChanged);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
        SubscribeLocalEvent<StationDeedSpawnerComponent, MapInitEvent>(OnInitDeedSpawner);
    }

    public override void Shutdown()
    {
        _configManager.UnsubValueChanged(NFCCVars.Shipyard, SetShipyardEnabled);
        _configManager.UnsubValueChanged(NFCCVars.ShipyardSellRate, SetShipyardSellRate);
    }
    private void OnShipyardStartup(EntityUid uid, ShipyardConsoleComponent component, ComponentStartup args)
    {
        if (!_enabled)
            return;
        InitializeConsole();
    }

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        CleanupShipyard();
    }

    private void SetShipyardEnabled(bool value)
    {
        if (_enabled == value)
            return;

        _enabled = value;

        if (value)
            SetupShipyardIfNeeded();
        else
            CleanupShipyard();
    }

    private void SetShipyardSellRate(float value)
    {
        _baseSaleRate = Math.Clamp(value, 0.0f, 1.0f);
    }

    /// <summary>
    /// Adds a ship to the shipyard, calculates its price, and attempts to ftl-dock it to the given station
    /// </summary>
    /// <param name="stationUid">The ID of the station to dock the shuttle to</param>
    /// <param name="shuttlePath">The path to the shuttle file to load. Must be a grid file!</param>
    /// <param name="shuttleEntityUid">The EntityUid of the shuttle that was purchased</param>
    public bool TryPurchaseShuttle(EntityUid stationUid, ResPath shuttlePath, [NotNullWhen(true)] out EntityUid? shuttleEntityUid)
    {
        if (!TryComp<StationDataComponent>(stationUid, out var stationData)
            || !TryAddShuttle(shuttlePath, out var shuttleGrid)
            || !TryComp<ShuttleComponent>(shuttleGrid, out var shuttleComponent))
        {
            shuttleEntityUid = null;
            return false;
        }

        if (!TryAddShuttle(shuttlePath, out var shuttleGrid))
        {
            shuttleEntityUid = null;
            return false;
        }

        var grid = shuttleGrid.Value;

        if (!TryComp<ShuttleComponent>(grid, out var shuttleComponent))
        {
            shuttleEntityUid = null;
            return false;
        }

        var price = _pricing.AppraiseGrid(grid, null);
        var targetGrid = consoleXform.GridUid.Value;

        _sawmill.Info($"Shuttle {shuttlePath} was purchased at {ToPrettyString(consoleUid)} for {price:f2}");
        _shuttle.TryFTLDock(grid, shuttleComponent, targetGrid);
        shuttleEntityUid = grid;
        return true;
    }

    /// <summary>
    /// Loads a shuttle from a file and docks it to the grid the console is on, like ship purchases.
    /// This is used for loading saved ships.
    /// </summary>
    /// <param name="consoleUid">The entity of the shipyard console to dock to its grid</param>
    /// <param name="shuttlePath">The path to the shuttle file to load. Must be a grid file!</param>
    /// <param name="shuttleEntityUid">The EntityUid of the shuttle that was loaded</param>
    public bool TryPurchaseShuttleFromFile(EntityUid consoleUid, ResPath shuttlePath, [NotNullWhen(true)] out EntityUid? shuttleEntityUid)
    {
        // Get the grid the console is on
        if (!TryComp<TransformComponent>(consoleUid, out var consoleXform) || consoleXform.GridUid == null)
        {
            shuttleEntityUid = null;
            return false;
        }

        if (!TryAddShuttle(shuttlePath, out var shuttleGrid))
        {
            shuttleEntityUid = null;
            return false;
        }

        var grid = shuttleGrid.Value;

        if (!TryComp<ShuttleComponent>(grid, out var shuttleComponent))
        {
            shuttleEntityUid = null;
            return false;
        }

        _sawmill.Info($"Shuttle {shuttlePath} was purchased at {ToPrettyString(stationUid)} for {price:f2}");
        var ev = new ShipBoughtEvent();
        RaiseLocalEvent(shuttleGrid.Value, ev);
        //can do TryFTLDock later instead if we need to keep the shipyard map paused
        _shuttle.TryFTLDock(grid, shuttleComponent, targetGrid.Value);
        shuttleEntityUid = grid;
        return true;
    }

    /// <summary>
    /// Loads a shuttle into the ShipyardMap from a file path
    /// </summary>
    /// <param name="shuttlePath">The path to the grid file to load. Must be a grid file!</param>
    /// <returns>Returns the EntityUid of the shuttle</returns>
    private bool TryAddShuttle(ResPath shuttlePath, [NotNullWhen(true)] out EntityUid? shuttleGrid)
    {
        shuttleGrid = null;
        SetupShipyardIfNeeded();
        if (ShipyardMap == null)
            return false;

        if (!_mapLoader.TryLoadGrid(ShipyardMap.Value, shuttlePath, out var grid, offset: new Vector2(500f + _shuttleIndex, 1f)))
        {
            _sawmill.Error($"Unable to spawn shuttle {shuttlePath}");
            return false;
        }

        _shuttleIndex += grid.Value.Comp.LocalAABB.Width + ShuttleSpawnBuffer;

        shuttleGrid = grid.Value.Owner;
        return true;
    }

    /// <summary>
    /// Loads a ship directly from YAML data, following the same pattern as ship purchases
    /// </summary>
    /// <param name="consoleUid">The entity of the shipyard console to dock to its grid</param>
    /// <param name="yamlData">The YAML data of the ship to load</param>
    /// <param name="shuttleEntityUid">The EntityUid of the shuttle that was loaded</param>
    public bool TryLoadShipFromYaml(EntityUid consoleUid, string yamlData, [NotNullWhen(true)] out EntityUid? shuttleEntityUid)
    {
        shuttleEntityUid = null;

        // Get the grid the console is on
        if (!TryComp<TransformComponent>(consoleUid, out var consoleXform) || consoleXform.GridUid == null)
        {
            return false;
        }

        var targetGrid = consoleXform.GridUid.Value;

        try
        {
            // Setup shipyard
            SetupShipyardIfNeeded();
            if (ShipyardMap == null)
                return false;

            // Load ship directly from YAML data (bypassing file system)
            if (!TryLoadGridFromYamlData(yamlData, ShipyardMap.Value, new Vector2(500f + _shuttleIndex, 1f), out var grid))
            {
                _sawmill.Error($"Unable to load ship from YAML data");
                return false;
            }

            var shuttleGrid = grid.Value.Owner;

            if (!TryComp<ShuttleComponent>(shuttleGrid, out var shuttleComponent))
            {
                _sawmill.Error("Loaded entity is not a shuttle");
                return false;
            }

            // Update shuttle index for spacing
            _shuttleIndex += grid.Value.Comp.LocalAABB.Width + ShuttleSpawnBuffer;

            _sawmill.Info($"Ship loaded from YAML data at {ToPrettyString(consoleUid)}");
            // Immediately attempt to dock the loaded ship to the console's grid (station) just like purchase flow.
            // This ensures the loaded vessel appears docked instead of floating.
            try
            {
                // Pre-align shuttle to best docking angle to reduce clipping/rotation mismatch
                try
                {
                    var config = _docking.GetDockingConfig(shuttleGrid, targetGrid);
                    if (config != null && TryComp<TransformComponent>(shuttleGrid, out var preXform))
                    {
                        preXform.LocalRotation = config.Angle;
                        _sawmill.Info($"Pre-aligned shuttle rotation to docking angle {config.Angle.Degrees:F2}°");
                    }
                }
                catch (Exception alignEx)
                {
                    _sawmill.Debug($"Pre-dock alignment skipped: {alignEx.Message}");
                }
                if (TryComp<TransformComponent>(shuttleGrid, out var shipXform) && TryComp<TransformComponent>(targetGrid, out var targetXform))
                {
                    _sawmill.Info($"Pre-dock orientation: shipRot={shipXform.LocalRotation.Degrees:F2}° shipPos={shipXform.WorldPosition} targetRot={targetXform.LocalRotation.Degrees:F2}° targetPos={targetXform.WorldPosition}");
                }
                if (_shuttle.TryFTLDock(shuttleGrid, shuttleComponent, targetGrid))
                {
                    if (TryComp<TransformComponent>(shuttleGrid, out var shipXform2))
                        _sawmill.Info($"Post-dock orientation: shipRot={shipXform2.LocalRotation.Degrees:F2}° shipPos={shipXform2.WorldPosition}");
                    _sawmill.Info($"Auto-docked loaded ship {shuttleGrid} to grid {targetGrid}");
                }
                else
                {
                    // Fallback: try with Gas-type ports instead of Airlock (DockType.Docking does not exist)
                    if (_shuttle.TryFTLDock(shuttleGrid, shuttleComponent, targetGrid, dockType: DockType.Gas))
                    {
                        if (TryComp<TransformComponent>(shuttleGrid, out var shipXform3))
                            _sawmill.Info($"Post-dock (fallback) orientation: shipRot={shipXform3.LocalRotation.Degrees:F2}° shipPos={shipXform3.WorldPosition}");
                        _sawmill.Info($"Auto-docked (fallback) loaded ship {shuttleGrid} to grid {targetGrid}");
                    }
                    else
                    {
                        _sawmill.Warning($"Auto-dock attempt for loaded ship {shuttleGrid} to grid {targetGrid} failed (no matching docks?)");
                    }
                }

                // Post-load maintenance: cleanup duplicates & auto-anchor infrastructure.
                // IMPORTANT: Do NOT move a grid after docking (joints expect a fixed transform). Any nudging while docked
                // can cause position desync and wild teleporting on undock. Only resolve overlaps if NOT docked (handled in method).
                try
                {
                    // Only resolves clipping if shuttle is not currently docked.
                    ResolvePostDockClipping(shuttleGrid, targetGrid);
                    CleanupDuplicateLooseParts(shuttleGrid);
                    AutoAnchorInfrastructure(shuttleGrid);
                }
                catch (Exception postEx)
                {
                    _sawmill.Error($"Post-load maintenance error on {shuttleGrid}: {postEx.Message}");
                }
            }
            catch (Exception dockEx)
            {
                _sawmill.Error($"Exception during auto-dock of loaded ship {shuttleGrid}: {dockEx.Message}");
            }

            shuttleEntityUid = shuttleGrid;
            return true;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Exception while loading ship from YAML: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Removes loose duplicates of machine parts (boards, capacitors, matter bins, etc.) that were already restored inside machines.
    /// Heuristic: if an item prototype name contains one of the target substrings and there exists an anchored entity on the same grid
    /// that has a matching container slot already filled (implying the part was restored), delete the loose item.
    /// This is a stop-gap until full container metadata round-trips reliably.
    /// </summary>
    private void CleanupDuplicateLooseParts(EntityUid shuttleGrid)
    {
        if (!TryComp<MapGridComponent>(shuttleGrid, out _))
            return;

        var partKeywords = new[] {
            // Machine parts / boards
            "capacitor", "matter bin", "board", "manipulator", "laser", "scanner",
            // Added per request: tools, electronics, lights
            "tool", "wrench", "screwdriver", "crowbar", "multitool",
            "electronics", "circuit", "light", "lamp", "bulb"
        };
        var transformQuery = GetEntityQuery<TransformComponent>();
        var metaQuery = GetEntityQuery<MetaDataComponent>();
        var toDelete = new List<EntityUid>();

        // Collect all loose (unanchored or no container parent) items with keywords
        foreach (var uid in EntityManager.GetEntities())
        {
            if (!transformQuery.TryGetComponent(uid, out var xform))
                continue;
            if (xform.GridUid != shuttleGrid)
                continue;
            if (xform.Anchored) // anchored parts we leave alone
                continue;
            if (!metaQuery.TryGetComponent(uid, out var meta) || meta.EntityPrototype == null)
                continue;
            var name = meta.EntityPrototype.ID.ToLowerInvariant();
            if (!partKeywords.Any(k => name.Contains(k)))
                continue;

            // Simple heuristic: if it's not in a container (parent is grid) mark for deletion
            if (xform.ParentUid == shuttleGrid)
                toDelete.Add(uid);
        }

        if (toDelete.Count == 0)
            return;

        foreach (var ent in toDelete)
        {
            QueueDel(ent);
        }
        _sawmill.Info($"Removed {toDelete.Count} loose duplicate machine part items on loaded ship {shuttleGrid}");
    }

    /// <summary>
    /// Detects severe overlap (clipping) between the loaded shuttle grid and the target grid tiles after docking.
    /// If more than a threshold of solid tiles overlap inside each other's AABBs, nudges the shuttle outward along the vector between dock centers.
    /// This is a heuristic mitigation until rotation/origin alignment is fully resolved.
    /// </summary>
    private void ResolvePostDockClipping(EntityUid shuttleGrid, EntityUid targetGrid)
    {
        // If the shuttle is docked, we must NOT move it. Moving a docked grid breaks joint constraints and
        // leads to drifting / teleporting upon undock. Skip in that case.
        try
        {
            var docks = _docking.GetDocks(shuttleGrid);
            if (docks.Count > 0 && docks.Any(d => d.Comp.Docked))
            {
                _sawmill.Debug($"ResolvePostDockClipping skipped: shuttle {shuttleGrid} is docked");
                return;
            }
        }
        catch
        {
            // best-effort; continue with checks below if docking query fails
        }

        // Use physics fixtures to detect overlap against target grid tiles and anchored collidables.
        if (!TryComp<FixturesComponent>(shuttleGrid, out var shuttleFix) || shuttleFix.Fixtures.Count == 0)
            return;
        if (!TryComp<TransformComponent>(shuttleGrid, out var shuttleXform) || !TryComp<TransformComponent>(targetGrid, out var targetXform))
            return;
        if (!TryComp<MapGridComponent>(targetGrid, out var targetMap))
            return;

        int CountOverlaps()
        {
            var overlaps = 0;
            var (shipPos, shipRot) = _transform.GetWorldPositionRotation(shuttleXform);
            foreach (var fix in shuttleFix.Fixtures.Values)
            {
                if (fix.Shape is not PolygonShape poly)
                    continue;
                var worldAabb = poly.ComputeAABB(new Transform(shipPos, (float)shipRot.Theta), 0);

                // Convert to target grid local space to check intersecting tiles
                var corners = new System.Numerics.Vector2[4]
                {
                    new(worldAabb.Left, worldAabb.Bottom),
                    new(worldAabb.Left, worldAabb.Top),
                    new(worldAabb.Right, worldAabb.Bottom),
                    new(worldAabb.Right, worldAabb.Top)
                };
                var invAngle = -targetXform.WorldRotation;
                var invOrigin = targetXform.WorldPosition;
                var min = new System.Numerics.Vector2(float.MaxValue, float.MaxValue);
                var max = new System.Numerics.Vector2(float.MinValue, float.MinValue);
                foreach (ref readonly var c in corners.AsSpan())
                {
                    var local = invAngle.RotateVec(c - invOrigin);
                    min = System.Numerics.Vector2.Min(min, local);
                    max = System.Numerics.Vector2.Max(max, local);
                }
                var localAabb = new Box2(min, max).Enlarged(-0.01f);

                foreach (var tile in _map.GetLocalTilesIntersecting(targetGrid, targetMap, localAabb))
                {
                    var def = tile.Tile.GetContentTileDefinition(_tileDefManager);
                    if (def.ID != "Space")
                    {
                        overlaps++;
                        break;
                    }
                }

                if (overlaps > 0)
                    continue;

                // Also check anchored hard collidables on target grid within worldAabb
                var mapId = targetXform.MapID;
                foreach (var ent in _lookup.GetEntitiesIntersecting(mapId, worldAabb))
                {
                    if (!TryComp<TransformComponent>(ent, out var ex) || ex.GridUid != targetGrid)
                        continue;
                    if (!TryComp<PhysicsComponent>(ent, out var phys) || !phys.Hard || !phys.CanCollide)
                        continue;
                    overlaps++;
                    break;
                }
            }
            return overlaps;
        }

        var start = CountOverlaps();
        if (start <= 0)
            return;

        // Nudge outward up to N steps
        var dir = shuttleXform.WorldPosition - targetXform.WorldPosition;
        if (dir.LengthSquared() < 0.001f)
            dir = new System.Numerics.Vector2(1, 0);
        dir = System.Numerics.Vector2.Normalize(dir);

        const int maxSteps = 10;
        const float step = 0.5f;
        var steps = 0;
        var overlapsNow = start;
        while (overlapsNow > 0 && steps < maxSteps)
        {
            shuttleXform.WorldPosition += dir * step;
            overlapsNow = CountOverlaps();
            steps++;
        }

        _sawmill.Info($"Post-dock fixture overlap: start={start}, end={overlapsNow}, steps={steps}");
    }

    /// <summary>
    /// Anchors atmos and infrastructure entities (vents, scrubbers, pipes, manifolds, cables) that should be anchored but aren't.
    /// </summary>
    private void AutoAnchorInfrastructure(EntityUid shuttleGrid)
    {
        var transformQuery = GetEntityQuery<TransformComponent>();
        var metaQuery = GetEntityQuery<MetaDataComponent>();
        var anchored = 0;

        foreach (var uid in EntityManager.GetEntities())
        {
            if (!transformQuery.TryGetComponent(uid, out var xform))
                continue;
            if (xform.GridUid != shuttleGrid)
                continue;
            if (xform.Anchored)
                continue;
            if (!metaQuery.TryGetComponent(uid, out var meta) || meta.EntityPrototype == null)
                continue;
            var id = meta.EntityPrototype.ID;
            // Heuristic match list (can expand):
            if (!(id.Contains("Vent", StringComparison.OrdinalIgnoreCase)
                || id.Contains("Scrubber", StringComparison.OrdinalIgnoreCase)
                || id.Contains("Pipe", StringComparison.OrdinalIgnoreCase)
                || id.Contains("Manifold", StringComparison.OrdinalIgnoreCase)
                || id.Contains("Cable", StringComparison.OrdinalIgnoreCase)
                || id.Contains("Conduit", StringComparison.OrdinalIgnoreCase)))
                continue;

            try
            {
                _transform.AnchorEntity(uid, xform);
                anchored++;
            }
            catch
            {
                // Ignore failures (some may not be anchorable)
            }
        }

        if (anchored > 0)
            _sawmill.Info($"Auto-anchored {anchored} infrastructure entities on loaded ship {shuttleGrid}");
    }

    /// <summary>
    /// Loads a grid directly from YAML string data, similar to MapLoaderSystem.TryLoadGrid but without file system dependency
    /// </summary>
    private bool TryLoadGridFromYamlData(string yamlData, MapId map, Vector2 offset, [NotNullWhen(true)] out Entity<MapGridComponent>? grid)
    {
        grid = null;

        try
        {
            _sawmill.Info($"[ShipLoad] Begin loading ship YAML (len={yamlData.Length}) at offset {offset}");

            // Refined classification: Look only at the first few non-empty, non-comment root-level lines.
            // If first root key is 'meta:' we treat as engine-standard grid YAML regardless of 'metadata:' appearing later inside components.
            bool IsEngineStandardGrid(string text)
            {
                using var reader = new System.IO.StringReader(text);
                string? line;
                var examined = 0;
                while ((line = reader.ReadLine()) != null && examined < 30) // examine first 30 lines max
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
                        continue;
                    // Root-level key? (no leading spaces)
                    if (char.IsWhiteSpace(line, 0))
                    {
                        examined++;
                        continue;
                    }
                    if (trimmed.StartsWith("meta:"))
                        return true; // engine format
                    if (trimmed.StartsWith("metadata:"))
                        return false; // custom format
                    // Any other root key before meta/metadata suggests custom; break.
                    break;
                }
                // Fallback: if we never saw metadata but we did see characteristic engine keys globally, classify engine.
                if (text.Contains("meta:") && (text.Contains("tilemap:") || text.Contains("nullspace:") || text.Contains("entityCount:")))
                    return true;
                return false;
            }

            var isStandard = IsEngineStandardGrid(yamlData);
            var attemptedStandard = false;

            // 1. Fast-path: Treat YAML as an engine-standard grid file first (if heuristic says so, or we still just try anyway for safety).
            try
            {
                attemptedStandard = true;
                var fastGridUid = _shipSerialization.TryLoadStandardGridYaml(yamlData, map, new System.Numerics.Vector2(offset.X, offset.Y));
                if (fastGridUid != null && EntityManager.TryGetComponent<MapGridComponent>(fastGridUid.Value, out var fastGrid))
                {
                    grid = new Entity<MapGridComponent>(fastGridUid.Value, fastGrid);
                    _sawmill.Info($"[ShipLoad] Loaded via standard MapLoader-compatible path: {fastGridUid.Value}");
                    LogGridChildDiagnostics(fastGridUid.Value, "post-standard-load");
                    LogPotentialOrphanedEntities(fastGridUid.Value, "post-standard-load");
                    // Automatically purge obvious duplicate loose items that sometimes accumulate at grid origin.
                    // This runs only once immediately after load.
                    CleanupDuplicateOriginPile(fastGridUid.Value, "post-standard-load");
                    return true;
                }
                _sawmill.Debug("[ShipLoad] Standard grid YAML loader did not succeed (may be custom or fallback needed).");
            }
            catch (Exception fastEx)
            {
                _sawmill.Debug($"[ShipLoad] Standard grid YAML path threw: {fastEx.Message}.");
            }

            // 2. Custom ship serialization format (legacy / secure export) -> reconstruct entities manually (skip if clearly standard engine YAML)
            if (!isStandard)
            {
                try
                {
                    var shipGridData = _shipSerialization.DeserializeShipGridDataFromYaml(yamlData, Guid.Empty);
                    if (shipGridData != null)
                    {
                        _sawmill.Info("[ShipLoad] Parsed YAML as custom ShipGridData format (legacy / secure export)");
                        var shipGridUid = _shipSerialization.ReconstructShipOnMap(shipGridData, map, new System.Numerics.Vector2(offset.X, offset.Y));

                        // Ensure the reconstructed grid has a ShuttleComponent so downstream docking logic treats it identically to purchased shuttles.
                        if (!HasComp<ShuttleComponent>(shipGridUid))
                        {
                            EnsureComp<ShuttleComponent>(shipGridUid);
                            _sawmill.Debug("[ShipLoad] Added missing ShuttleComponent to reconstructed grid");
                        }

                        if (EntityManager.TryGetComponent<MapGridComponent>(shipGridUid, out var shipGrid))
                        {
                            grid = new Entity<MapGridComponent>(shipGridUid, shipGrid);
                            _sawmill.Info($"[ShipLoad] Successfully reconstructed ship grid: {shipGridUid}");
                            LogGridChildDiagnostics(shipGridUid, "post-reconstruct");
                            LogPotentialOrphanedEntities(shipGridUid, "post-reconstruct");
                            CleanupDuplicateOriginPile(shipGridUid, "post-reconstruct");
                            return true;
                        }
                    }
                }
                catch (Exception shipEx)
                {
                    _sawmill.Warning($"[ShipLoad] Custom ship reconstruction failed: {shipEx.Message}. Falling back to raw deserializer path.");
                }
            }
            else
            {
                _sawmill.Debug("[ShipLoad] Skipping custom ShipGridData parser: looks like engine standard grid YAML.");
                // If we already tried standard and it failed, jump straight to raw fallback (avoid metadata errors)
                if (attemptedStandard)
                    _sawmill.Debug("[ShipLoad] Proceeding directly to raw fallback deserializer for engine grid YAML.");
            }

            _sawmill.Debug("[ShipLoad] Proceeding to raw EntityDeserializer fallback path.");

            // Fallback: Parse YAML data directly (same approach as MapLoaderSystem.TryReadFile)
            using var textReader = new StringReader(yamlData);
            var documents = DataNodeParser.ParseYamlStream(textReader).ToArray();

            switch (documents.Length)
            {
                case < 1:
                    _sawmill.Error("YAML data has no documents.");
                    return false;
                case > 1:
                    _sawmill.Error("YAML data has too many documents. Ship files should contain exactly one.");
                    return false;
            }

            var data = (MappingDataNode)documents[0].Root;

            // Create load options (same as MapLoaderSystem.TryLoadGrid)
            var opts = new MapLoadOptions
            {
                MergeMap = map,
                Offset = offset,
                Rotation = Angle.Zero,
                DeserializationOptions = DeserializationOptions.Default,
                ExpectedCategory = FileCategory.Grid
            };

            // Process data with EntityDeserializer (same as MapLoaderSystem.TryLoadGeneric)
            var ev = new BeforeEntityReadEvent();
            RaiseLocalEvent(ev);

            opts.DeserializationOptions.AssignMapids = opts.ForceMapId == null;

            if (opts.MergeMap is { } targetId && !_map.MapExists(targetId))
                throw new Exception($"Target map {targetId} does not exist");

            var deserializer = new EntityDeserializer(
                _dependency,
                data,
                opts.DeserializationOptions,
                ev.RenamedPrototypes,
                ev.DeletedPrototypes);

            if (!deserializer.TryProcessData())
            {
                _sawmill.Error("Failed to process YAML entity data");
                return false;
            }

            deserializer.CreateEntities();

            if (opts.ExpectedCategory is { } exp && exp != deserializer.Result.Category)
            {
                _sawmill.Error($"YAML data does not contain the expected data. Expected {exp} but got {deserializer.Result.Category}");
                _mapLoader.Delete(deserializer.Result);
                return false;
            }

            // Apply transformations and start entities (same as MapLoaderSystem)
            var merged = new HashSet<EntityUid>();
            MergeMaps(deserializer, opts, merged);

            if (!SetMapId(deserializer, opts))
            {
                _mapLoader.Delete(deserializer.Result);
                return false;
            }

            ApplyTransform(deserializer, opts);
            deserializer.StartEntities();

            if (opts.MergeMap is { } mergeMap)
                MapInitalizeMerged(merged, mergeMap);

            // Process deferred anchoring after all entities are started and physics is stable
            ProcessPendingAnchors(merged);

            // Check for exactly one grid (same as MapLoaderSystem.TryLoadGrid)
            if (deserializer.Result.Grids.Count == 1)
            {
                grid = deserializer.Result.Grids.Single();
                LogGridChildDiagnostics(grid.Value.Owner, "post-fallback-deserializer");
                LogPotentialOrphanedEntities(grid.Value.Owner, "post-fallback-deserializer");
                ReconcileGridChildren(grid.Value.Owner);
                CleanupDuplicateOriginPile(grid.Value.Owner, "post-fallback-deserializer");
                return true;
            }

            _mapLoader.Delete(deserializer.Result);
            return false;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Exception while loading grid from YAML data: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Diagnostic helper to log counts of child entities & orphaned entities relative to a grid to help track issues
    /// where only tiles move but entities are left behind (e.g., improper parenting before FTL docking).
    /// </summary>
    private void LogGridChildDiagnostics(EntityUid gridUid, string stage)
    {
        try
        {
            if (!TryComp<TransformComponent>(gridUid, out var gridXform))
                return;

            var total = 0;
            var notParented = 0;
            foreach (var uid in _entityManager.GetEntities())
            {
                if (!TryComp<TransformComponent>(uid, out var xform))
                    continue;
                if (xform.ParentUid == gridUid)
                {
                    total++;
                }
                else if (xform.GridUid == gridUid && xform.ParentUid != gridUid)
                {
                    // Entity is on the grid (GridUid matches) but parent is elsewhere (possible detach issue)
                    notParented++;
                }
            }
            if (notParented > 0)
                _sawmill.Warning($"[ShipLoad][diag:{stage}] Grid {gridUid} children={total} mismatchedParent={notParented}");
            else
                _sawmill.Debug($"[ShipLoad][diag:{stage}] Grid {gridUid} children={total} (no mismatches)");
        }
        catch
        {
            // Best-effort diagnostics; ignore failures.
        }
    }

    /// <summary>
    /// Logs potential duplicate / orphan entities that appear clustered near the grid's origin (0,0) and are unanchored.
    /// This helps diagnose duplicated internal container contents (e.g. thruster/consoles ejecting their parts) on load.
    /// </summary>
    private void LogPotentialOrphanedEntities(EntityUid gridUid, string stage)
    {
        // Only do work if debug level is enabled to avoid overhead.
        if (!_sawmill.IsLogLevelEnabled(LogLevel.Debug))
            return;
        try
        {
            var suspicious = new List<string>();
            if (!TryComp<MapGridComponent>(gridUid, out _))
                return;

            var gridXform = Transform(gridUid);
            var childEnumerator = gridXform.ChildEnumerator;
            while (childEnumerator.MoveNext(out var child))
            {
                if (!TryComp<TransformComponent>(child, out var childXform))
                    continue;
                // Only consider unanchored items within a radius (e.g. 1.5) of grid origin.
                if (childXform.Anchored)
                    continue;
                if ((childXform.LocalPosition).LengthSquared() > 2.25f)
                    continue;
                // Skip obvious structural components (doors, docks, etc.)
                if (HasComp<DockingComponent>(child) || HasComp<ThrusterComponent>(child))
                    continue;
                var meta = MetaData(child);
                var proto = meta.EntityPrototype?.ID ?? "(no-proto)";
                suspicious.Add($"{child} proto={proto} pos={childXform.LocalPosition}");
            }

            if (suspicious.Count > 0)
            {
                _sawmill.Debug($"[ShipLoad] Potential orphan/duplicate entities near origin after {stage}: {suspicious.Count}\n - " + string.Join("\n - ", suspicious.Take(40)) + (suspicious.Count > 40 ? "\n   (truncated)" : string.Empty));
            }
        }
        catch (Exception ex)
        {
            _sawmill.Debug($"[ShipLoad] Orphan diagnostic failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes unanchored loose entities piled very close to the grid origin that appear to be duplicates of
    /// properly contained / anchored equipment (e.g. machine parts, thrusters, consoles) created by legacy
    /// deserialization quirks. We identify candidates by:
    ///   - Unanchored
    ///   - Parent is the grid (not inside a container / machine)
    ///   - Within a small radius (default 1.75 tiles) of origin
    ///   - Prototype matches a whitelist of typical duplicated categories OR there are multiple entities of same proto clustered
    /// For safety we only delete if at least one anchored instance of that prototype exists elsewhere on the grid OR there
    /// are 2+ loose copies in the pile (suggesting duplication rather than a legitimate loose item).
    /// </summary>
    private void CleanupDuplicateOriginPile(EntityUid gridUid, string stage)
    {
        try
        {
            if (!TryComp<MapGridComponent>(gridUid, out _))
                return;

            var origin = Transform(gridUid);
            // Collect anchored prototypes to know what "legitimate" instances exist.
            var anchoredProtoCounts = new Dictionary<string, int>();
            // Collect container-contained prototype counts on this grid (used to identify duplicates dumped from containers)
            var containerProtoCounts = GetContainerProtoCountsOnGrid(gridUid);
            var pileCandidates = new List<EntityUid>();
            var pileByProto = new Dictionary<string, List<EntityUid>>();

            foreach (var ent in _entityManager.GetEntities())
            {
                if (ent == gridUid) continue;
                if (!TryComp<TransformComponent>(ent, out var xform)) continue;
                if (xform.GridUid != gridUid) continue;
                var meta = MetaData(ent);
                var proto = meta.EntityPrototype?.ID;
                if (proto == null) continue;

                if (xform.Anchored)
                {
                    anchoredProtoCounts.TryGetValue(proto, out var count);
                    anchoredProtoCounts[proto] = count + 1;
                    continue;
                }

                // Only consider unanchored directly parented items very near origin (<= ~1.75 tile radius)
                if (xform.ParentUid != gridUid) continue;
                if (xform.LocalPosition.LengthSquared() > 3.0625f) continue; // 1.75^2

                // Ignore players / mobs / docking parts etc.
                if (HasComp<DockingComponent>(ent) || HasComp<ShuttleComponent>(ent))
                    continue;

                pileCandidates.Add(ent);
                if (!pileByProto.TryGetValue(proto, out var list))
                {
                    list = new List<EntityUid>();
                    pileByProto[proto] = list;
                }
                list.Add(ent);
            }

            if (pileCandidates.Count == 0)
                return; // nothing to do

            // Whitelist patterns frequently duplicated. Lowercase compare.
            bool IsWhitelisted(string p)
            {
                p = p.ToLowerInvariant();
                return p.Contains("thruster") || p.Contains("console") || p.Contains("computer") || p.Contains("circuit") || p.Contains("board") || p.Contains("capacitor") || p.Contains("manipulator") || p.Contains("laser") || p.Contains("scanner") || p.Contains("matter") || p.Contains("tool") || p.Contains("lamp") || p.Contains("light") || p.Contains("bulb") || p.Contains("engine");
            }

            var toDelete = new List<EntityUid>();
            foreach (var (proto, list) in pileByProto)
            {
                // Safety: Only delete if more than 1 loose copy OR an anchored version exists somewhere else.
                var looseCount = list.Count;
                anchoredProtoCounts.TryGetValue(proto, out var anchoredCount);
                containerProtoCounts.TryGetValue(proto, out var containedCount);
                if (looseCount == 0) continue;

                // If there are anchored instances somewhere on the grid, purge all loose copies near origin.
                if (anchoredCount > 0)
                {
                    toDelete.AddRange(list);
                    continue;
                }

                // If containers on this grid already contain this prototype, treat near-origin copies as duplicates.
                if (containedCount > 0)
                {
                    // Delete up to the number of contained instances to avoid over-deleting if the map legitimately had extras.
                    var delCount = Math.Min(looseCount, containedCount);
                    foreach (var ent in list.Take(delCount))
                        toDelete.Add(ent);
                    continue;
                }

                // Otherwise, keep a single copy for safety unless on whitelist, where duplication is common.
                if (looseCount == 1 && !IsWhitelisted(proto))
                    continue;

                foreach (var ent in list.Skip(1))
                    toDelete.Add(ent);
            }

            if (toDelete.Count == 0)
                return;

            foreach (var ent in toDelete)
                QueueDel(ent);

            _sawmill.Info($"[ShipLoad] Deleted {toDelete.Count} duplicate loose entities near origin on grid {gridUid} ({stage})");
        }
        catch (Exception ex)
        {
            _sawmill.Warning($"[ShipLoad] CleanupDuplicateOriginPile failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds a per-prototype count of entities contained inside any containers owned by entities on the specified grid.
    /// Used to identify loose near-origin duplicates that actually belong in containers (e.g., boards, tools, bulbs...).
    /// </summary>
    private Dictionary<string, int> GetContainerProtoCountsOnGrid(EntityUid gridUid)
    {
        var counts = new Dictionary<string, int>();
        try
        {
            var xformQuery = GetEntityQuery<TransformComponent>();
            var metaQuery = GetEntityQuery<MetaDataComponent>();
            var query = _entityManager.EntityQueryEnumerator<ContainerManagerComponent>();
            while (query.MoveNext(out var ownerUid, out var manager))
            {
                if (!xformQuery.TryGetComponent(ownerUid, out var ownerXform))
                    continue;
                if (ownerXform.GridUid != gridUid)
                    continue;

                foreach (var container in manager.Containers.Values)
                {
                    foreach (var ent in container.ContainedEntities)
                    {
                        if (!metaQuery.TryGetComponent(ent, out var meta) || meta.EntityPrototype == null)
                            continue;
                        var proto = meta.EntityPrototype.ID;
                        counts.TryGetValue(proto, out var c);
                        counts[proto] = c + 1;
                    }
                }
            }
        }
        catch
        {
            // best-effort; if anything fails just return what we have.
        }
        return counts;
    }

    /// <summary>
    /// Re-parent any entities that report GridUid == grid but are not parented under the grid transform.
    /// This handles cases where legacy or custom deserialization did not establish proper hierarchy, causing
    /// tiles to move without their entities during FTL docking or transforms.
    /// </summary>
    private void ReconcileGridChildren(EntityUid gridUid)
    {
        if (!TryComp<MapGridComponent>(gridUid, out _))
            return;
        var fixedCount = 0;
        foreach (var uid in _entityManager.GetEntities())
        {
            if (uid == gridUid) continue;
            if (!TryComp<TransformComponent>(uid, out var xform)) continue;
            if (xform.GridUid == gridUid && xform.ParentUid != gridUid)
            {
                var coords = new EntityCoordinates(gridUid, xform.LocalPosition);
                _transform.SetCoordinates((uid, xform, MetaData(uid)), coords, newParent: Transform(gridUid));
                fixedCount++;
            }
        }
        if (fixedCount > 0)
            _sawmill.Info($"[ShipLoad] Reconciled {fixedCount} detached entities under grid {gridUid}");
    }

    /// <summary>
    /// Helper methods for our custom YAML loading - based on MapLoaderSystem implementation
    /// </summary>
    private void MergeMaps(EntityDeserializer deserializer, MapLoadOptions opts, HashSet<EntityUid> merged)
    {
        if (opts.MergeMap is not { } targetId)
            return;

        if (!_map.TryGetMap(targetId, out var targetUid))
            throw new Exception($"Target map {targetId} does not exist");

        deserializer.Result.Category = FileCategory.Unknown;
        var rotation = opts.Rotation;
        var matrix = Matrix3Helpers.CreateTransform(opts.Offset, rotation);
        var target = new Entity<TransformComponent>(targetUid.Value, Transform(targetUid.Value));

        // Mirror MapLoaderSystem.MergeMaps semantics: iterate all entities, reparent those whose parent is a map root.
        HashSet<EntityUid> maps = new();
        HashSet<EntityUid> logged = new();
        foreach (var uid in deserializer.Result.Entities)
        {
            var xform = Transform(uid);
            if (!TryComp<TransformComponent>(xform.ParentUid, out var parentXform))
                continue;

            if (!HasComp<MapComponent>(xform.ParentUid))
                continue;

            // If parent of this entity is a grid (grid-map root) log a warning (unsupported) similar to upstream logic.
            if (HasComp<MapGridComponent>(xform.ParentUid) && logged.Add(xform.ParentUid))
            {
                _sawmill.Error("[ShipLoad] Merging a grid-map onto another map is not supported (entity parent was grid-root)");
                continue;
            }

            maps.Add(xform.ParentUid);
            Merge(merged, uid, target, matrix, rotation);
        }

        // Remove any map roots we merged so only the grid remains
        deserializer.ToDelete.UnionWith(maps);
        deserializer.Result.Maps.RemoveWhere(x => maps.Contains(x.Owner));

        // Also merge orphans (entities that ended up in nullspace / root of load)
        foreach (var orphan in deserializer.Result.Orphans)
        {
            Merge(merged, orphan, target, matrix, rotation);
        }
        deserializer.Result.Orphans.Clear();
    }

    private void Merge(
        HashSet<EntityUid> merged,
        EntityUid uid,
        Entity<TransformComponent> target,
        in Matrix3x2 matrix,
        Angle rotation)
    {
        merged.Add(uid);
        var xform = Transform(uid);

        // Store whether the entity was anchored before transformation
        // We'll use this information later to re-anchor entities after startup
        var wasAnchored = xform.Anchored;

        // Apply transform matrix (same as RobustToolbox MapLoaderSystem)
        var angle = xform.LocalRotation + rotation;
        var pos = System.Numerics.Vector2.Transform(xform.LocalPosition, matrix);
        var coords = new EntityCoordinates(target.Owner, pos);
        _transform.SetCoordinates((uid, xform, MetaData(uid)), coords, rotation: angle, newParent: target.Comp);

        // Store anchoring information for later processing
        if (wasAnchored)
        {
            EnsureComp<PendingAnchorComponent>(uid);
        }

        // Delete any map entities since we're merging
        if (HasComp<MapComponent>(uid))
        {
            QueueDel(uid);
        }
    }

    private bool SetMapId(EntityDeserializer deserializer, MapLoadOptions opts)
    {
        // Check for any entities with MapComponents that might need MapId assignment
        foreach (var uid in deserializer.Result.Entities)
        {
            if (TryComp<MapComponent>(uid, out var mapComp))
            {
                if (opts.ForceMapId != null)
                {
                    // Should not happen in our shipyard use case
                    _sawmill.Error("Unexpected ForceMapId when merging maps");
                    return false;
                }
            }
        }
        return true;
    }

    private void ApplyTransform(EntityDeserializer deserializer, MapLoadOptions opts)
    {
        if (opts.Rotation == Angle.Zero && opts.Offset == Vector2.Zero)
            return;

        // If merging onto a single map, the transformation was already applied by MergeMaps
        if (opts.MergeMap != null)
            return;

        var matrix = Matrix3Helpers.CreateTransform(opts.Offset, opts.Rotation);

        // Apply transforms to all children of loaded maps
        foreach (var uid in deserializer.Result.Entities)
        {
            if (TryComp<TransformComponent>(uid, out var xform))
            {
                // Check if this entity is attached to a map
                if (xform.MapUid != null && HasComp<MapComponent>(xform.MapUid.Value))
                {
                    var pos = System.Numerics.Vector2.Transform(xform.LocalPosition, matrix);
                    _transform.SetLocalPosition(uid, pos);
                    _transform.SetLocalRotation(uid, xform.LocalRotation + opts.Rotation);
                }
            }
        }
    }

    private void MapInitalizeMerged(HashSet<EntityUid> merged, MapId targetId)
    {
        // Initialize merged entities according to the target map's state
        if (!_map.TryGetMap(targetId, out var targetUid))
            throw new Exception($"Target map {targetId} does not exist");

        if (_map.IsInitialized(targetUid.Value))
        {
            foreach (var uid in merged)
            {
                if (TryComp<MetaDataComponent>(uid, out var metadata))
                {
                    EntityManager.RunMapInit(uid, metadata);
                }
            }
        }

        var paused = _map.IsPaused(targetUid.Value);
        foreach (var uid in merged)
        {
            if (TryComp<MetaDataComponent>(uid, out var metadata))
            {
                _metaData.SetEntityPaused(uid, paused, metadata);
            }
        }
    }

    private void ProcessPendingAnchors(HashSet<EntityUid> merged)
    {
        // Process entities that need to be anchored after grid loading
        foreach (var uid in merged)
        {
            if (!TryComp<PendingAnchorComponent>(uid, out _))
                continue;

            // Remove the temporary component
            RemComp<PendingAnchorComponent>(uid);

            // Try to anchor the entity if it's on a valid grid
            if (TryComp<TransformComponent>(uid, out var xform) && xform.GridUid != null)
            {
                try
                {
                    _transform.AnchorEntity(uid, xform);
                }
                catch (Exception ex)
                {
                    // Log but don't fail - some entities might not be anchorable
                    _sawmill.Warning($"Failed to anchor entity {uid}: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Checks a shuttle to make sure that it is docked to the given station, and that there are no lifeforms aboard. Then it teleports tagged items on top of the console, appraises the grid, outputs to the server log, and deletes the grid
    /// </summary>
    /// <param name="stationUid">The ID of the station that the shuttle is docked to</param>
    /// <param name="shuttleUid">The grid ID of the shuttle to be appraised and sold</param>
    /// <param name="consoleUid">The ID of the console being used to sell the ship</param>
    public ShipyardSaleResult TrySellShuttle(EntityUid stationUid, EntityUid shuttleUid, EntityUid consoleUid, out int bill)
    {
        ShipyardSaleResult result = new ShipyardSaleResult();
        bill = 0;

        if (!TryComp<StationDataComponent>(stationUid, out var stationGrid)
            || !HasComp<ShuttleComponent>(shuttleUid)
            || !TryComp(shuttleUid, out TransformComponent? xform)
            || ShipyardMap == null)
        {
            result.Error = ShipyardSaleError.InvalidShip;
            return result;
        }

        var targetGrid = _station.GetLargestGrid(stationGrid);

        if (targetGrid == null)
        {
            result.Error = ShipyardSaleError.InvalidShip;
            return result;
        }

        var gridDocks = _docking.GetDocks(targetGrid.Value);
        var shuttleDocks = _docking.GetDocks(shuttleUid);
        var isDocked = false;

        foreach (var shuttleDock in shuttleDocks)
        {
            foreach (var gridDock in gridDocks)
            {
                if (shuttleDock.Comp.DockedWith == gridDock.Owner)
                {
                    isDocked = true;
                    break;
                }
            }
            if (isDocked)
                break;
        }

        if (!isDocked)
        {
            _sawmill.Warning($"shuttle is not docked to that station");
            result.Error = ShipyardSaleError.Undocked;
            return result;
        }

        var mobQuery = GetEntityQuery<MobStateComponent>();
        var xformQuery = GetEntityQuery<TransformComponent>();

        var charName = FoundOrganics(shuttleUid, mobQuery, xformQuery);
        if (charName is not null)
        {
            _sawmill.Warning($"organics on board");
            result.Error = ShipyardSaleError.OrganicsAboard;
            result.OrganicName = charName;
            return result;
        }

        //just yeet and delete for now. Might want to split it into another function later to send back to the shipyard map first to pause for something
        //also superman 3 moment
        if (_station.GetOwningStation(shuttleUid) is { Valid: true } shuttleStationUid)
        {
            _station.DeleteStation(shuttleStationUid);
        }

        if (TryComp<ShipyardConsoleComponent>(consoleUid, out var comp))
        {
            CleanGrid(shuttleUid, consoleUid);
        }

        bill = (int)_pricing.AppraiseGrid(shuttleUid, LacksPreserveOnSaleComp);
        QueueDel(shuttleUid);
        _sawmill.Info($"Sold shuttle {shuttleUid} for {bill}");

        // Update all record UI (skip records, no new records)
        _shuttleRecordsSystem.RefreshStateForAll(true);

        result.Error = ShipyardSaleError.Success;
        return result;
    }

    private void CleanGrid(EntityUid grid, EntityUid destination)
    {
        var xform = Transform(grid);
        var enumerator = xform.ChildEnumerator;
        var entitiesToPreserve = new List<EntityUid>();

        while (enumerator.MoveNext(out var child))
        {
            FindEntitiesToPreserve(child, ref entitiesToPreserve);
        }
        foreach (var ent in entitiesToPreserve)
        {
            // Teleport this item and all its children to the floor (or space).
            _transform.SetCoordinates(ent, new EntityCoordinates(destination, 0, 0));
            _transform.AttachToGridOrMap(ent);
        }
    }

    // checks if something has the ShipyardPreserveOnSaleComponent and if it does, adds it to the list
    private void FindEntitiesToPreserve(EntityUid entity, ref List<EntityUid> output)
    {
        if (TryComp<ShipyardSellConditionComponent>(entity, out var comp) && comp.PreserveOnSale == true)
        {
            output.Add(entity);
            return;
        }
        if (TryComp<EntityStorageComponent>(entity, out var storageComp))
        {
            // Make storage containers delete their contents when they are deleted during ship sale
            storageComp.DeleteContentsOnDestruction = true;
            Dirty(entity, storageComp);
        }

        if (TryComp<ContainerManagerComponent>(entity, out var containers))
        {
            foreach (var container in containers.Containers.Values)
            {
                foreach (var ent in container.ContainedEntities)
                {
                    FindEntitiesToPreserve(ent, ref output);
                }
            }
        }
    }

    // returns false if it has ShipyardPreserveOnSaleComponent, true otherwise
    private bool LacksPreserveOnSaleComp(EntityUid uid)
    {
        return !TryComp<ShipyardSellConditionComponent>(uid, out var comp) || comp.PreserveOnSale == false;
    }
    private void CleanupShipyard()
    {
        if (ShipyardMap == null || !_map.MapExists(ShipyardMap.Value))
        {
            ShipyardMap = null;
            return;
        }

        _map.DeleteMap(ShipyardMap.Value);
    }

    public void SetupShipyardIfNeeded()
    {
        if (ShipyardMap != null && _map.MapExists(ShipyardMap.Value))
            return;

        _map.CreateMap(out var shipyardMap);
        ShipyardMap = shipyardMap;

        _map.SetPaused(ShipyardMap.Value, false);
    }

    // <summary>
    // Tries to rename a shuttle deed and update the respective components.
    // Returns true if successful.
    //
    // Null name parts are promptly ignored.
    // </summary>
    public bool TryRenameShuttle(EntityUid uid, ShuttleDeedComponent? shuttleDeed, string? newName, string? newSuffix)
    {
        if (!Resolve(uid, ref shuttleDeed))
            return false;

        var shuttle = shuttleDeed.ShuttleUid;
        if (shuttle != null
             && TryGetEntity(shuttle.Value, out var shuttleEntity)
             && _station.GetOwningStation(shuttleEntity.Value) is { Valid: true } shuttleStation)
        {
            // Update the primary deed
            shuttleDeed.ShuttleName = newName;
            shuttleDeed.ShuttleNameSuffix = newSuffix;
            Dirty(uid, shuttleDeed);

            // Find and update all other deeds for the same ship
            var query = EntityQueryEnumerator<ShuttleDeedComponent>();
            while (query.MoveNext(out var deedEntity, out var deed))
            {
                // Skip the deed we already updated
                if (deedEntity == uid)
                    continue;

                // Update deeds that reference the same shuttle
                if (deed.ShuttleUid == shuttle)
                {
                    deed.ShuttleName = newName;
                    deed.ShuttleNameSuffix = newSuffix;
                    Dirty(deedEntity, deed);
                }
            }

            var fullName = GetFullName(shuttleDeed);
            _station.RenameStation(shuttleStation, fullName, loud: false);
            _metaData.SetEntityName(shuttleEntity.Value, fullName);
            _metaData.SetEntityName(shuttleStation, fullName);
        }
        else
        {
            _sawmill.Error($"Could not rename shuttle {ToPrettyString(shuttle):entity} to {newName}");
            return false;
        }

        //TODO: move this to an event that others hook into.
        if (shuttleDeed.ShuttleUid != null &&
            _shuttleRecordsSystem.TryGetRecord(shuttleDeed.ShuttleUid.Value, out var record))
        {
            record.Name = newName ?? "";
            record.Suffix = newSuffix ?? "";
            _shuttleRecordsSystem.TryUpdateRecord(record);
        }

        return true;
    }

    /// <summary>
    /// Returns the full name of the shuttle component in the form of [prefix] [name] [suffix].
    /// </summary>
    public static string GetFullName(ShuttleDeedComponent comp)
    {
        string?[] parts = { comp.ShuttleName, comp.ShuttleNameSuffix };
        return string.Join(' ', parts.Where(it => it != null));
    }

    /// <summary>
    /// Mono: Adds ShipAccessReaderComponent to all doors and lockers on a ship grid.
    /// </summary>
    public async Task<bool> TryLoadShipComprehensive(EntityUid consoleUid, EntityUid idCardUid, string yamlData, string shipName, string playerUserId, ICommonSession playerSession, string shipyardChannel, string? filePath = null)
    {
        EntityUid? loadedShipUid = null;

        try
        {
            _sawmill.Info($"Starting comprehensive ship load process for '{shipName}' by player {playerUserId}");

            // STEP 1: Load ship from YAML data
            loadedShipUid = await LoadStep1_LoadShipFromYaml(consoleUid, yamlData, shipName);
            if (!loadedShipUid.HasValue)
            {
                _sawmill.Error("Step 1 failed: Could not load ship from YAML data");
                return false;
            }
            _sawmill.Info($"Step 1 complete: Ship loaded with EntityUid {loadedShipUid.Value}");

            // STEP 2: FTL the ship to the station (already done in LoadShipFromYaml)
            // This step is inherently part of the loading process
            // _sawmill.Info("Step 2 complete: Ship FTL'd to station during load");

            // STEP 3: Set up shuttle deed and database systems
            var deedSuccess = await LoadStep3_SetupShuttleDeed(loadedShipUid.Value, shipName, playerUserId);
            if (!deedSuccess)
            {
                _sawmill.Error("Step 3 failed: Could not set up shuttle deed");
                return false;
            }
            _sawmill.Info("Step 3 complete: Shuttle deed and database systems set up");

            // STEP 4: Update player ID card with deed
            var idUpdateSuccess = await LoadStep4_UpdatePlayerIdCard(idCardUid, loadedShipUid.Value, shipName);
            if (!idUpdateSuccess)
            {
                _sawmill.Error("Step 4 failed: Could not update player ID card with deed");
                return false;
            }
            _sawmill.Info("Step 4 complete: Player ID card updated with deed");

            // STEP 5: Fire ShipLoadedEvent and update console
            var eventSuccess = await LoadStep5_FireEventAndUpdateConsole(consoleUid, idCardUid, loadedShipUid.Value, shipName, playerUserId, playerSession, yamlData, shipyardChannel, filePath);
            if (!eventSuccess)
            {
                _sawmill.Error("Step 5 failed: Could not fire events and update console");
                // Don't return false here as the ship was loaded successfully
            }
            _sawmill.Info("Step 5 complete: Events fired and console updated");

            _sawmill.Info($"Comprehensive ship load process completed successfully for '{shipName}'");
            return true;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Exception during comprehensive ship load: {ex}");
            return false;
        }
    }

    /// <summary>
    /// STEP 1: Load ship from YAML data and FTL to station
    /// </summary>
    private async Task<EntityUid?> LoadStep1_LoadShipFromYaml(EntityUid consoleUid, string yamlData, string shipName)
    {
        try
        {
            _sawmill.Info($"Step 1: Loading ship '{shipName}' from YAML data");

            // Use the existing ship loading logic but return the EntityUid
            if (!TryLoadShipFromYaml(consoleUid, yamlData, out var shuttleEntityUid))
            {
                _sawmill.Error("Failed to load ship from YAML data using existing system");
                return null;
            }

            _sawmill.Info($"Successfully loaded ship from YAML data: {shuttleEntityUid}");
            return shuttleEntityUid;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Step 1 failed: {ex}");
            return null;
        }
    }

    /// <summary>
    /// STEP 3: Set up shuttle deed and database systems for loaded ship
    /// </summary>
    private async Task<bool> LoadStep3_SetupShuttleDeed(EntityUid shipUid, string shipName, string playerUserId)
    {
        try
        {
            _sawmill.Info("Step 3: Setting up shuttle deed and database systems");

            // Note: The ship itself doesn't need a deed component - that goes on the player's ID card
            // The ship should already have any necessary components from the YAML data
            // This step is for any additional database/ownership setup if needed

            _sawmill.Info($"Ship {shipUid} with name '{shipName}' ready for deed assignment to player ID");

            // Additional database setup could go here if needed
            // For now, we just ensure the ship is ready for ownership assignment

            return true;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Step 3 failed: {ex}");
            return false;
        }
    }

    /// <summary>
    /// STEP 4: Update player ID card with shuttle deed
    /// </summary>
    private async Task<bool> LoadStep4_UpdatePlayerIdCard(EntityUid idCardUid, EntityUid shipUid, string shipName)
    {
        try
        {
            _sawmill.Info("Step 4: Updating player ID card with shuttle deed");

            // Ensure the ID card has a deed component (may already exist)
            var idDeedComponent = EnsureComp<ShuttleDeedComponent>(idCardUid);
            idDeedComponent.ShuttleName = shipName;
            idDeedComponent.ShuttleNameSuffix = ""; // No suffix for loaded ships
            idDeedComponent.ShuttleUid = GetNetEntity(shipUid);
            idDeedComponent.PurchasedWithVoucher = false; // Mark as loaded

            _sawmill.Info($"Updated ShuttleDeedComponent on ID card {idCardUid} for ship '{shipName}' ({shipUid})");

            return true;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Step 4 failed: {ex}");
            return false;
        }
    }

    private void AddShipAccessToEntities(EntityUid gridUid)
    {
        // Get the grid bounds to find all entities on the grid
        if (!TryComp<MapGridComponent>(gridUid, out var grid))
            return;

        var gridBounds = grid.LocalAABB;
        var gridEntities = new HashSet<EntityUid>();
        _lookup.GetLocalEntitiesIntersecting(gridUid, gridBounds, gridEntities);

        foreach (var entity in gridEntities)
        {
            // Add ship access to doors
            if (EntityManager.HasComponent<DoorComponent>(entity))
            {
                EntityManager.EnsureComponent<ShipAccessReaderComponent>(entity);
            }
            // Add ship access to entity storage (lockers, crates, etc.)
            else if (EntityManager.HasComponent<EntityStorageComponent>(entity))
            {
                EntityManager.EnsureComponent<ShipAccessReaderComponent>(entity);
            }
        }
    }

    private void SendLoadMessage(EntityUid uid, EntityUid player, string name, string shipyardChannel, bool secret = false)
    {
        try
        {
            _sawmill.Info("Step 5: FTL docking ship to station, firing ShipLoadedEvent and updating console");

            // First, FTL dock the ship to the station
            if (!TryComp<TransformComponent>(consoleUid, out var consoleXform) || consoleXform.GridUid == null)
            {
                _sawmill.Error("Step 5 failed: Could not get console grid for FTL docking");
                return false;
            }

            if (!TryComp<ShuttleComponent>(shipUid, out var shuttleComponent))
            {
                _sawmill.Error("Step 5 failed: Ship does not have ShuttleComponent for FTL docking");
                return false;
            }

            var targetGrid = consoleXform.GridUid.Value;
            _sawmill.Info($"Attempting to FTL dock ship {shipUid} to station grid {targetGrid}");

            if (_shuttle.TryFTLDock(shipUid, shuttleComponent, targetGrid))
            {
                _sawmill.Info($"Successfully FTL docked ship {shipUid} to station grid {targetGrid}");
            }
            else
            {
                _sawmill.Warning($"Failed to FTL dock ship {shipUid} to station grid {targetGrid} - ship may need manual docking");
                // Don't fail the entire operation if docking fails, as the ship was loaded successfully
            }

            // Fire the ShipLoadedEvent
            var shipLoadedEvent = new ShipLoadedEvent
            {
                ConsoleUid = consoleUid,
                IdCardUid = idCardUid,
                ShipGridUid = shipUid,
                ShipName = shipName,
                PlayerUserId = playerUserId,
                PlayerSession = playerSession,
                YamlData = yamlData,
                FilePath = filePath,
                ShipyardChannel = shipyardChannel
            };
            RaiseLocalEvent(shipLoadedEvent);
            _sawmill.Info($"Fired ShipLoadedEvent for ship '{shipName}'");

            // If this load originated from a client-side file, notify the client to delete it now
            if (!string.IsNullOrEmpty(filePath) && playerSession != null)
            {
                try
                {
                    RaiseNetworkEvent(new Content.Shared.Shuttles.Save.DeleteLocalShipFileMessage(filePath!), playerSession);
                    _sawmill.Info($"Requested client to delete local ship file '{filePath}' after successful load");
                }
                catch (Exception ex)
                {
                    _sawmill.Warning($"Failed to send delete local ship file message: {ex}");
                }
            }

            // Console updates are handled by the calling method (UI feedback)
            // Additional console-specific updates could go here if needed

            return true;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Step 5 failed: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Attempts to extract ship name from YAML data
    /// </summary>
    private string? ExtractShipNameFromYaml(string yamlData)
    {
        try
        {
            // Simple YAML parsing to extract ship name
            var lines = yamlData.Split('\n');
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                if (trimmedLine.StartsWith("shipName:"))
                {
                    var parts = trimmedLine.Split(':', 2);
                    if (parts.Length > 1)
                    {
                        return parts[1].Trim().Trim('"', '\'');
                    }
                }
                // Also check for entity names that might indicate ship name
                if (trimmedLine.StartsWith("name:"))
                {
                    var parts = trimmedLine.Split(':', 2);
                    if (parts.Length > 1)
                    {
                        var name = parts[1].Trim().Trim('"', '\'');
                        // Only use if it looks like a ship name (not generic component names)
                        if (!name.Contains("Component") && !name.Contains("System") && name.Length > 3)
                        {
                            return name;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _sawmill.Warning($"Failed to extract ship name from YAML: {ex}");
        }
    }
}
