using Content.IntegrationTests;
using Content.Server._NF.Shipyard.Systems;
using Content.Server.Maps;
using Content.Shared.Shuttles.Save;
using Content.Shared.VendingMachines;
using NUnit.Framework;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Utility;
using System.Threading.Tasks;
using System.Linq;

namespace Content.IntegrationTests.Tests._NF.Shipyard
{
    [TestFixture]
    public sealed class ShipyardGridSaveTest
    {
        [Test]
        public async Task TestPhaeronShipSave()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            var entityManager = server.ResolveDependency<IEntityManager>();
            var mapManager = server.ResolveDependency<IMapManager>();
            var mapLoader = server.ResolveDependency<IEntitySystemManager>().GetEntitySystem<MapLoaderSystem>();
            var shipyardGridSaveSystem = server.ResolveDependency<IEntitySystemManager>().GetEntitySystem<ShipyardGridSaveSystem>();

            // --- Setup ---
            var mapId = default(MapId);
            EntityUid? gridUid = null;

            await server.WaitPost(() =>
            {
                mapId = mapManager.CreateMap();

                var loaded = mapLoader.TryLoadGrid(
                    mapId,
                    new ResPath("/Maps/_Mono/Shuttles/phaeron.yml"),
                    out gridUid);

                Assert.That(loaded, Is.True, "Grid should load");
                Assert.That(gridUid, Is.Not.Null, "Grid UID should not be null");
            });

            await server.WaitIdleAsync(); // ensure full spawn/initialization

            // --- Act ---
            shipyardGridSaveSystem.CleanGridForSaving(gridUid!.Value);

            await server.WaitIdleAsync(); // ensure deletions propagate

            // --- Assert ---
            await server.WaitAssertion(() =>
            {
                var foundVendingMachine = false;

                var query = entityManager.EntityQueryEnumerator<VendingMachineComponent>();
                while (query.MoveNext(out var uid, out _))
                {
                    var xform = entityManager.GetComponent<TransformComponent>(uid);
                    if (xform.GridUid == gridUid)
                    {
                        foundVendingMachine = true;
                        break;
                    }
                }

                Assert.That(foundVendingMachine, Is.False,
                    "No vending machines should remain in cleaned grid");
            });

            // --- Cleanup ---
            await server.WaitPost(() =>
            {
                mapManager.DeleteMap(mapId);
            });

            await pair.CleanReturnAsync();
        }
    }
}
