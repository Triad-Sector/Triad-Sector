using Content.Server._NF.Shipyard.Components;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared._NF.Shipyard.Events;
using Content.Shared.Shuttles.Save; // For SendShipSaveDataClientMessage
using Content.Server.Maps;
using Content.Server.Atmos.Components;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Atmos.Piping.Components;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Shared.Access.Components;
using Content.Shared.Atmos.Components;
using Content.Shared.Movement.Components;
using Content.Shared.Power.Components;
using Content.Shared.VendingMachines;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
// using Content.Shared.Access.Components; // duplicate using removed
using System.Diagnostics.CodeAnalysis;
using Robust.Server.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using System.Numerics;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Utility;
using Robust.Shared.ContentPack;
using Robust.Server.Player;
using Robust.Shared.EntitySerialization;
using Robust.Shared.Physics.Components;
using Robust.Shared.Containers;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Robust.Shared.Configuration;
using Content.Shared.HL.CCVar;

// Suppress RA0004 for this file. There is no Task<Result> usage here, but the analyzer
// occasionally reports a false positive during Release/integration builds.
#pragma warning disable RA0004
// Suppress naming rule for _NF namespace prefix (modding convention)
#pragma warning disable IDE1006
namespace Content.Server._NF.Shipyard.Systems;

/// <summary>
/// System for saving ships using the MapLoaderSystem infrastructure.
/// Saves ships as complete YAML files similar to savegrid command,
/// after cleaning them of problematic components and moving to exports folder.
/// </summary>
[SuppressMessage("Usage", "RA0004:Risk of deadlock from accessing Task<T>.Result", Justification = "No Task.Result used; false positive during Release/integration builds.")]
public sealed class ShipyardGridSaveSystem : EntitySystem
{
    [Dependency] private readonly IEntityManager _entityManager = default!;
    [Dependency] private readonly IEntitySystemManager _entitySystemManager = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly SharedTransformSystem _transformSystem = default!;
    [Dependency] private readonly IResourceManager _resourceManager = default!;
    [Dependency] private readonly SharedContainerSystem _containerSystem = default!;
    [Dependency] private readonly Content.Server.Shuttles.Save.ShipSerializationSystem _shipSerialization = default!;
    [Dependency] private readonly IConfigurationManager _configManager = default!;

    private ISawmill _sawmill = default!;
    private MapLoaderSystem _mapLoader = default!;
    private SharedMapSystem _mapSystem = default!;

    public override void Initialize()
    {
        base.Initialize();

        // Initialize sawmill for logging
        _sawmill = Logger.GetSawmill("shipyard.gridsave");

        // Get the MapLoaderSystem reference
        _mapLoader = _entitySystemManager.GetEntitySystem<MapLoaderSystem>();
        _mapSystem = _entitySystemManager.GetEntitySystem<SharedMapSystem>();

        // Subscribe to shipyard console events
        SubscribeLocalEvent<ShipyardConsoleComponent, ShipyardConsoleSaveMessage>(OnSaveShipMessage);
    }

