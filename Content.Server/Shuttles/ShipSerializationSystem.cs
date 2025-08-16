using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Value;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Numerics;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Content.Shared.Shuttles.Save;
using Robust.Shared.Network;
using Robust.Shared.Maths;
using System;
using Content.Shared.Coordinates;
using Content.Shared.Coordinates.Helpers;
using Robust.Shared.Log;
using Robust.Server.GameObjects;
using Content.Server.Atmos.Components;
using Content.Server.Atmos;
using Content.Server.Atmos.EntitySystems;
using Robust.Shared.Containers;
using Robust.Server.Physics;
using Content.Shared.Atmos;
using Robust.Shared.Timing;
using Robust.Shared.Console;
using Content.Shared.Decals;
using Content.Server.Decals;
using static Content.Shared.Decals.DecalGridComponent;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics;
using Robust.Shared.Configuration;
using System.Security.Cryptography;
using Content.Shared._NF.CCVar;
using Robust.Shared.Player;
using Robust.Server.Player;
using Content.Server.Administration.Commands;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Components.SolutionManager;
using Content.Shared.Paper;
using Content.Shared.Stacks;
using Robust.Shared.Serialization.Markdown;

namespace Content.Server.Shuttles.Save
{
    public sealed class ShipSerializationSystem : EntitySystem
    {
        [Dependency] private readonly IEntityManager _entityManager = default!;
        [Dependency] private readonly IMapManager _mapManager = default!;
        [Dependency] private readonly ISerializationManager _serializationManager = default!;
        [Dependency] private readonly ITileDefinitionManager _tileDefManager = default!;
        [Dependency] private readonly MapSystem _map = default!;
        [Dependency] private readonly AtmosphereSystem _atmosphere = default!;
        [Dependency] private readonly IConsoleHost _consoleHost = default!;
        [Dependency] private readonly DecalSystem _decalSystem = default!;
        [Dependency] private readonly IConfigurationManager _configManager = default!;
        [Dependency] private readonly IServerNetManager _netManager = default!;
        [Dependency] private readonly SharedContainerSystem _containerSystem = default!;
        [Dependency] private readonly ServerIdentityService _serverIdentity = default!;
        [Dependency] private readonly SharedTransformSystem _transformSystem = default!;
        
        private ISawmill _sawmill = default!;

        private ISerializer _serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        private IDeserializer _deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        public override void Initialize()
        {
            base.Initialize();
            _sawmill = Logger.GetSawmill("ship-serialization");
        }

    public ShipGridData SerializeShip(EntityUid gridId, NetUserId playerId, string shipName)
        {
            if (!_entityManager.TryGetComponent<MapGridComponent>(gridId, out var grid))
            {
                throw new ArgumentException($"Grid with ID {gridId} not found.");
            }

            var shipGridData = new ShipGridData
            {
                Metadata = new ShipMetadata
                {
                    OriginalGridId = gridId.ToString(),
                    PlayerId = playerId.ToString(),
                    ShipName = shipName,
                    Timestamp = DateTime.UtcNow
                }
            };

            var gridData = new GridData
            {
                GridId = grid.Owner.ToString()
            };

            // Proper tile serialization
            var tiles = _map.GetAllTiles(gridId, grid);
            foreach (var tile in tiles)
            {
                var tileDef = _tileDefManager[tile.Tile.TypeId];
                if (tileDef.ID == "Space") // Skip space tiles
                    continue;

                gridData.Tiles.Add(new TileData 
                { 
                    X = tile.GridIndices.X, 
                    Y = tile.GridIndices.Y, 
                    TileType = tileDef.ID 
                });
            }

            // Serialized tiles
            

            // Skip atmosphere serialization as TileAtmosphere is not serializable
            // Atmosphere will be restored using fixgridatmos command during loading
            // Skipping atmosphere serialization

            // Serialize decal data
            if (_entityManager.TryGetComponent<DecalGridComponent>(gridId, out var decalComponent))
            {
                try
                {
                    var decalNode = _serializationManager.WriteValue(decalComponent.ChunkCollection, notNullableOverride: true);
                    var decalYaml = _serializer.Serialize(decalNode);
                    gridData.DecalData = decalYaml;
                    // Serialized decal data
                }
                catch (Exception ex)
                {
                    _sawmill.Error($"Failed to serialize decal data: {ex.Message}");
                }
            }
            else
            {
                // No decal component found
            }

            // Enhanced entity serialization - includes all entities on grid and in containers
            var serializedEntities = new HashSet<EntityUid>();
            
            foreach (var entity in _entityManager.EntityQuery<TransformComponent>())
            {
                if (entity.GridUid != gridId)
                    continue;

                var uid = entity.Owner;
                var meta = _entityManager.GetComponentOrNull<MetaDataComponent>(uid);
                var proto = meta?.EntityPrototype?.ID ?? string.Empty;

                // Serialize all entities, including contained ones
                var entityData = SerializeEntity(uid, entity, proto, gridId);
                if (entityData != null)
                {
                    gridData.Entities.Add(entityData);
                    serializedEntities.Add(uid);
                    // Serialized entity
                }
            }

            // Also serialize entities that are contained but might not be in the grid query
            SerializeContainedEntities(gridId, gridData, serializedEntities);

            // Validate container relationships before finalizing
            ValidateContainerRelationships(gridData);

            shipGridData.Grids.Add(gridData);
            
            // Check for overlapping entities at same coordinates AFTER serialization
            var positionGroups = gridData.Entities.GroupBy(e => new { e.Position.X, e.Position.Y }).Where(g => g.Count() > 1);
            if (positionGroups.Any())
            {
                _sawmill.Warning($"Found {positionGroups.Count()} positions with overlapping entities during serialization:");
                foreach (var group in positionGroups)
                {
                    _sawmill.Warning($"  Position ({group.Key.X}, {group.Key.Y}): {string.Join(", ", group.Select(e => e.Prototype))}");
                }
            }
            
            // Generate server-bound checksum AFTER all data is finalized
            var checksum = GenerateServerBoundChecksum(shipGridData);
            shipGridData.Metadata.Checksum = checksum;
            _sawmill.Info($"Ship serialized with checksum: {checksum.Substring(0, Math.Min(30, checksum.Length))}...");

            return shipGridData;
        }

        public string SerializeShipGridDataToYaml(ShipGridData data)
        {
            return _serializer.Serialize(data);
        }

        public ShipGridData DeserializeShipGridDataFromYaml(string yamlString, Guid loadingPlayerId)
        {
            return DeserializeShipGridDataFromYaml(yamlString, loadingPlayerId, out _);
        }

