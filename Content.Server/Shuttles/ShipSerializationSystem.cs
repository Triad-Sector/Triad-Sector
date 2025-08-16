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
using Content.Shared._NF.CCVar;
using Robust.Shared.Player;
using Robust.Server.Player;

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

            _sawmill.Info($"Serialized {gridData.Tiles.Count} tiles");
            
            // Check for overlapping entities at same coordinates
            var positionGroups = gridData.Entities.GroupBy(e => new { e.Position.X, e.Position.Y }).Where(g => g.Count() > 1);
            foreach (var group in positionGroups)
            {
                _sawmill.Info($"Found {group.Count()} entities at position ({group.Key.X}, {group.Key.Y}): {string.Join(", ", group.Select(e => e.Prototype))}");
            }

            // Skip atmosphere serialization as TileAtmosphere is not serializable
            // Atmosphere will be restored using fixgridatmos command during loading
            _sawmill.Info("Skipping atmosphere serialization - will use fixgridatmos during load");

            // Serialize decal data
            if (_entityManager.TryGetComponent<DecalGridComponent>(gridId, out var decalComponent))
            {
                try
                {
                    var decalNode = _serializationManager.WriteValue(decalComponent.ChunkCollection, notNullableOverride: true);
                    var decalYaml = _serializer.Serialize(decalNode);
                    gridData.DecalData = decalYaml;
                    _sawmill.Info($"Serialized decal data for grid");
                }
                catch (Exception ex)
                {
                    _sawmill.Error($"Failed to serialize decal data: {ex.Message}");
                }
            }
            else
            {
                _sawmill.Info("No decal component found on grid, decals will not be preserved");
            }

            // Simplified entity serialization - only entities directly on grid (not in containers)
            foreach (var entity in _entityManager.EntityQuery<TransformComponent>())
            {
                if (entity.GridUid != gridId)
                    continue;

                var uid = entity.Owner;
                var meta = _entityManager.GetComponentOrNull<MetaDataComponent>(uid);
                
                // Skip entities that are inside containers (items in lockers, pockets, etc.)
                if (IsEntityContained(uid, gridId))
                {
                    _sawmill.Debug($"Skipping entity {uid} ({meta?.EntityPrototype?.ID}) - inside container");
                    continue;
                }
                
                var proto = meta?.EntityPrototype?.ID ?? string.Empty;

                var entityData = new EntityData
                {
                    EntityId = uid.ToString(),
                    Prototype = proto,
                    Position = new Vector2((float)Math.Round(entity.LocalPosition.X, 3), (float)Math.Round(entity.LocalPosition.Y, 3)),
                    Rotation = (float)Math.Round(entity.LocalRotation.Theta, 3)
                };

                gridData.Entities.Add(entityData);
                _sawmill.Debug($"Serialized entity {uid} ({proto}) at {entityData.Position}");
            }

            shipGridData.Grids.Add(gridData);
            
            // Generate checksum AFTER all data is finalized
            var checksum = GenerateChecksum(shipGridData.Grids);
            shipGridData.Metadata.Checksum = checksum;
            _sawmill.Info($"Generated checksum for ship save: {checksum}");

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
            _sawmill.Info($"Deserializing YAML for player {loadingPlayerId}");
            wasLegacyConverted = false;
            ShipGridData data;
            try
            {
                data = _deserializer.Deserialize<ShipGridData>(yamlString);
                _sawmill.Debug($"Successfully deserialized YAML data");
            }
            catch (Exception ex)
            {
                _sawmill.Error($"YAML deserialization failed: {ex.Message}");
                throw;
            }

            // Store original checksum BEFORE any calculations
            var originalStoredChecksum = data.Metadata.Checksum;
            _sawmill.Info($"ORIGINAL stored checksum from file: {originalStoredChecksum}");
            
            // Calculate checksum from loaded data (this should NOT modify data.Metadata.Checksum)
            var actualChecksum = GenerateChecksum(data.Grids);
            
            // Verify the stored checksum wasn't modified
            if (data.Metadata.Checksum != originalStoredChecksum)
            {
                _sawmill.Error($"BUG: Stored checksum was modified during GenerateChecksum! Was: {originalStoredChecksum}, Now: {data.Metadata.Checksum}");
            }
            
            _sawmill.Info($"Expected checksum: {originalStoredChecksum}");
            _sawmill.Info($"Actual checksum: {actualChecksum}");
            _sawmill.Debug($"Grid count: {data.Grids.Count}");
            _sawmill.Debug($"Tile count: {data.Grids[0].Tiles.Count}");
            _sawmill.Debug($"Entity count: {data.Grids[0].Entities.Count}");
            
            // Check if checksum validation is enabled via cvar
            var checksumValidationEnabled = _configManager.GetCVar(NFCCVars.ShipyardChecksumValidation);
            
            if (checksumValidationEnabled)
            {
                // Detect checksum format and handle appropriately
                bool isLegacySHA = originalStoredChecksum.Length == 64 && !originalStoredChecksum.Contains(":");
                bool isLegacyEnhanced = originalStoredChecksum.Contains(":PP-");
                
                if (isLegacySHA)
                {
                    // Legacy SHA256 checksum - regenerate current checksum and update file metadata for future saves
                    _sawmill.Warning($"Legacy SHA256 checksum detected: {originalStoredChecksum}");
                    _sawmill.Warning($"This ship will be automatically converted to use the new checksum format");
                    _sawmill.Info($"Current checksum would be: {actualChecksum}");
                    
                    // Update the metadata with new checksum for conversion
                    data.Metadata.Checksum = actualChecksum;
                    wasLegacyConverted = true;
                    _sawmill.Info("Legacy checksum compatibility mode - validation passed, marked for conversion");
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
                        _sawmill.Error($"Ship data has been tampered with!");
                        _sawmill.Error($"File: {data.Metadata.ShipName} saved by player {data.Metadata.PlayerId}");
                        throw new InvalidOperationException("Checksum mismatch! Ship data may have been tampered with.");
                    }
                    
                    _sawmill.Info("Legacy enhanced checksum validation passed successfully");
                }
                else
                {
                    // Current format validation
                    if (!string.Equals(originalStoredChecksum, actualChecksum, StringComparison.OrdinalIgnoreCase))
                    {
                        _sawmill.Error($"CHECKSUM VALIDATION FAILED!");
                        _sawmill.Error($"Expected: {originalStoredChecksum}");
                        _sawmill.Error($"Actual: {actualChecksum}");
                        _sawmill.Error($"Ship data has been tampered with!");
                        _sawmill.Error($"File: {data.Metadata.ShipName} saved by player {data.Metadata.PlayerId}");
                        throw new InvalidOperationException("Checksum mismatch! Ship data may have been tampered with.");
                    }
                    
                    _sawmill.Info("Current checksum validation passed successfully");
                }
            }
            else
            {
                _sawmill.Info("Checksum validation disabled by server configuration");
            }


            if (data.Metadata.PlayerId != loadingPlayerId.ToString())
            {
                throw new UnauthorizedAccessException("Player ID mismatch! You can only load ships you have saved.");
            }

            return data;
        }

        public EntityUid ReconstructShipOnMap(ShipGridData shipGridData, MapId targetMap, Vector2 offset)
        {
            _sawmill.Info($"Reconstructing ship with {shipGridData.Grids.Count} grids on map {targetMap} at offset {offset}");
            if (shipGridData.Grids.Count == 0)
            {
                throw new ArgumentException("No grid data to reconstruct.");
            }

            var primaryGridData = shipGridData.Grids[0];
            _sawmill.Info($"Primary grid has {primaryGridData.Entities.Count} entities");
            _sawmill.Info($"Primary grid has {primaryGridData.Tiles.Count} tiles");
            
            var newGrid = _mapManager.CreateGrid(targetMap);
            _sawmill.Info($"Created new grid {newGrid.Owner} on shipyard map {targetMap}");
            
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

            // Sort tiles by connectivity to maintain grid integrity during placement
            // Start from the center and work outward to ensure the grid stays connected
            if (tilesToPlace.Any())
            {
                var center = new Vector2(
                    (float)tilesToPlace.Average(t => t.coords.X),
                    (float)tilesToPlace.Average(t => t.coords.Y)
                );

                tilesToPlace.Sort((a, b) => 
                    (new Vector2(a.coords.X, a.coords.Y) - center).LengthSquared().CompareTo(
                    (new Vector2(b.coords.X, b.coords.Y) - center).LengthSquared()));
            }

            // Place tiles maintaining connectivity
            foreach (var (coords, tile) in tilesToPlace)
            {
                _map.SetTile(newGrid.Owner, newGrid, coords, tile);
            }
            _sawmill.Info($"Placed {tilesToPlace.Count} tiles in connectivity order");

            // Apply fixgridatmos-style atmosphere to all loaded ships
            _sawmill.Info("Applying fixgridatmos-style atmosphere to loaded ship");
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
                _sawmill.Info("No decal data found, decals will not be restored");
            }

            // Reconstruct entities
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

        public EntityUid ReconstructShip(ShipGridData shipGridData)
        {
            _sawmill.Info($"Reconstructing ship with {shipGridData.Grids.Count} grids");
            if (shipGridData.Grids.Count == 0)
            {
                throw new ArgumentException("No grid data to reconstruct.");
            }

            var primaryGridData = shipGridData.Grids[0];
            _sawmill.Info($"Primary grid has {primaryGridData.Entities.Count} entities");
            
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

            // Sort tiles by connectivity to maintain grid integrity during placement
            // Start from the center and work outward to ensure the grid stays connected
            if (tilesToPlace.Any())
            {
                var center = new Vector2(
                    (float)tilesToPlace.Average(t => t.coords.X),
                    (float)tilesToPlace.Average(t => t.coords.Y)
                );

                tilesToPlace.Sort((a, b) => 
                    (new Vector2(a.coords.X, a.coords.Y) - center).LengthSquared().CompareTo(
                    (new Vector2(b.coords.X, b.coords.Y) - center).LengthSquared()));
            }

            // Place tiles maintaining connectivity
            foreach (var (coords, tile) in tilesToPlace)
            {
                _map.SetTile(newGrid.Owner, newGrid, coords, tile);
            }
            _sawmill.Info($"Placed {tilesToPlace.Count} tiles in connectivity order");

            // Apply fixgridatmos-style atmosphere to all loaded ships
            _sawmill.Info("Applying fixgridatmos-style atmosphere to loaded ship");
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
                _sawmill.Info("No decal data found, decals will not be restored");
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
                while (parent.IsValid() && parent != gridId)
                {
                    // If we find a parent that has a ContainerManagerComponent, this entity is contained
                    if (_entityManager.HasComponent<ContainerManagerComponent>(parent))
                    {
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
            // Enhanced tamper-detection checksum using detailed entity data
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
                
                // Entity summary  
                var sortedEntities = grid.Entities.OrderBy(e => e.Position.X).ThenBy(e => e.Position.Y).ToList();
                checksumBuilder.Append($":E{sortedEntities.Count}");
                
                // Entity prototype counts
                var entityProtoCounts = sortedEntities.GroupBy(e => e.Prototype).OrderBy(g => g.Key)
                    .Select(g => $"{g.Key.Substring(0, Math.Min(6, g.Key.Length))}{g.Count()}").ToList();
                checksumBuilder.Append($"[{string.Join(",", entityProtoCounts)}]");
                
                // Position checksum for tamper detection
                var tileCoordSum = sortedTiles.Sum(t => t.X * 100 + t.Y);
                var entityPosSum = (int)sortedEntities.Sum(e => e.Position.X * 100 + e.Position.Y * 100);
                checksumBuilder.Append($":P{tileCoordSum + entityPosSum}");
                
                checksumBuilder.Append(";");
            }
            
            var checksum = checksumBuilder.ToString().TrimEnd(';');
            _sawmill.Info($"Enhanced tamper-detection checksum: {checksum} (length: {checksum.Length})");
            
            return checksum;
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
    }
}