    private void OnSaveShipMessage(EntityUid consoleUid, ShipyardConsoleComponent component, ShipyardConsoleSaveMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        if (component.TargetIdSlot.ContainerSlot?.ContainedEntity is not { Valid: true } targetId)
        {
            _sawmill.Warning("No ID card in shipyard console slot");
            return;
        }

        if (!_entityManager.TryGetComponent<ShuttleDeedComponent>(targetId, out var deed))
        {
            _sawmill.Warning("ID card does not have a shuttle deed");
            return;
        }

        if (deed.ShuttleUid == null || !_entityManager.TryGetEntity(deed.ShuttleUid.Value, out var shuttleUid))
        {
            _sawmill.Warning("Shuttle deed does not reference a valid shuttle");
            return;
        }

        if (!_entityManager.TryGetComponent<MapGridComponent>(shuttleUid.Value, out var gridComponent))
        {
            _sawmill.Warning("Shuttle entity is not a valid grid");
            return;
        }

        // Get player session
        if (!_playerManager.TryGetSessionByEntity(player, out var playerSession))
        {
            _sawmill.Warning("Could not get player session");
            return;
        }

        _sawmill.Info($"Starting ship save for {deed.ShuttleName ?? "Unknown_Ship"} owned by {playerSession.Name}");

        // Run save inline on the main thread to avoid off-thread ECS access.
        var success = TrySaveGridAsShip(shuttleUid.Value, deed.ShuttleName ?? "Unknown_Ship", playerSession.UserId.ToString(), playerSession);

        if (success)
        {
            // Clean up the deed after successful save
            _entityManager.RemoveComponent<ShuttleDeedComponent>(targetId);

            // Also remove any other shuttle deeds that reference this shuttle
            RemoveAllShuttleDeeds(shuttleUid.Value);

            // Transfer semantics: after saving, delete the live ship grid.
            // Use QueueDel to schedule deletion safely at end-of-frame to avoid PVS or in-frame references.
            QueueDel(shuttleUid.Value);
            _sawmill.Info($"Successfully saved ship {deed.ShuttleName}; queued deletion of grid {shuttleUid.Value}");
        }
        else
        {
            _sawmill.Error($"Failed to save ship {deed.ShuttleName}");
        }
    }

    /// <summary>
    /// Removes all ShuttleDeedComponents that reference the specified shuttle EntityUid
    /// </summary>
    private void RemoveAllShuttleDeeds(EntityUid shuttleUid)
    {
        var query = _entityManager.EntityQueryEnumerator<ShuttleDeedComponent>();
        var deedsToRemove = new List<EntityUid>();

        while (query.MoveNext(out var entityUid, out var deed))
        {
            if (deed.ShuttleUid != null && _entityManager.TryGetEntity(deed.ShuttleUid.Value, out var deedShuttleEntity) && deedShuttleEntity == shuttleUid)
            {
                deedsToRemove.Add(entityUid);
            }
        }

        foreach (var deedEntity in deedsToRemove)
        {
            _entityManager.RemoveComponent<ShuttleDeedComponent>(deedEntity);
            _sawmill.Info($"Removed shuttle deed from entity {deedEntity}");
        }
    }