        public ShipGridData DeserializeShipGridDataFromYaml(string yamlString, Guid loadingPlayerId, out bool wasLegacyConverted)
        {
            _sawmill.Info($"Deserializing ship YAML for player {loadingPlayerId}");
            wasLegacyConverted = false;
            ShipGridData data;
            try
            {
                data = _deserializer.Deserialize<ShipGridData>(yamlString);
                // Successfully deserialized YAML
            }
            catch (Exception ex)
            {
                _sawmill.Error($"YAML deserialization failed: {ex.Message}");
                throw;
            }

            // Store original checksum BEFORE any calculations
            var originalStoredChecksum = data.Metadata.Checksum;
            // Original stored checksum
            
            // Check if this is a server-bound checksum
            if (originalStoredChecksum.StartsWith("S:"))
            {
                // Check blacklist first
                if (ShipBlacklistService.IsBlacklisted(originalStoredChecksum))
                {
                    var reason = ShipBlacklistService.GetBlacklistReason(originalStoredChecksum);
                    _sawmill.Warning($"SECURITY: Blacklisted ship load attempt blocked - {reason}");
                    throw new UnauthorizedAccessException($"This ship has been blacklisted by server administration: {reason}");
                }
                
                // Validate server-bound checksum
                var isValid = ValidateServerBoundChecksum(originalStoredChecksum, data);
                if (!isValid)
                {
                    throw new UnauthorizedAccessException("Server-bound checksum validation failed!");
                }
            }
            else
            {
                // Check blacklist for legacy checksums too
                if (ShipBlacklistService.IsBlacklisted(originalStoredChecksum))
                {
                    var reason = ShipBlacklistService.GetBlacklistReason(originalStoredChecksum);
                    _sawmill.Warning($"SECURITY: Blacklisted ship load attempt blocked - {reason}");
                    throw new UnauthorizedAccessException($"This ship has been blacklisted by server administration: {reason}");
                }
                // Legacy checksum validation
                var actualChecksum = GenerateChecksum(data.Grids);
                
                // Verify the stored checksum wasn't modified
                if (data.Metadata.Checksum != originalStoredChecksum)
                {
                    _sawmill.Error($"BUG: Stored checksum was modified during GenerateChecksum! Was: {originalStoredChecksum}, Now: {data.Metadata.Checksum}");
                }
                
                // Expected checksum
                // Actual checksum
                // Grid count
                // Tile count
                // Entity count
                
                // Check if checksum validation is enabled via cvar
                var checksumValidationEnabled = _configManager.GetCVar(NFCCVars.ShipyardChecksumValidation);
            
                if (checksumValidationEnabled)
                {
                    // Detect checksum format and handle appropriately
                    bool isLegacySHA = originalStoredChecksum.Length == 64 && !originalStoredChecksum.Contains(":");
                    bool isLegacyEnhanced = originalStoredChecksum.Contains(":PP-");
                    bool isLegacyBasic = originalStoredChecksum.Contains(":E") && !originalStoredChecksum.Contains(":C") && !originalStoredChecksum.Contains(":CM");
                    bool isCurrentFormat = originalStoredChecksum.Contains(":C") && originalStoredChecksum.Contains(":CM");
                    
                    if (isLegacySHA)
                    {
                        // Legacy SHA256 checksum - regenerate current checksum and update file metadata for future saves
                        _sawmill.Warning($"Legacy SHA256 checksum detected: {originalStoredChecksum}");
                        _sawmill.Info($"Legacy checksum detected - ship will be converted to secure format");
                        // Current checksum
                        
                        // Update the metadata with new checksum for conversion
                        data.Metadata.Checksum = actualChecksum;
                    wasLegacyConverted = true;
                    // Legacy checksum compatibility mode
                }
                else if (isLegacyEnhanced)
                {
                    // Handle legacy enhanced format - strip old ":PP-" suffix if present
                    var cleanStoredChecksum = originalStoredChecksum.Substring(0, originalStoredChecksum.IndexOf(":PP-"));
                    _sawmill.Info($"Detected legacy enhanced checksum format, using cleaned version: {cleanStoredChecksum}");
                    
                    if (!string.Equals(cleanStoredChecksum, actualChecksum, StringComparison.OrdinalIgnoreCase))
                    {
                        _sawmill.Error($"CHECKSUM VALIDATION FAILED!");
                        _sawmill.Error($"Expected: {cleanStoredChecksum}");
                        _sawmill.Error($"Actual: {actualChecksum}");
                        _sawmill.Error($"SECURITY VIOLATION: Ship data tampering detected!");
                        _sawmill.Error($"SECURITY: Tampered ship - {data.Metadata.ShipName} by player {data.Metadata.PlayerId}");
                        throw new InvalidOperationException("Checksum mismatch! Ship data may have been tampered with.");
                    }
                    
                    _sawmill.Info("Legacy enhanced checksum validation passed successfully");
                }
                else if (isLegacyBasic)
                {
                    // Legacy basic format (before container support) - convert to new format
                    _sawmill.Warning($"Legacy basic checksum detected: {originalStoredChecksum}");
                    _sawmill.Info($"Basic checksum detected - ship will be upgraded to enhanced format");
                    _sawmill.Info($"Current enhanced checksum would be: {actualChecksum}");
                    
                    // Update the metadata with new checksum for conversion
                    data.Metadata.Checksum = actualChecksum;
                    wasLegacyConverted = true;
                    _sawmill.Info("Legacy basic checksum compatibility mode - validation passed, marked for conversion");
                }
                else if (isCurrentFormat)
                {
                    // Current enhanced format validation
                    if (!string.Equals(originalStoredChecksum, actualChecksum, StringComparison.OrdinalIgnoreCase))
                    {
                        _sawmill.Error($"CHECKSUM VALIDATION FAILED!");
                        _sawmill.Error($"Expected: {originalStoredChecksum}");
                        _sawmill.Error($"Actual: {actualChecksum}");
                        _sawmill.Error($"SECURITY VIOLATION: Ship data tampering detected!");
                        _sawmill.Error($"SECURITY: Tampered ship - {data.Metadata.ShipName} by player {data.Metadata.PlayerId}");
                        throw new InvalidOperationException("Checksum mismatch! Ship data may have been tampered with.");
                    }
                    
                    _sawmill.Info("Enhanced checksum validation passed successfully");
                }
                else
                {
                    // Unknown format - try to validate anyway but warn
                    _sawmill.Warning($"Unknown checksum format detected: {originalStoredChecksum}");
                    _sawmill.Warning($"Attempting validation with current format...");
                    
                    if (!string.Equals(originalStoredChecksum, actualChecksum, StringComparison.OrdinalIgnoreCase))
                    {
                        _sawmill.Warning($"Checksum validation failed for unknown format");
                        _sawmill.Info($"Expected: {originalStoredChecksum}");
                        _sawmill.Info($"Actual: {actualChecksum}");
                        _sawmill.Warning($"Ship may have been modified or saved with different version");
                        // Don't throw for unknown formats - allow loading but log warning
                    }
                    else
                    {
                        _sawmill.Info("Unknown format checksum validation passed");
                    }
                }
                }
                else
                {
                    _sawmill.Info("Checksum validation disabled by server configuration");
                }
            }


            if (data.Metadata.PlayerId != loadingPlayerId.ToString())
            {
                throw new UnauthorizedAccessException("Player ID mismatch! You can only load ships you have saved.");
            }

            return data;
        }

