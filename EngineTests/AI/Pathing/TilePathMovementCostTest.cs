using C7Engine;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using Xunit;

namespace EngineTests.AI.Pathing;

// The road and railroad movement discounts only apply when both ends of a
// step carry the improvement, and an unroaded city tile is not a road end.
// See the movement specification, sections 3.2 (roads), 3.3 (railroads) and
// 3.7 (cities).
public sealed class TilePathMovementCostTest : MapBase {
	private const double Tolerance = 0.0001;
	private const double RoadCost = 1.0 / 3.0;

	public TilePathMovementCostTest() {
		// Adding a road invalidates the cached trade network, which needs game data.
		EngineStorage.InitializeGameDataForTests(new C7GameData.GameData(1234));
	}

	private static Player Walker() {
		return new Player() { isHuman = false };
	}

	[Fact]
	private void TestRoadOnDestinationOnlyCostsTerrain() {
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		var destination = AddNeighborsAndUpdateMap(startTile, MakeMountainTile(), TileDirection.NORTH);
		destination.overlays.Add(road);

		// The source tile carries no road, so the destination's terrain cost
		// applies instead of the road cost.
		Assert.Equal(3.0, TilePath.GetMovementCost(Walker(), startTile, TileDirection.NORTH, destination), Tolerance);
	}

	[Fact]
	private void TestRoadOnSourceOnlyCostsTerrain() {
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		startTile.overlays.Add(road);
		var destination = AddNeighborsAndUpdateMap(startTile, MakeMountainTile(), TileDirection.NORTH);

		// The destination tile carries no road, so the road discount does not
		// apply even though the unit starts on a road.
		Assert.Equal(3.0, TilePath.GetMovementCost(Walker(), startTile, TileDirection.NORTH, destination), Tolerance);
	}

	[Fact]
	private void TestRoadOnBothEndsCostsTheRoadCost() {
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		startTile.overlays.Add(road);
		var destination = AddNeighborsAndUpdateMap(startTile, MakeMountainTile(), TileDirection.NORTH);
		destination.overlays.Add(road);

		// Both ends are roaded, so the road cost applies even though the
		// destination is mountains.
		Assert.Equal(RoadCost, TilePath.GetMovementCost(Walker(), startTile, TileDirection.NORTH, destination), Tolerance);
	}

	[Fact]
	private void TestRailroadOnBothEndsIsFree() {
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		startTile.overlays.Add(railroad);
		var destination = AddNeighborsAndUpdateMap(startTile, MakeMountainTile(), TileDirection.NORTH);
		destination.overlays.Add(railroad);

		Assert.Equal(0.0, TilePath.GetMovementCost(Walker(), startTile, TileDirection.NORTH, destination), Tolerance);
	}

	[Fact]
	private void TestRailroadOnOneEndOnlyCostsTerrain() {
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		startTile.overlays.Add(railroad);
		var destination = AddNeighborsAndUpdateMap(startTile, MakeMountainTile(), TileDirection.NORTH);

		// Railroads also require both ends, so a single rail end gives the
		// destination's terrain cost rather than a free step.
		Assert.Equal(3.0, TilePath.GetMovementCost(Walker(), startTile, TileDirection.NORTH, destination), Tolerance);
	}

	[Fact]
	private void TestCityTileWithoutRoadIsNotARoadEnd() {
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		Player player = Walker();
		startTile.cityAtTile = new City(startTile, player, "City", ID.None(""));

		var destination = AddNeighborsAndUpdateMap(startTile, MakePlainsTile(), TileDirection.NORTH);
		destination.overlays.Add(road);

		// A city tile is not a road by itself. Since the city carries no road,
		// the roaded destination does not discount the step.
		Assert.Equal(1.0, TilePath.GetMovementCost(player, startTile, TileDirection.NORTH, destination), Tolerance);
	}

	[Fact]
	private void TestTwoCityTilesWithoutRoadsCostTerrain() {
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		Player player = Walker();
		startTile.cityAtTile = new City(startTile, player, "From", ID.None(""));

		var destination = AddNeighborsAndUpdateMap(startTile, MakePlainsTile(), TileDirection.NORTH);
		destination.cityAtTile = new City(destination, player, "To", ID.None(""));

		// Neither city tile carries a road, so the step is not free.
		Assert.Equal(1.0, TilePath.GetMovementCost(player, startTile, TileDirection.NORTH, destination), Tolerance);
	}
}