    /// <summary>
    /// Saves a grid to YAML without mutating live game state. Uses ShipSerializationSystem to serialize in-place.
    /// This avoids moving the grid to temporary maps or deleting any entities, preventing PVS/map deletion issues.
    /// </summary>
    public bool TrySaveGridAsShip(EntityUid gridUid, string shipName, string playerUserId, ICommonSession playerSession)
    {
        if (!_entityManager.HasComponent<MapGridComponent>(gridUid))
        {
            _sawmill.Error($"Entity {gridUid} is not a valid grid");
            return false;
        }

        try
        {
            _sawmill.Info($"Serializing ship grid {gridUid} as '{shipName}' using standard grid format (non-destructive save)");

            // Use MapLoaderSystem to save directly to a temporary file in user data.
            // Attach a temporary filter to optionally exclude vending machines from the save to avoid load-time spikes.
            var excludeVending = _configManager.GetCVar(HLCCVars.ExcludeVendingInShipSave);
            EntitySerializer.IsSerializableDelegate? veto = null;
            if (excludeVending)
            {
                veto = (Entity<MetaDataComponent> ent, ref bool serializable) =>
                {
                    if (_entityManager.HasComponent<VendingMachineComponent>(ent))
                        serializable = false;
                };
            }

            // Add a veto to exclude obvious near-origin loose duplicates from being saved.
            // This is a safety net so we never persist the post-load duplicate pile even if cleanup missed it.
            EntitySerializer.IsSerializableDelegate originPileVeto = (Entity<MetaDataComponent> ent, ref bool serializable) =>
            {
                if (!_entityManager.TryGetComponent<TransformComponent>(ent, out var xform))
                    return;
                if (xform.GridUid != gridUid)
                    return;
                // Only consider directly parented, unanchored entities very close to origin.
                if (xform.ParentUid != gridUid || xform.Anchored)
                    return;
                if (xform.LocalPosition.LengthSquared() > 3.0625f) // ~1.75 tiles
                    return;

                // Heuristic match list similar to load-time cleanup; skip structure components automatically.
                var id = ent.Comp.EntityPrototype?.ID ?? string.Empty;
                var low = id.ToLowerInvariant();
                if (low.Contains("thruster") || low.Contains("console") || low.Contains("computer") || low.Contains("circuit") || low.Contains("board") || low.Contains("capacitor") || low.Contains("manipulator") || low.Contains("laser") || low.Contains("scanner") || low.Contains("matter") || low.Contains("tool") || low.Contains("lamp") || low.Contains("light") || low.Contains("bulb") || low.Contains("engine"))
                {
                    serializable = false;
                }
            };

            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var fileName = $"{shipName}_{timestamp}.yml";
            var tempFilePath = new ResPath("/") / "UserData" / fileName;

            if (veto != null)
                _mapLoader.OnIsSerializable += veto;
            _mapLoader.OnIsSerializable += originPileVeto;
            try
            {
                var saved = _mapLoader.TrySaveGrid(gridUid, tempFilePath);
                if (!saved)
                {
                    _sawmill.Error($"Failed to save grid {gridUid} to {fileName}");
                    return false;
                }
            }
            finally
            {
                if (veto != null)
                    _mapLoader.OnIsSerializable -= veto;
                _mapLoader.OnIsSerializable -= originPileVeto;
            }

            // Read the YAML we just wrote and send to the client for local saving.
            string yaml;
            using (var fileStream = _resourceManager.UserData.OpenRead(tempFilePath))
            using (var reader = new StreamReader(fileStream))
            {
                yaml = reader.ReadToEnd();
            }

            var saveMessage = new SendShipSaveDataClientMessage(shipName, yaml);
            RaiseNetworkEvent(saveMessage, playerSession);
            _sawmill.Info($"Sent ship data '{shipName}' to client {playerSession.Name} for local saving");

            // Clean up the temporary file (best-effort).
            _ = TryDeleteFileWithRetry(tempFilePath);

            // Fire ShipSavedEvent for bookkeeping; DO NOT delete the grid or maps here.
            var gridSavedEvent = new ShipSavedEvent
            {
                GridUid = gridUid,
                ShipName = shipName,
                PlayerUserId = playerUserId,
                PlayerSession = playerSession
            };
            RaiseLocalEvent(gridSavedEvent);
            _sawmill.Info($"Fired ShipSavedEvent for '{shipName}'");

            return true;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Exception during non-destructive ship save: {ex}");
            return false;
        }
    }

    #region 5-Step Ship Save Process