        public EntityUid ReconstructShipOnMap(ShipGridData shipGridData, MapId targetMap, Vector2 offset)
        {
            _sawmill.Info($"Reconstructing ship: {shipGridData.Grids.Count} grids, {shipGridData.Grids[0].Entities.Count} entities");
            if (shipGridData.Grids.Count == 0)
            {
                throw new ArgumentException("No grid data to reconstruct.");
            }

            var primaryGridData = shipGridData.Grids[0];
            // Primary grid entities
            // Primary grid tiles
            
            var newGrid = _mapManager.CreateGrid(targetMap);
            // Created new grid
            
            // Note: Grid splitting prevention would require internal access
            // TODO: Investigate alternative approaches to prevent grid splitting

            // Move grid to the specified offset position
            var gridXform = Transform(newGrid.Owner);
            gridXform.WorldPosition = offset;

            // Reconstruct tiles in connectivity order to prevent grid splitting
            var tilesToPlace = new List<(Vector2i coords, Tile tile)>();
            foreach (var tileData in primaryGridData.Tiles)
            {
                if (string.IsNullOrEmpty(tileData.TileType) || tileData.TileType == "Space")
                    continue;

                try
                {
                    var tileDef = _tileDefManager[tileData.TileType];
                    var tile = new Tile(tileDef.TileId);
                    var tileCoords = new Vector2i(tileData.X, tileData.Y);
                    tilesToPlace.Add((tileCoords, tile));
                }
                catch (Exception ex)
                {
                    _sawmill.Error($"Failed to prepare tile {tileData.TileType} at ({tileData.X}, {tileData.Y}): {ex.Message}");
                }
            }

            // Sort tiles by connectivity using flood-fill to prevent grid splitting
            if (tilesToPlace.Any())
            {
                tilesToPlace = SortTilesForConnectivity(tilesToPlace);
            }

            // Place tiles maintaining connectivity
            foreach (var (coords, tile) in tilesToPlace)
            {
                _map.SetTile(newGrid.Owner, newGrid, coords, tile);
            }
            // Placed tiles

            // Apply fixgridatmos-style atmosphere to all loaded ships
            // Applying atmosphere
            ApplyFixGridAtmosphereToGrid(newGrid.Owner);

            // Restore decal data using proper DecalSystem API
            if (!string.IsNullOrEmpty(primaryGridData.DecalData))
            {
                try
                {
                    var decalChunkCollection = _deserializer.Deserialize<DecalGridChunkCollection>(primaryGridData.DecalData);
                    var decalsRestored = 0;
                    var decalsFailed = 0;
                    
                    // Ensure the grid has a DecalGridComponent
                    _entityManager.EnsureComponent<DecalGridComponent>(newGrid.Owner);
                    
                    foreach (var (chunkPos, chunk) in decalChunkCollection.ChunkCollection)
                    {
                        foreach (var (decalId, decal) in chunk.Decals)
                        {
                            // Convert the decal coordinates to EntityCoordinates on the new grid
                            var decalCoords = new EntityCoordinates(newGrid.Owner, decal.Coordinates);
                            
                            // Use the DecalSystem to properly add the decal
                            if (_decalSystem.TryAddDecal(decal.Id, decalCoords, out _, decal.Color, decal.Angle, decal.ZIndex, decal.Cleanable))
                            {
                                decalsRestored++;
                            }
                            else
                            {
                                decalsFailed++;
                                _sawmill.Warning($"Failed to restore decal {decal.Id} at {decal.Coordinates}");
                            }
                        }
                    }
                    
                    _sawmill.Info($"Restored {decalsRestored} decals from {decalChunkCollection.ChunkCollection.Count} chunks");
                    if (decalsFailed > 0)
                    {
                        _sawmill.Warning($"Failed to restore {decalsFailed} decals");
                    }
                }
                catch (Exception ex)
                {
                    _sawmill.Error($"Failed to restore decal data: {ex.Message}");
                }
            }
            else
            {
                // No decal data found
            }

            // Two-phase entity reconstruction to handle containers properly
            // Starting two-phase entity reconstruction
            
            var entityIdMapping = new Dictionary<string, EntityUid>();
            var spawnedEntities = new List<(EntityUid entity, string prototype, Vector2 position)>();
            
            // Check if this is a legacy save without container data
            var hasContainerData = primaryGridData.Entities.Any(e => e.IsContainer || e.IsContained);
            if (!hasContainerData)
            {
                _sawmill.Info("Legacy save detected - no container data found, using single-phase reconstruction");
                ReconstructEntitiesLegacyMode(primaryGridData, newGrid, entityIdMapping);
                return newGrid.Owner;
            }
            
            // Phase 1: Spawn all non-contained entities (containers, infrastructure, furniture)
            // Pre-filter entities into separate lists in a single pass
            var nonContainedEntities = new List<EntityData>();
            var containedEntitiesList = new List<EntityData>();
            
            foreach (var entity in primaryGridData.Entities)
            {
                if (string.IsNullOrEmpty(entity.Prototype))
                    continue;
                    
                if (entity.IsContained)
                    containedEntitiesList.Add(entity);
                else
                    nonContainedEntities.Add(entity);
            }
            
            foreach (var entityData in nonContainedEntities)
            {

                try
                {
                    var coordinates = new EntityCoordinates(newGrid.Owner, entityData.Position);
                    var newEntity = SpawnEntityWithComponents(entityData, coordinates);
                    
                    if (newEntity != null)
                    {
                        entityIdMapping[entityData.EntityId] = newEntity.Value;
                        spawnedEntities.Add((newEntity.Value, entityData.Prototype, entityData.Position));
                    }
                }
                catch (Exception ex)
                {
                    _sawmill.Error($"Failed to spawn entity {entityData.Prototype}: {ex.Message}");
                }
            }
            
            // Phase 1 complete
            
            // Phase 2: Spawn contained entities and insert them into containers
            // Phase 2: Spawning contained entities
            var containedSpawned = 0;
            var containedFailed = 0;
            
            foreach (var entityData in containedEntitiesList)
            {

                try
                {
                    // Spawn the entity in a temporary location (will be moved to container)
                    var tempCoordinates = new EntityCoordinates(newGrid.Owner, Vector2.Zero);
                    var containedEntity = SpawnEntityWithComponents(entityData, tempCoordinates);
                    
                    if (containedEntity != null)
                    {
                        entityIdMapping[entityData.EntityId] = containedEntity.Value;
                        
                        // Try to insert into the parent container
                        if (!string.IsNullOrEmpty(entityData.ParentContainerEntity) && 
                            !string.IsNullOrEmpty(entityData.ContainerSlot) &&
                            entityIdMapping.TryGetValue(entityData.ParentContainerEntity, out var parentContainer))
                        {
                            if (InsertIntoContainer(containedEntity.Value, parentContainer, entityData.ContainerSlot))
                            {
                                containedSpawned++;
                            }
                            else
                            {
                                // If insertion fails, delete the entity instead of placing at 0,0
                                _entityManager.DeleteEntity(containedEntity.Value);
                                entityIdMapping.Remove(entityData.EntityId);
                                containedFailed++;
                            }
                        }
                        else
                        {
                            // Parent container not found, delete the entity instead of placing at 0,0
                            _entityManager.DeleteEntity(containedEntity.Value);
                            entityIdMapping.Remove(entityData.EntityId);
                            containedFailed++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _sawmill.Error($"Phase 2: Failed to spawn contained entity {entityData.Prototype}: {ex.Message}");
                    containedFailed++;
                }
            }
            
            // Phase 2 complete
            
            // Log basic reconstruction statistics (only if there are failures)
            if (containedFailed > 0)
            {
                _sawmill.Warning($"{containedFailed} contained entities could not be properly placed");
            }
            
            // Skip expensive overlap/verification checking for performance

            return newGrid.Owner;
        }

        public EntityUid ReconstructShip(ShipGridData shipGridData)
        {
            _sawmill.Info($"Reconstructing ship with {shipGridData.Grids.Count} grids");
            if (shipGridData.Grids.Count == 0)
            {
                throw new ArgumentException("No grid data to reconstruct.");
            }

            var primaryGridData = shipGridData.Grids[0];
            // Primary grid entities
            
            // Note: Grid splitting prevention would require internal access
            // TODO: Investigate alternative approaches to prevent grid splitting
            
            // Create a new map for the ship instead of using MapId.Nullspace
            _map.CreateMap(out var mapId);
            _sawmill.Info($"Created new map {mapId}");
            
            var newGrid = _mapManager.CreateGrid(mapId);
            _sawmill.Info($"Created new grid {newGrid.Owner} on map {mapId}");

            // Reconstruct tiles in connectivity order to prevent grid splitting
            var tilesToPlace = new List<(Vector2i coords, Tile tile)>();
            foreach (var tileData in primaryGridData.Tiles)
            {
                if (string.IsNullOrEmpty(tileData.TileType) || tileData.TileType == "Space")
                    continue;

                try
                {
                    var tileDef = _tileDefManager[tileData.TileType];
                    var tile = new Tile(tileDef.TileId);
                    var tileCoords = new Vector2i(tileData.X, tileData.Y);
                    tilesToPlace.Add((tileCoords, tile));
                }
                catch (Exception ex)
                {
                    _sawmill.Error($"Failed to prepare tile {tileData.TileType} at ({tileData.X}, {tileData.Y}): {ex.Message}");
                }
            }

            // Sort tiles by connectivity using flood-fill to prevent grid splitting
            if (tilesToPlace.Any())
            {
                tilesToPlace = SortTilesForConnectivity(tilesToPlace);
            }

            // Place tiles maintaining connectivity
            foreach (var (coords, tile) in tilesToPlace)
            {
                _map.SetTile(newGrid.Owner, newGrid, coords, tile);
            }
            // Placed tiles

            // Apply fixgridatmos-style atmosphere to all loaded ships
            // Applying atmosphere
            ApplyFixGridAtmosphereToGrid(newGrid.Owner);

            // Restore decal data using proper DecalSystem API
            if (!string.IsNullOrEmpty(primaryGridData.DecalData))
            {
                try
                {
                    var decalChunkCollection = _deserializer.Deserialize<DecalGridChunkCollection>(primaryGridData.DecalData);
                    var decalsRestored = 0;
                    var decalsFailed = 0;
                    
                    // Ensure the grid has a DecalGridComponent
                    _entityManager.EnsureComponent<DecalGridComponent>(newGrid.Owner);
                    
                    foreach (var (chunkPos, chunk) in decalChunkCollection.ChunkCollection)
                    {
                        foreach (var (decalId, decal) in chunk.Decals)
                        {
                            // Convert the decal coordinates to EntityCoordinates on the new grid
                            var decalCoords = new EntityCoordinates(newGrid.Owner, decal.Coordinates);
                            
                            // Use the DecalSystem to properly add the decal
                            if (_decalSystem.TryAddDecal(decal.Id, decalCoords, out _, decal.Color, decal.Angle, decal.ZIndex, decal.Cleanable))
                            {
                                decalsRestored++;
                            }
                            else
                            {
                                decalsFailed++;
                                _sawmill.Warning($"Failed to restore decal {decal.Id} at {decal.Coordinates}");
                            }
                        }
                    }
                    
                    _sawmill.Info($"Restored {decalsRestored} decals from {decalChunkCollection.ChunkCollection.Count} chunks");
                    if (decalsFailed > 0)
                    {
                        _sawmill.Warning($"Failed to restore {decalsFailed} decals");
                    }
                }
                catch (Exception ex)
                {
                    _sawmill.Error($"Failed to restore decal data: {ex.Message}");
                }
            }
            else
            {
                // No decal data found
            }

            // Reconstruct entities with component restoration
            foreach (var entityData in primaryGridData.Entities)
            {
                // Skip entities with empty or null prototypes
                if (string.IsNullOrEmpty(entityData.Prototype))
                {
                    _sawmill.Debug($"Skipping entity with empty prototype at {entityData.Position}");
                    continue;
                }

                try
                {
                    var coordinates = new EntityCoordinates(newGrid.Owner, entityData.Position);
                    var newEntity = _entityManager.SpawnEntity(entityData.Prototype, coordinates);
                    
                    // Apply rotation if it exists
                    if (Math.Abs(entityData.Rotation) > 0.001f)
                    {
                        var transform = _entityManager.GetComponent<TransformComponent>(newEntity);
                        transform.LocalRotation = new Angle(entityData.Rotation);
                    }

                    
                    _sawmill.Debug($"Spawned entity {newEntity} ({entityData.Prototype}) at {entityData.Position}");
                }
                catch (Exception ex)
                {
                    _sawmill.Error($"Failed to spawn entity {entityData.Prototype}: {ex.Message}");
                    throw;
                }
            }

            return newGrid.Owner;
        }

        private bool IsEntityContained(EntityUid entityUid, EntityUid gridId)
        {
            // Check if this entity is contained within another entity (not directly on the grid)
            if (_entityManager.TryGetComponent<TransformComponent>(entityUid, out var transformComp))
            {
                var parent = transformComp.ParentUid;
                
                // Direct grid children should always be included (pipes, cables, fixtures, etc.)
                if (parent == gridId)
                {
                    return false;
                }
                
                while (parent.IsValid() && parent != gridId)
                {
                    // Get the entity prototype to make smarter containment decisions
                    var meta = _entityManager.GetComponentOrNull<MetaDataComponent>(parent);
                    var parentProto = meta?.EntityPrototype?.ID ?? string.Empty;
                    
                    // Allow infrastructure entities even if they have complex hierarchies
                    if (parentProto.Contains("Pipe") || parentProto.Contains("Cable") || 
                        parentProto.Contains("Conduit") || parentProto.Contains("Atmos") ||
                        parentProto.Contains("Wire") || parentProto.Contains("Junction"))
                    {
                        return false;
                    }
                    
                    // If we find a parent that has a ContainerManagerComponent, this entity is contained
                    // BUT exclude certain infrastructure containers that should be serialized
                    if (_entityManager.HasComponent<ContainerManagerComponent>(parent))
                    {
                        // Allow entities in certain "infrastructure" containers
                        if (parentProto.Contains("Pipe") || parentProto.Contains("Machine") || 
                            parentProto.Contains("Console") || parentProto.Contains("Computer"))
                        {
                            return false;
                        }
                        return true;
                    }
                    
                    // Move up the hierarchy
                    if (_entityManager.TryGetComponent<TransformComponent>(parent, out var parentTransform))
                        parent = parentTransform.ParentUid;
                    else
                        break;
                }
            }
            return false;
        }

        private void ApplyFixGridAtmosphereToGrid(EntityUid gridUid)
        {
            // Execute fixgridatmos console command after a short delay to allow atmosphere system to initialize
            Timer.Spawn(TimeSpan.FromMilliseconds(100), () =>
            {
                if (!_entityManager.EntityExists(gridUid))
                {
                    _sawmill.Error($"Grid {gridUid} no longer exists for atmosphere application");
                    return;
                }

                var netEntity = _entityManager.GetNetEntity(gridUid);
                var commandArgs = $"fixgridatmos {netEntity}";
                _sawmill.Info($"Running fixgridatmos command: {commandArgs}");
                
                try
                {
                    _consoleHost.ExecuteCommand(null, commandArgs);
                    _sawmill.Info($"Successfully executed fixgridatmos for grid {gridUid}");
                }
                catch (Exception ex)
                {
                    _sawmill.Error($"Failed to execute fixgridatmos command: {ex.Message}");
                }
            });
        }


        private string GenerateChecksum(List<GridData> grids)
        {
            // Enhanced tamper-detection checksum using detailed entity data including containers and components
            var checksumBuilder = new StringBuilder();
            
            foreach (var grid in grids)
            {
                // Grid identifier
                checksumBuilder.Append($"G{grid.GridId}:");
                
                // Tile summary
                var sortedTiles = grid.Tiles.OrderBy(t => t.X).ThenBy(t => t.Y).ToList();
                checksumBuilder.Append($"T{sortedTiles.Count}");
                
                // Tile type counts for tamper detection
                var tileTypeCounts = sortedTiles.GroupBy(t => t.TileType).OrderBy(g => g.Key)
                    .Select(g => $"{g.Key.Substring(0, Math.Min(4, g.Key.Length))}{g.Count()}").ToList();
                checksumBuilder.Append($"[{string.Join(",", tileTypeCounts)}]");
                
                // Entity summary with container information
                var sortedEntities = grid.Entities.OrderBy(e => e.Position.X).ThenBy(e => e.Position.Y).ToList();
                checksumBuilder.Append($":E{sortedEntities.Count}");
                
                // Entity prototype counts - optimized grouping
                checksumBuilder.Append('[');
                var first = true;
                foreach (var group in sortedEntities.GroupBy(e => e.Prototype).OrderBy(g => g.Key))
                {
                    if (!first) checksumBuilder.Append(',');
                    checksumBuilder.Append(group.Key.Substring(0, Math.Min(6, group.Key.Length)));
                    checksumBuilder.Append(group.Count());
                    first = false;
                }
                checksumBuilder.Append(']');
                
                // Pre-calculate container counts and components in a single pass
                var containerCount = 0;
                var containedCount = 0;
                var totalComponents = 0;
                var componentTypes = new Dictionary<string, int>();
                
                foreach (var entity in sortedEntities)
                {
                    if (entity.IsContainer) containerCount++;
                    if (entity.IsContained) containedCount++;
                    totalComponents += entity.Components.Count;
                    
                    foreach (var component in entity.Components)
                    {
                        componentTypes[component.Type] = componentTypes.GetValueOrDefault(component.Type, 0) + 1;
                    }
                }
                
                checksumBuilder.Append($":C{containerCount}x{containedCount}");
                checksumBuilder.Append($":CM{totalComponents}[");
                
                var componentGroups = componentTypes.OrderBy(kv => kv.Key).Take(10);
                first = true;
                foreach (var group in componentGroups)
                {
                    if (!first) checksumBuilder.Append(',');
                    checksumBuilder.Append(group.Key.Substring(0, Math.Min(3, group.Key.Length)));
                    checksumBuilder.Append(group.Value);
                    first = false;
                }
                checksumBuilder.Append(']');
                
                // Position checksum for tamper detection (including container relationships)
                var tileCoordSum = sortedTiles.Sum(t => t.X * 100 + t.Y);
                var entityPosSum = (int)sortedEntities.Sum(e => e.Position.X * 100 + e.Position.Y * 100);
                // Use deterministic string hash instead of GetHashCode for container relationships
                var containerRelationSum = sortedEntities
                    .Where(e => e.IsContained && !string.IsNullOrEmpty(e.ParentContainerEntity))
                    .Sum(e => ComputeStringHash(e.ParentContainerEntity!) % 10000);
                checksumBuilder.Append($":P{tileCoordSum + entityPosSum + containerRelationSum}");
                
                checksumBuilder.Append(";");
            }
            
            var checksum = checksumBuilder.ToString().TrimEnd(';');
            _sawmill.Info($"Enhanced container-aware tamper-detection checksum: {checksum} (length: {checksum.Length})");
            
            return checksum;
        }
        
        private string GenerateServerBoundChecksum(ShipGridData data)
        {
            var baseChecksum = GenerateChecksum(data.Grids);
            var serverHardwareId = _serverIdentity.GetServerHardwareId();
            
            // Create server binding hash (use 8 chars for shorter length)
            var serverBinding = ComputeSha256Hash($"{serverHardwareId}:{baseChecksum}");
            var serverBindingShort = serverBinding.Substring(0, 8);
            
            var serverBoundChecksum = $"S:{serverBindingShort}:{baseChecksum}";
            
            _sawmill.Info($"Generated server-bound checksum with binding: {serverBindingShort}");
            return serverBoundChecksum;
        }
        
        private bool ValidateServerBoundChecksum(string storedChecksum, ShipGridData data)
        {
            var parts = storedChecksum.Split(':', 3);
            if (parts.Length < 3 || parts[0] != "S")
            {
                _sawmill.Error("Invalid server-bound checksum format");
                return false;
            }
            
            var storedServerBinding = parts[1];
            var storedBaseChecksum = parts[2];
            
            // Calculate actual base checksum
            var actualBaseChecksum = GenerateChecksum(data.Grids);
            
            // Verify base checksum first
            if (!string.Equals(storedBaseChecksum, actualBaseChecksum, StringComparison.OrdinalIgnoreCase))
            {
                _sawmill.Error("SECURITY VIOLATION: Base checksum validation failed - ship data tampered");
                _sawmill.Error($"Expected: {storedBaseChecksum}");
                _sawmill.Error($"Actual: {actualBaseChecksum}");
                return false;
            }
            
            // Verify server binding
            var currentServerHardwareId = _serverIdentity.GetServerHardwareId();
            var expectedBinding = ComputeSha256Hash($"{currentServerHardwareId}:{storedBaseChecksum}");
            
            if (!expectedBinding.StartsWith(storedServerBinding))
            {
                _sawmill.Error("SECURITY: Server binding validation failed - ship from different server rejected");
                _sawmill.Info($"Security feature: Prevents loading dev ships on production servers");
                return false;
            }
            
            _sawmill.Info("Server-bound checksum validation passed successfully");
            return true;
        }
        
        private static string ComputeSha256Hash(string input)
        {
            using var sha256 = SHA256.Create();
            var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
        
        private static int ComputeStringHash(string input)
        {
            // Deterministic string hash using simple FNV-1a algorithm
            uint hash = 2166136261u;
            foreach (byte b in Encoding.UTF8.GetBytes(input))
            {
                hash ^= b;
                hash *= 16777619u;
            }
            return (int)(hash & 0x7FFFFFFF); // Ensure positive
        }

        public string GetConvertedLegacyShipYaml(ShipGridData shipData, string playerName, string originalYamlString)
        {
            try
            {
                _sawmill.Info($"Generating converted YAML for legacy ship file for player {playerName}");
                
                // Serialize the updated ship data with new checksum
                var convertedYamlString = SerializeShipGridDataToYaml(shipData);
                
                var originalChecksum = ExtractOriginalChecksum(originalYamlString);
                _sawmill.Info($"Ship '{shipData.Metadata.ShipName}' converted from legacy SHA checksum '{originalChecksum}' to secure format '{shipData.Metadata.Checksum}'");
                
                return convertedYamlString;
            }
            catch (Exception ex)
            {
                _sawmill.Error($"Failed to generate converted ship YAML for player {playerName}: {ex.Message}");
                return string.Empty;
            }
        }
        
        private string ExtractOriginalChecksum(string yamlString)
        {
            // Simple regex to extract the original checksum from YAML
            var lines = yamlString.Split('\n');
            foreach (var line in lines)
            {
                if (line.Trim().StartsWith("checksum:"))
                {
                    return line.Split(':')[1].Trim().Trim('"');
                }
            }
            return "unknown";
        }

        private List<ComponentData> SerializeEntityComponents(EntityUid entityUid)
        {
            var componentDataList = new List<ComponentData>();
            
            try
            {
                // Get all components on the entity
                var metaData = _entityManager.GetComponent<MetaDataComponent>(entityUid);
                foreach (var component in _entityManager.GetComponents(entityUid))
                {
                    // Skip certain components that shouldn't be serialized
                    var componentType = component.GetType();
                    if (ShouldSkipComponent(componentType))
                        continue;

                    try
                    {
                        var componentData = SerializeComponent(component);
                        if (componentData != null)
                            componentDataList.Add(componentData);
                    }
                    catch (Exception ex)
                    {
                        _sawmill.Warning($"Failed to serialize component {componentType.Name} on entity {entityUid}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _sawmill.Error($"Failed to serialize components for entity {entityUid}: {ex.Message}");
            }

            return componentDataList;
        }

        private ComponentData? SerializeComponent(IComponent component)
        {
            try
            {
                var componentType = component.GetType();
                var typeName = componentType.Name;

                // Filter out problematic component types that shouldn't be serialized
                if (IsProblematicComponent(componentType))
                {
                    // Skipping problematic component
                    return null;
                }

                // Special handling for solution components to ensure chemical preservation
                if (component is SolutionContainerManagerComponent solutionManager)
                {
                    return SerializeSolutionComponent(solutionManager);
                }

                // Skip paper components - causes loading lag
                if (component is Content.Shared.Paper.PaperComponent)
                {
                    return null;
                }

                // Use RobustToolbox's serialization system to serialize the component
                var node = _serializationManager.WriteValue(componentType, component, notNullableOverride: true);
                var yamlData = _serializer.Serialize(node);

                var componentData = new ComponentData
                {
                    Type = typeName,
                    YamlData = yamlData,
                    NetId = 0 // NetID not available in this context
                };

                // Log important component preservation
                if (IsImportantComponent(componentType))
                {
                    // Preserved important component
                }

                return componentData;
            }
            catch (Exception ex)
            {
                var componentType = component.GetType();
                
                // Don't log warnings for expected problematic components
                if (IsProblematicComponent(componentType))
                {
                    // Reduce noise - only log at debug level for expected failures
                    // Expected serialization failure
                }
                else if (IsImportantComponent(componentType))
                {
                    // Only warn for important components that fail
                    _sawmill.Warning($"Failed to serialize important component {componentType.Name}: {ex.Message}");
                }
                else
                {
                    // Less important components - just debug
                    // Failed to serialize component
                }
                return null;
            }
        }

        private bool IsProblematicComponent(Type componentType)
        {
            var typeName = componentType.Name;
            
            // Network/client-side components that cause serialization issues
            if (typeName.Contains("Network") || typeName.Contains("Client") || typeName.Contains("Ui"))
                return true;
                
            // Timing/temporary components that shouldn't be preserved
            if (typeName.Contains("Timer") || typeName.Contains("Temporary") || typeName.Contains("Transient"))
                return true;
                
            // Event/notification components
            if (typeName.Contains("Event") || typeName.Contains("Alert") || typeName.Contains("Notification"))
                return true;
                
            // Runtime/generated components
            if (typeName.Contains("Runtime") || typeName.Contains("Generated") || typeName.Contains("Dynamic"))
                return true;

            // Known problematic component types from logs
            var problematicTypes = new[]
            {
                "ActionsComponent", "ItemSlotsComponent", "InventoryComponent", "SlotManagerComponent",
                "HandsComponent", "BodyComponent", "PlayerInputMoverComponent", "GhostComponent",
                "MindComponent", "MovementSpeedModifierComponent", "InputMoverComponent",
                "ActorComponent", "DamageableComponent", "ThermalRegulatorComponent", "FlammableComponent",
                "DamageTriggerComponent", "AtmosDeviceComponent", "NodeContainerComponent",
                "DeviceNetworkComponent", "StatusEffectsComponent", "BloodstreamComponent",
                "FixtureComponent", "InventoryComponent", "RadioComponent", "InteractionOutlineComponent"
            };

            return problematicTypes.Contains(typeName);
        }

        private ComponentData? SerializeSolutionComponent(SolutionContainerManagerComponent solutionManager)
        {
            try
            {
                // Create a simplified representation of the solution data for better preservation
                var solutionData = new Dictionary<string, object>();
                
                foreach (var (solutionName, solution) in solutionManager.Solutions ?? new Dictionary<string, Solution>())
                {
                    var solutionInfo = new Dictionary<string, object>
                    {
                        ["Volume"] = solution.Volume,
                        ["MaxVolume"] = solution.MaxVolume,
                        ["Temperature"] = solution.Temperature,
                        ["Reagents"] = solution.Contents?.ToDictionary(
                            reagent => reagent.Reagent.Prototype, 
                            reagent => (object)reagent.Quantity
                        ) ?? new Dictionary<string, object>()
                    };
                    solutionData[solutionName] = solutionInfo;
                }

                var componentData = new ComponentData
                {
                    Type = "SolutionContainerManagerComponent",
                    Properties = solutionData,
                    NetId = 0 // NetID not available in this context
                };

                // Preserved solution component
                return componentData;
            }
            catch (Exception ex)
            {
                _sawmill.Warning($"Failed to serialize solution component: {ex.Message}");
                return null;
            }
        }


        private static readonly Dictionary<Type, bool> ComponentSkipCache = new();
        
        private bool ShouldSkipComponent(Type componentType)
        {
            if (ComponentSkipCache.TryGetValue(componentType, out var cached))
                return cached;
                
            var typeName = componentType.Name;
            
            var shouldSkip = false;
            
            // Skip transform components (position handled separately)
            if (typeName == "TransformComponent")
                shouldSkip = true;
                
            // Skip metadata components (handled separately)
            else if (typeName == "MetaDataComponent")
                shouldSkip = true;
                
            // Skip physics components (usually regenerated)
            else if (typeName.Contains("Physics"))
                shouldSkip = true;
                
            // Skip appearance/visual components (usually regenerated)
            else if (typeName.Contains("Appearance") || typeName.Contains("Sprite"))
                shouldSkip = true;

            // Skip network/client-side components
            else if (typeName.Contains("Eye") || typeName.Contains("Input") || typeName.Contains("UserInterface"))
                shouldSkip = true;
                
            ComponentSkipCache[componentType] = shouldSkip;
            return shouldSkip;
        }

        private bool IsImportantComponent(Type componentType)
        {
            var typeName = componentType.Name;
            
            // Chemical/solution components - high priority for preservation
            if (typeName.Contains("Solution") || typeName.Contains("Chemical"))
                return true;
                
            // Book and text components (paper no longer preserved)
            if (typeName.Contains("Book") || typeName.Contains("SignComponent"))
                return true;
                
            // Storage and container components
            if (typeName.Contains("Storage") || typeName.Contains("Container"))
                return true;
                
            // Seed and plant components (GMO preservation)
            if (typeName.Contains("Seed") || typeName.Contains("Plant") || typeName.Contains("Produce"))
                return true;
                
            // Stack and quantity components
            if (typeName.Contains("Stack") || typeName.Contains("Quantity"))
                return true;
                
            // Power and battery components
            if (typeName.Contains("Battery") || typeName.Contains("PowerCell"))
                return true;
                
            // Generator and fuel components (PACMAN, AME, etc.)
            if (typeName.Contains("Generator") || typeName.Contains("Fuel") || typeName.Contains("AME") || 
                typeName.Contains("PACMAN") || typeName.Contains("Reactor") || typeName.Contains("Engine"))
                return true;
                
            // Power/energy storage and distribution
            if (typeName.Contains("Power") || typeName.Contains("Energy") || typeName.Contains("Charge"))
                return true;
                
            // Machine state components
            if (typeName.Contains("Machine") || typeName.Contains("Processor") || typeName.Contains("Fabricator"))
                return true;

            // Atmospheric components (for atmospheric engines, scrubbers, etc.)
            if (typeName.Contains("Atmospheric") || typeName.Contains("Gas") || typeName.Contains("Atmos"))
                return true;

            // IFF and ship identification components
            if (typeName.Contains("IFF") || typeName.Contains("Identification") || typeName.Contains("Identity"))
                return true;

            return false;
        }

        private void RestoreEntityComponents(EntityUid entityUid, List<ComponentData> componentDataList)
        {
            var restored = 0;
            var failed = 0;
            var skipped = 0;

            foreach (var componentData in componentDataList)
            {
                try
                {
                    var componentTypes = _entityManager.ComponentFactory.GetAllRefTypes()
                        .Select(idx => _entityManager.ComponentFactory.GetRegistration(idx).Type)
                        .Where(t => t.Name == componentData.Type || t.Name.EndsWith($".{componentData.Type}"))
                        .FirstOrDefault();

                    if (componentTypes != null && IsProblematicComponent(componentTypes))
                    {
                        skipped++;
                        continue;
                    }

                    RestoreComponent(entityUid, componentData);
                    restored++;
                }
                catch (Exception ex)
                {
                    failed++;
                    // Reduce noise - only warn for important components
                    var componentTypes = _entityManager.ComponentFactory.GetAllRefTypes()
                        .Select(idx => _entityManager.ComponentFactory.GetRegistration(idx).Type)
                        .Where(t => t.Name == componentData.Type || t.Name.EndsWith($".{componentData.Type}"))
                        .FirstOrDefault();
                        
                    if (componentTypes != null && (IsImportantComponent(componentTypes) && !IsProblematicComponent(componentTypes)))
                    {
                        _sawmill.Warning($"Failed to restore important component {componentData.Type} on entity {entityUid}: {ex.Message}");
                    }
                    else
                    {
                        // Failed to restore component
                    }
                }
            }

            if (restored > 0 || failed > 0 || skipped > 0)
            {
                // Entity component restoration completed
                if (failed > 10) // Only warn if excessive failures
                {
                    _sawmill.Warning($"Entity {entityUid} had {failed} component restoration failures - entity may be incomplete");
                }
            }
        }

        private void RestoreComponent(EntityUid entityUid, ComponentData componentData)
        {
            try
            {
                // Special handling for solution components
                if (componentData.Type == "SolutionContainerManagerComponent")
                {
                    if (componentData.Properties.Any())
                    {
                        RestoreSolutionComponent(entityUid, componentData);
                    }
                    else
                    {
                        // Solution component has no data - skipping
                    }
                    return;
                }

                // Skip paper components - no longer preserved to reduce loading lag
                if (componentData.Type == "PaperComponent")
                {
                    return;
                }

                if (string.IsNullOrEmpty(componentData.YamlData))
                    return;

                // Filter out components that shouldn't be restored
                var componentTypes = _entityManager.ComponentFactory.GetAllRefTypes()
                    .Select(idx => _entityManager.ComponentFactory.GetRegistration(idx).Type)
                    .Where(t => t.Name == componentData.Type || t.Name.EndsWith($".{componentData.Type}"))
                    .ToList();

                if (!componentTypes.Any())
                {
                    // Component type not found - version mismatch
                    return;
                }

                var componentType = componentTypes.First();
                
                // Skip problematic components during restoration too
                if (IsProblematicComponent(componentType))
                {
                    // Skipping problematic component
                    return;
                }

                // Deserialize the component data
                var node = _deserializer.Deserialize<DataNode>(componentData.YamlData);
                
                // Ensure the entity has this component
                if (!_entityManager.HasComponent(entityUid, componentType))
                {
                    try
                    {
                        var newComponent = (Component)Activator.CreateInstance(componentType)!;
                        _entityManager.AddComponent(entityUid, newComponent);
                    }
                    catch (Exception ex)
                    {
                        // Failed to create component - continuing
                        return;
                    }
                }

                // Get the existing component and populate it with saved data
                var existingComponent = _entityManager.GetComponent(entityUid, componentType);
                
                try
                {
                    object? temp = existingComponent;
                    _serializationManager.CopyTo(node, ref temp);
                    // Component restored
                }
                catch (Exception ex)
                {
                    // Only warn for important components
                    if (IsImportantComponent(componentType) && !IsProblematicComponent(componentType))
                    {
                        _sawmill.Warning($"Failed to populate important component {componentData.Type} data: {ex.Message}");
                    }
                    else
                    {
                        // Failed to populate component data
                    }
                    // Continue execution - partial restoration is better than none
                }
            }
            catch (Exception ex)
            {
                // Failed to restore component - continuing
                // Don't throw - continue with other components
            }
        }

        private void RestoreSolutionComponent(EntityUid entityUid, ComponentData componentData)
        {
            try
            {
                if (!_entityManager.TryGetComponent<SolutionContainerManagerComponent>(entityUid, out var solutionManager))
                {
                    _sawmill.Warning($"Entity {entityUid} does not have SolutionContainerManagerComponent to restore");
                    return;
                }

                var restoredSolutions = 0;
                foreach (var (solutionName, solutionDataObj) in componentData.Properties)
                {
                    if (solutionDataObj is not Dictionary<string, object> solutionInfo)
                        continue;

                    try
                    {
                        // Get or create the solution
                        if (solutionManager.Solutions?.TryGetValue(solutionName, out var solution) != true || solution == null)
                        {
                            _sawmill.Warning($"Solution '{solutionName}' not found on entity {entityUid}");
                            continue;
                        }

                        // Clear existing contents
                        solution.RemoveAllSolution();

                        // Restore solution properties
                        if (solutionInfo.TryGetValue("Temperature", out var tempObj) && tempObj is double temperature)
                        {
                            solution.Temperature = (float)temperature;
                        }

                        // Restore reagents
                        if (solutionInfo.TryGetValue("Reagents", out var reagentsObj) && 
                            reagentsObj is Dictionary<string, object> reagents)
                        {
                            foreach (var (reagentId, quantityObj) in reagents)
                            {
                                if (quantityObj is double quantity && quantity > 0)
                                {
                                    // Add the reagent back to the solution
                                    solution?.AddReagent(reagentId, (float)quantity);
                                }
                            }
                        }

                        restoredSolutions++;
                        var reagentCount = solutionInfo.ContainsKey("Reagents") && 
                                          solutionInfo["Reagents"] is Dictionary<string, object> reagentDict ? 
                                          reagentDict.Count : 0;
                        _sawmill.Debug($"Restored solution '{solutionName}' with {reagentCount} reagents");
                    }
                    catch (Exception ex)
                    {
                        _sawmill.Warning($"Failed to restore solution '{solutionName}': {ex.Message}");
                    }
                }

                if (restoredSolutions > 0)
                {
                    // Restored chemical solutions
                }
            }
            catch (Exception ex)
            {
                _sawmill.Error($"Failed to restore solution component on entity {entityUid}: {ex.Message}");
            }
        }


        private (string? parentContainerEntity, string? containerSlot) GetContainerInfo(EntityUid entityUid)
        {
            try
            {
                if (!_entityManager.TryGetComponent<TransformComponent>(entityUid, out var transform))
                    return (null, null);

                var parent = transform.ParentUid;
                if (!parent.IsValid())
                    return (null, null);

                // Check if the parent has a container manager
                if (!_entityManager.TryGetComponent<ContainerManagerComponent>(parent, out var containerManager))
                    return (null, null);

                // Find which container this entity is in
                foreach (var container in containerManager.Containers.Values)
                {
                    if (container.Contains(entityUid))
                    {
                        return (parent.ToString(), container.ID);
                    }
                }

                return (null, null);
            }
            catch (Exception ex)
            {
                _sawmill.Warning($"Failed to get container info for entity {entityUid}: {ex.Message}");
                return (null, null);
            }
        }

        private bool HasContainers(EntityUid entityUid)
        {
            return _entityManager.HasComponent<ContainerManagerComponent>(entityUid);
        }

        private EntityData? SerializeEntity(EntityUid uid, TransformComponent transform, string prototype, EntityUid gridId)
        {
            try
            {
                // Get container relationship information
                var (parentContainer, containerSlot) = GetContainerInfo(uid);
                var isContained = parentContainer != null;
                var isContainer = HasContainers(uid);

                // Serialize component states
                var components = SerializeEntityComponents(uid);

                var entityData = new EntityData
                {
                    EntityId = uid.ToString(),
                    Prototype = prototype,
                    Position = new Vector2((float)Math.Round(transform.LocalPosition.X, 3), (float)Math.Round(transform.LocalPosition.Y, 3)),
                    Rotation = (float)Math.Round(transform.LocalRotation.Theta, 3),
                    Components = components,
                    ParentContainerEntity = parentContainer,
                    ContainerSlot = containerSlot,
                    IsContainer = isContainer,
                    IsContained = isContained
                };

                return entityData;
            }
            catch (Exception ex)
            {
                _sawmill.Error($"Failed to serialize entity {uid}: {ex.Message}");
                return null;
            }
        }

        private void SerializeContainedEntities(EntityUid gridId, GridData gridData, HashSet<EntityUid> alreadySerialized)
        {
            // Find all entities that might be contained within grid entities but not directly on the grid
            var containersToCheck = new Queue<EntityUid>();
            
            // Start with all container entities on the grid
            foreach (var entityData in gridData.Entities.Where(e => e.IsContainer))
            {
                if (EntityUid.TryParse(entityData.EntityId, out var containerUid))
                {
                    containersToCheck.Enqueue(containerUid);
                }
            }

            // Process containers recursively
            while (containersToCheck.Count > 0)
            {
                var containerUid = containersToCheck.Dequeue();
                
                if (!_entityManager.TryGetComponent<ContainerManagerComponent>(containerUid, out var containerManager))
                    continue;

                foreach (var container in containerManager.Containers.Values)
                {
                    foreach (var containedEntity in container.ContainedEntities)
                    {
                        if (alreadySerialized.Contains(containedEntity))
                            continue;

                        try
                        {
                            var transform = _entityManager.GetComponent<TransformComponent>(containedEntity);
                            var meta = _entityManager.GetComponentOrNull<MetaDataComponent>(containedEntity);
                            var proto = meta?.EntityPrototype?.ID ?? string.Empty;

                            var entityData = SerializeEntity(containedEntity, transform, proto, gridId);
                            if (entityData != null)
                            {
                                gridData.Entities.Add(entityData);
                                alreadySerialized.Add(containedEntity);
                                _sawmill.Debug($"Serialized contained entity {containedEntity} ({proto}) in container {containerUid}");

                                // If this contained entity is also a container, check its contents
                                if (entityData.IsContainer)
                                {
                                    containersToCheck.Enqueue(containedEntity);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _sawmill.Warning($"Failed to serialize contained entity {containedEntity}: {ex.Message}");
                        }
                    }
                }
            }
        }

        private EntityUid? SpawnEntityWithComponents(EntityData entityData, EntityCoordinates coordinates)
        {
            try
            {
                // Spawn the basic entity
                var newEntity = _entityManager.SpawnEntity(entityData.Prototype, coordinates);
                
                // Apply rotation if it exists
                if (Math.Abs(entityData.Rotation) > 0.001f)
                {
                    var transform = _entityManager.GetComponent<TransformComponent>(newEntity);
                    transform.LocalRotation = new Angle(entityData.Rotation);
                }

                // Clear any default container contents to prevent duplicates
                // This ensures saved containers don't get refilled with prototype defaults
                if (entityData.IsContainer && _entityManager.TryGetComponent<ContainerManagerComponent>(newEntity, out var containerManager))
                {
                    foreach (var container in containerManager.Containers.Values)
                    {
                        // Clear default spawned items - we'll restore saved contents later
                        var defaultItems = container.ContainedEntities.ToList();
                        foreach (var defaultItem in defaultItems)
                        {
                            _containerSystem.Remove(defaultItem, container);
                            _entityManager.DeleteEntity(defaultItem);
                        }
                    }
                    //_sawmill.Debug($"Cleared default contents from container {newEntity} - will restore saved contents");
                }

                // Restore component states
                if (entityData.Components.Any())
                {
                    RestoreEntityComponents(newEntity, entityData.Components);
                }

                return newEntity;
            }
            catch (Exception ex)
            {
                _sawmill.Error($"Failed to spawn entity with components {entityData.Prototype}: {ex.Message}");
                return null;
            }
        }

        private bool InsertIntoContainer(EntityUid entityToInsert, EntityUid containerEntity, string containerSlot)
        {
            try
            {
                if (!_entityManager.TryGetComponent<ContainerManagerComponent>(containerEntity, out var containerManager))
                {
                    _sawmill.Warning($"Container entity {containerEntity} does not have ContainerManagerComponent");
                    return false;
                }

                if (!containerManager.TryGetContainer(containerSlot, out var container))
                {
                    _sawmill.Warning($"Container slot '{containerSlot}' not found on entity {containerEntity}");
                    return false;
                }

                // Use the container system to properly insert the entity
                if (_containerSystem.Insert(entityToInsert, container))
                {
                    //_sawmill.Debug($"Successfully inserted entity {entityToInsert} into container {containerEntity} slot '{containerSlot}'");
                    return true;
                }
                else
                {
                    //_sawmill.Warning($"Failed to insert entity {entityToInsert} into container {containerEntity} slot '{containerSlot}' - container may be full");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _sawmill.Error($"Error inserting entity {entityToInsert} into container {containerEntity}: {ex.Message}");
                return false;
            }
        }

        private List<(Vector2i coords, Tile tile)> SortTilesForConnectivity(List<(Vector2i coords, Tile tile)> tilesToPlace)
        {
            if (!tilesToPlace.Any()) return tilesToPlace;

            var result = new List<(Vector2i coords, Tile tile)>();
            var remaining = new HashSet<Vector2i>(tilesToPlace.Select(t => t.coords));
            var tileDict = tilesToPlace.ToDictionary(t => t.coords, t => t.tile);

            // Start with any tile (preferably near center)
            var startCoord = tilesToPlace.OrderBy(t => t.coords.X * t.coords.X + t.coords.Y * t.coords.Y).First().coords;
            var queue = new Queue<Vector2i>();
            queue.Enqueue(startCoord);
            remaining.Remove(startCoord);

            // Flood-fill from start position
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                result.Add((current, tileDict[current]));

                // Check adjacent positions (4-directional)
                var adjacent = new[]
                {
                    new Vector2i(current.X + 1, current.Y),
                    new Vector2i(current.X - 1, current.Y),
                    new Vector2i(current.X, current.Y + 1),
                    new Vector2i(current.X, current.Y - 1)
                };

                foreach (var adj in adjacent)
                {
                    if (remaining.Contains(adj))
                    {
                        queue.Enqueue(adj);
                        remaining.Remove(adj);
                    }
                }
            }

            // Add any remaining disconnected tiles (these will create separate grids)
            foreach (var remainingCoord in remaining)
            {
                result.Add((remainingCoord, tileDict[remainingCoord]));
                _sawmill.Warning($"GRID SPLIT: Disconnected tile at {remainingCoord} will create separate grid");
            }

            return result;
        }

        private Vector2 FindNearbyPosition(EntityUid gridEntity, Vector2 originalPosition)
        {
            // Try to find a nearby unoccupied position
            var searchPositions = new[]
            {
                originalPosition,
                originalPosition + new Vector2(1, 0),
                originalPosition + new Vector2(-1, 0),
                originalPosition + new Vector2(0, 1),
                originalPosition + new Vector2(0, -1),
                originalPosition + new Vector2(1, 1),
                originalPosition + new Vector2(-1, -1),
                originalPosition + new Vector2(1, -1),
                originalPosition + new Vector2(-1, 1)
            };

            foreach (var testPos in searchPositions)
            {
                var coords = new EntityCoordinates(gridEntity, testPos);
                
                // Check if position is occupied (basic check)
                var lookup = _entityManager.System<EntityLookupSystem>();
                var mapCoords = coords.ToMap(_entityManager, _transformSystem);
                var entitiesAtPos = lookup.GetEntitiesIntersecting(mapCoords.MapId, new Box2(testPos - Vector2.One * 0.1f, testPos + Vector2.One * 0.1f));
                if (!entitiesAtPos.Any())
                {
                    return testPos;
                }
            }

            // If all nearby positions are occupied, just use the original position
            return originalPosition;
        }

        private void ValidateContainerRelationships(GridData gridData)
        {
            try
            {
                var containerEntities = gridData.Entities.Where(e => e.IsContainer).ToList();
                var containedEntities = gridData.Entities.Where(e => e.IsContained).ToList();
                var entityIds = gridData.Entities.Select(e => e.EntityId).ToHashSet();

                // Validating container relationships

                var orphanedEntities = 0;
                var invalidContainers = 0;

                foreach (var containedEntity in containedEntities)
                {
                    // Check if parent container exists
                    if (string.IsNullOrEmpty(containedEntity.ParentContainerEntity))
                    {
                        _sawmill.Warning($"Contained entity {containedEntity.EntityId} has no parent container specified");
                        orphanedEntities++;
                        continue;
                    }

                    if (!entityIds.Contains(containedEntity.ParentContainerEntity))
                    {
                        _sawmill.Warning($"Contained entity {containedEntity.EntityId} references non-existent parent container {containedEntity.ParentContainerEntity}");
                        orphanedEntities++;
                        continue;
                    }

                    // Check if parent is actually marked as a container
                    var parentEntity = gridData.Entities.FirstOrDefault(e => e.EntityId == containedEntity.ParentContainerEntity);
                    if (parentEntity != null && !parentEntity.IsContainer)
                    {
                        _sawmill.Warning($"Entity {containedEntity.EntityId} is contained in {containedEntity.ParentContainerEntity}, but parent is not marked as container");
                        invalidContainers++;
                    }

                    // Check if container slot is specified
                    if (string.IsNullOrEmpty(containedEntity.ContainerSlot))
                    {
                        _sawmill.Warning($"Contained entity {containedEntity.EntityId} has no container slot specified");
                    }
                }

                if (orphanedEntities > 0 || invalidContainers > 0)
                {
                    _sawmill.Warning($"Container validation found issues: {orphanedEntities} orphaned entities, {invalidContainers} invalid containers");
                }
                else
                {
                    // Container relationship validation passed
                }
            }
            catch (Exception ex)
            {
                _sawmill.Error($"Failed to validate container relationships: {ex.Message}");
            }
        }

        private void ReconstructEntitiesLegacyMode(GridData gridData, MapGridComponent newGrid, Dictionary<string, EntityUid> entityIdMapping)
        {
            _sawmill.Info("Using legacy reconstruction mode for backward compatibility");
            
            var spawnedCount = 0;
            var failedCount = 0;
            
            foreach (var entityData in gridData.Entities)
            {
                if (string.IsNullOrEmpty(entityData.Prototype))
                {
                    _sawmill.Debug($"Skipping entity with empty prototype at {entityData.Position}");
                    continue;
                }

                try
                {
                    var coordinates = new EntityCoordinates(newGrid.Owner, entityData.Position);
                    var newEntity = SpawnEntityWithComponents(entityData, coordinates);
                    
                    if (newEntity != null)
                    {
                        entityIdMapping[entityData.EntityId] = newEntity.Value;
                        spawnedCount++;
                        _sawmill.Debug($"Legacy: Spawned entity {newEntity} ({entityData.Prototype}) at {entityData.Position}");
                    }
                    else
                    {
                        failedCount++;
                    }
                }
                catch (Exception ex)
                {
                    _sawmill.Error($"Legacy: Failed to spawn entity {entityData.Prototype} at {entityData.Position}: {ex.Message}");
                    failedCount++;
                }
            }
            
            // Legacy reconstruction complete
        }

    }
}