    /// <summary>
    /// STEP 1: Create a blank map and teleport the ship to it for saving
    /// </summary>
    private async Task<MapId?> Step1_CreateBlankMapAndTeleportShip(EntityUid gridUid, string shipName, ICommonSession playerSession)
    {
        MapId tempMapId = default;
        try
        {
            _sawmill.Info("Step 1: Creating blank map and teleporting ship");

            // Create a temporary blank map for saving
            tempMapId = _mapManager.CreateMap();
            _sawmill.Info($"Created temporary map {tempMapId}");

            // Step 2: Move the grid to the temporary map and clean it
            var tempGridUid = await MoveAndCleanGrid(gridUid, tempMapId);
            if (tempGridUid == null)
            {
                _sawmill.Error("Failed to move and clean grid");
                return null;
            }

            _sawmill.Info($"Successfully moved and cleaned grid to {tempGridUid}");

            // Step 3: Save the grid using MapLoaderSystem to a temporary file
            var fileName = $"{shipName}.yml";
            var tempFilePath = new ResPath("/") / "UserData" / fileName;
            _sawmill.Info($"Attempting to save grid as {fileName}");

            bool success = _mapLoader.TrySaveGrid(tempGridUid.Value, tempFilePath);

            if (success)
            {
                _sawmill.Info($"Successfully saved grid to {fileName}");

                // Step 4: Read the YAML file and send to client
                try
                {
                    using var fileStream = _resourceManager.UserData.OpenRead(tempFilePath);
                    using var reader = new StreamReader(fileStream);
                    var yamlContent = await reader.ReadToEndAsync();

                    // Send the YAML data to the client for local saving
                    var saveMessage = new SendShipSaveDataClientMessage(shipName, yamlContent);
                    RaiseNetworkEvent(saveMessage, playerSession);

                    _sawmill.Info($"Sent ship data '{shipName}' to client {playerSession.Name} for local saving");

                    // Clean up the temporary server file with retry logic
                    await TryDeleteFileWithRetry(tempFilePath);
                }
                catch (Exception ex)
                {
                    _sawmill.Error($"Failed to read/send YAML file: {ex}");
                    success = false;
                }
            }
            else
            {
                _sawmill.Error($"Failed to save grid to {fileName}");
            }

            return success ? tempMapId : null;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Exception during ship save: {ex}");
            return null;
        }
        finally
        {
            // Step 6: Clean up temporary resources with proper timing
            if (tempMapId != default)
            {
                // Give all systems significant time to finish processing the map deletion
                await Task.Delay(500);

                try
                {
                    _mapManager.DeleteMap(tempMapId);
                    _sawmill.Info($"Cleaned up temporary map {tempMapId}");
                }
                catch (Exception ex)
                {
                    _sawmill.Error($"Failed to clean up temporary map {tempMapId}: {ex}");
                }
            }

            // Delete the original grid after all processing is complete
            if (_entityManager.EntityExists(gridUid))
            {
                // Additional delay to ensure all systems finish processing entity changes
                await Task.Delay(300);

                try
                {
                    _entityManager.DeleteEntity(gridUid);
                    _sawmill.Info($"Deleted original grid entity {gridUid}");
                }
                catch (Exception ex)
                {
                    _sawmill.Error($"Failed to delete original grid entity {gridUid}: {ex}");
                }
            }
        }
    }

    /// <summary>
    /// STEP 2: Empty the contents of any container on the grid, then clean the grid of problematic components
    /// and delete freefloating entities that are not anchored or connected to the grid
    /// </summary>
    private async Task<bool> Step2_EmptyContainersAndCleanGrid(EntityUid gridUid)
    {
        try
        {
            _sawmill.Info("Step 2: Emptying containers and cleaning grid");

            var allEntities = new HashSet<EntityUid>();

            // Get all entities on the grid
            if (_entityManager.TryGetComponent<MapGridComponent>(gridUid, out var grid))
            {
                var gridBounds = grid.LocalAABB;
                var lookupSystem = _entitySystemManager.GetEntitySystem<EntityLookupSystem>();
                foreach (var entity in lookupSystem.GetEntitiesIntersecting(gridUid, gridBounds))
                {
                    if (entity != gridUid) // Don't include the grid itself
                        allEntities.Add(entity);
                }
            }

            _sawmill.Info($"Found {allEntities.Count} entities to process");

            var entitiesRemoved = 0;
            var containersEmptied = 0;

            // First pass: Empty all containers
            foreach (var entity in allEntities.ToList())
            {
                if (!_entityManager.EntityExists(entity))
                    continue;

                // Empty any containers by deleting their contents
                if (_entityManager.TryGetComponent<ContainerManagerComponent>(entity, out var containerManager))
                {
                    foreach (var container in containerManager.Containers.Values)
                    {
                        var containedEntities = container.ContainedEntities.ToList();
                        foreach (var containedEntity in containedEntities)
                        {
                            try
                            {
                                _entityManager.DeleteEntity(containedEntity);
                                entitiesRemoved++;
                            }
                            catch (Exception ex)
                            {
                                _sawmill.Warning($"Failed to delete contained entity {containedEntity}: {ex}");
                            }
                        }
                        if (containedEntities.Count > 0)
                        {
                            containersEmptied++;
                            _sawmill.Info($"Emptied container with {containedEntities.Count} items");
                        }
                    }
                }
            }

            // Second pass: Delete freefloating entities (not anchored or connected to grid)
            var freefloatingEntities = new List<EntityUid>();
            foreach (var entity in allEntities)
            {
                if (!_entityManager.EntityExists(entity))
                    continue;

                // Skip structural grid components
                if (_entityManager.HasComponent<MapGridComponent>(entity))
                    continue;

                // Check if entity is anchored or in a container
                if (_entityManager.TryGetComponent<TransformComponent>(entity, out var transform))
                {
                    // If not anchored and not in a container, mark for deletion
                    if (!transform.Anchored && !_containerSystem.IsEntityInContainer(entity))
                    {
                        freefloatingEntities.Add(entity);
                    }
                }
            }

            // Delete freefloating entities
            foreach (var entity in freefloatingEntities)
            {
                try
                {
                    if (_entityManager.EntityExists(entity))
                    {
                        _entityManager.DeleteEntity(entity);
                        entitiesRemoved++;
                    }
                }
                catch (Exception ex)
                {
                    _sawmill.Warning($"Failed to delete freefloating entity {entity}: {ex}");
                }
            }

            _sawmill.Info($"Step 2 complete: Emptied {containersEmptied} containers, removed {entitiesRemoved} entities");
            return true;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Step 2 failed: {ex}");
            return false;
        }
    }

    /// <summary>
    /// STEP 3: Delete any vending machines or remaining structures that would pose a problem to ship saving
    /// </summary>
    private async Task<bool> Step3_DeleteProblematicStructures(EntityUid gridUid)
    {
        try
        {
            _sawmill.Info("Step 3: Deleting problematic structures");

            var allEntities = new HashSet<EntityUid>();

            // Get all remaining entities on the grid
            if (_entityManager.TryGetComponent<MapGridComponent>(gridUid, out var grid))
            {
                var gridBounds = grid.LocalAABB;
                var lookupSystem = _entitySystemManager.GetEntitySystem<EntityLookupSystem>();
                foreach (var entity in lookupSystem.GetEntitiesIntersecting(gridUid, gridBounds))
                {
                    if (entity != gridUid) // Don't include the grid itself
                        allEntities.Add(entity);
                }
            }

            var structuresRemoved = 0;
            var componentsRemoved = 0;

            foreach (var entity in allEntities.ToList())
            {
                if (!_entityManager.EntityExists(entity))
                    continue;

                // Delete vending machines completely
                if (_entityManager.HasComponent<VendingMachineComponent>(entity))
                {
                    _sawmill.Info($"Removing vending machine entity {entity}");
                    _entityManager.DeleteEntity(entity);
                    structuresRemoved++;
                    continue;
                }

                // Remove problematic components from remaining entities

                // Note: Removed PhysicsComponent deletion that was causing collision issues in loaded ships
                // PhysicsComponent and FixturesComponent are needed for proper collision detection

                // Remove atmospheric components that hold runtime state

                // Reset power components to clean state
            }

            _sawmill.Info($"Step 3 complete: Removed {structuresRemoved} problematic structures, cleaned {componentsRemoved} components");
            return true;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Step 3 failed: {ex}");
            return false;
        }
    }

    /// <summary>
    /// STEP 4: Save the grid
    /// </summary>
    private async Task<bool> Step4_SaveGrid(EntityUid gridUid, string shipName, ICommonSession playerSession)
    {
        try
        {
            _sawmill.Info($"Step 4: Saving grid as '{shipName}'");

            // Save the grid using MapLoaderSystem to a temporary file
            var fileName = $"{shipName}.yml";
            var tempFilePath = new ResPath("/") / "UserData" / fileName;

            bool success = _mapLoader.TrySaveGrid(gridUid, tempFilePath);

            if (success)
            {
                _sawmill.Info($"Successfully saved grid to {fileName}");

                // Read the YAML file and send to client
                try
                {
                    using var fileStream = _resourceManager.UserData.OpenRead(tempFilePath);
                    using var reader = new StreamReader(fileStream);
                    var yamlContent = await reader.ReadToEndAsync();

                    // Send the YAML data to the client for local saving
                    var saveMessage = new SendShipSaveDataClientMessage(shipName, yamlContent);
                    RaiseNetworkEvent(saveMessage, playerSession);

                    _sawmill.Info($"Sent ship data '{shipName}' to client {playerSession.Name} for local saving");

                    // Clean up the temporary server file
                    await TryDeleteFileWithRetry(tempFilePath);
                }
                catch (Exception ex)
                {
                    _sawmill.Error($"Failed to read/send YAML file: {ex}");
                    success = false;
                }
            }
            else
            {
                _sawmill.Error($"Failed to save grid to {fileName}");
            }

            return success;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Step 4 failed: {ex}");
            return false;
        }
    }

    /// <summary>
    /// STEP 5: Throw event to say grid was saved, remove shuttle deed from player's ID, and update console
    /// </summary>
    private async Task<bool> Step5_PostSaveCleanupAndEvents(EntityUid originalGridUid, string shipName, string playerUserId, ICommonSession playerSession)
    {
        try
        {
            _sawmill.Info("Step 5: Post-save cleanup and events");

            // Fire grid saved event
            var gridSavedEvent = new ShipSavedEvent
            {
                GridUid = originalGridUid,
                ShipName = shipName,
                PlayerUserId = playerUserId,
                PlayerSession = playerSession
            };
            RaiseLocalEvent(gridSavedEvent);
            _sawmill.Info($"Fired ShipSavedEvent for '{shipName}'");

            // Deed removal is handled where the save is initiated (console slot entity) after success

            // Delete the original grid entity now that save is complete
            if (_entityManager.EntityExists(originalGridUid))
            {
                await Task.Delay(100); // Brief delay to ensure all events are processed
                _entityManager.DeleteEntity(originalGridUid);
                _sawmill.Info($"Deleted original grid entity {originalGridUid}");
            }

            _sawmill.Info("Step 5 complete: Events fired, deed removed, grid deleted");
            return true;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Step 5 failed: {ex}");
            return false;
        }
    }

    #endregion

    /// <summary>
    /// Legacy method - replaced by 5-step process above</summary>
    private async Task<EntityUid?> MoveAndCleanGrid(EntityUid originalGridUid, MapId targetMapId)
    {
        try
        {
            // Move the grid to the temporary map and normalize its rotation
            var gridTransform = _entityManager.GetComponent<TransformComponent>(originalGridUid);
            _transformSystem.SetCoordinates(originalGridUid, new EntityCoordinates(_mapManager.GetMapEntityId(targetMapId), Vector2.Zero));

            // Normalize grid rotation to 0 degrees
            _transformSystem.SetLocalRotation(originalGridUid, Angle.Zero);

            _sawmill.Info($"Moved grid {originalGridUid} to temporary map {targetMapId} and normalized rotation");

            // Clean the grid of problematic components
            CleanGridForSaving(originalGridUid);

            return originalGridUid;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Failed to move and clean grid: {ex}");
            return null;
        }
    }

    /// <summary>
    /// Removes problematic components from a grid before saving.
    /// This includes session-specific data, vending machines, runtime state, etc.
    /// Uses a two-phase approach: first delete problematic entities, then clean remaining entities.
    /// </summary>
    public void CleanGridForSaving(EntityUid gridUid)
    {
        _sawmill.Info($"Starting grid cleanup for {gridUid}");

        var allEntities = new HashSet<EntityUid>();

        // Get all entities on the grid
        if (_entityManager.TryGetComponent<MapGridComponent>(gridUid, out var grid))
        {
            var gridBounds = grid.LocalAABB;
            var lookupSystem = _entitySystemManager.GetEntitySystem<EntityLookupSystem>();
            foreach (var entity in lookupSystem.GetEntitiesIntersecting(gridUid, gridBounds))
            {
                if (entity != gridUid) // Don't include the grid itself
                    allEntities.Add(entity);
            }
        }

        _sawmill.Info($"Found {allEntities.Count} entities to clean on grid");

        var entitiesRemoved = 0;
        var componentsRemoved = 0;

        // PHASE 1: Do not delete entities to preserve physics counts
        // We'll clean by removing components instead (e.g., VendingMachineComponent)
        _sawmill.Info("Phase 1: Skipping entity deletions to preserve physics components");
        _sawmill.Info($"Phase 1 complete: deleted {entitiesRemoved} entities");

        // PHASE 2: Clean components from remaining entities
        // Re-gather remaining entities to avoid processing deleted ones
        _sawmill.Info("Phase 2: Cleaning components from remaining entities");

        var remainingEntities = new HashSet<EntityUid>();

        if (_entityManager.TryGetComponent<MapGridComponent>(gridUid, out grid))
        {
            var gridBounds = grid.LocalAABB;
            var lookupSystem = _entitySystemManager.GetEntitySystem<EntityLookupSystem>();
            foreach (var entity in lookupSystem.GetEntitiesIntersecting(gridUid, gridBounds))
            {
                if (entity != gridUid) // Don't include the grid itself
                    remainingEntities.Add(entity);
            }
        }

        _sawmill.Info($"Found {remainingEntities.Count} remaining entities to clean components from");

        foreach (var entity in remainingEntities)
        {
            try
            {
                // Check if entity still exists before processing
                if (!_entityManager.EntityExists(entity))
                    continue;

                // Remove session-specific components that shouldn't be saved
                if (_entityManager.RemoveComponent<ActorComponent>(entity))
                    componentsRemoved++;
                if (_entityManager.RemoveComponent<EyeComponent>(entity))
                    componentsRemoved++;

                // Remove vending machine behavior but keep the entity to preserve physics
                if (_entityManager.RemoveComponent<VendingMachineComponent>(entity))
                    componentsRemoved++;

                // Note: Removed PhysicsComponent deletion that was causing collision issues in loaded ships
                // PhysicsComponent and FixturesComponent are needed for proper collision detection

                // Reset power components to clean state through the proper system
                if (_entityManager.TryGetComponent<BatteryComponent>(entity, out var battery))
                {
                    // Use the battery system instead of direct access
                    if (_entitySystemManager.TryGetEntitySystem<BatterySystem>(out var batterySystem))
                    {
                        batterySystem.SetCharge(entity, battery.MaxCharge);
                    }
                }

                // Remove problematic atmospheric state
                if (_entityManager.RemoveComponent<AtmosDeviceComponent>(entity))
                    componentsRemoved++;

                // Remove any other problematic components
                // Note: We're being conservative here - removing things that commonly cause issues
            }
            catch (Exception ex)
            {
                _sawmill.Warning($"Error cleaning entity {entity}: {ex}");
            }
        }

        _sawmill.Info($"Grid cleanup complete: deleted {entitiesRemoved} entities, removed {componentsRemoved} components from {remainingEntities.Count} remaining entities");
    }

    /// <summary>
    /// Writes YAML data to a temporary file in UserData for loading
    /// </summary>
    public async Task<bool> WriteYamlToUserData(string fileName, string yamlData)
    {
        try
        {
            var userDataPath = _resourceManager.UserData;
            var resPath = new ResPath(fileName);

            await using var stream = userDataPath.OpenWrite(resPath);
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(yamlData);

            _sawmill.Info($"Temporary YAML file written: {resPath}");
            return true;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Failed to write temporary YAML file {fileName}: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Attempts to delete a file with retry logic to handle file access conflicts
    /// </summary>
    private async Task TryDeleteFileWithRetry(ResPath filePath, int maxRetries = 3, int delayMs = 100)
    {
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                _resourceManager.UserData.Delete(filePath);
                _sawmill.Info($"Successfully deleted temporary server file {filePath}");
                return;
            }
            catch (IOException ex) when (attempt < maxRetries - 1)
            {
                _sawmill.Warning($"File deletion attempt {attempt + 1} failed for {filePath}: {ex.Message}. Retrying in {delayMs}ms...");
                await Task.Delay(delayMs);
                delayMs *= 2; // Exponential backoff
            }
            catch (Exception ex)
            {
                _sawmill.Error($"Failed to delete temporary file {filePath} on attempt {attempt + 1}: {ex}");
                if (attempt == maxRetries - 1)
                {
                    _sawmill.Error($"Giving up on deleting {filePath} after {maxRetries} attempts");
                }
                break;
            }
        }
    }

}
