using System.Linq;
using C7Engine;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using Xunit;

namespace EngineTests.GameData;

/// <summary>
/// Guards the barricade movement rule (11_movement.md §3.8): a barricade on a
/// destination tile owned by a civ that is neither the mover nor unowned, with
/// no active right of passage for the mover, consumes all of the unit's
/// remaining movement. A fortress has no movement effect.
/// </summary>
public sealed class BarricadeMovementCostTest : MapBase {
	private const double Tolerance = 0.0001;

	public BarricadeMovementCostTest() {
		// Adding a terrain improvement touches the cached trade network.
		EngineStorage.InitializeGameDataForTests(new C7GameData.GameData(1234));
	}

	private readonly TerrainImprovement barricade =
		new(Tile.TileOverlays.BARRICADE, TerrainImprovement.Layer.Holdings);

	private Player MakeDiplomaticPlayer(string id) {
		Player player = MakePlayer();
		player.civilization = new Civilization();
		player.id = ID.FromString(id);
		return player;
	}

	// The mover's relationship toward the tile owner is where the right of
	// passage is recorded.
	private static void GiveRightOfPassage(Player mover, Player tileOwner) {
		PlayerRelationship relationship = new();
		relationship.multiTurnDeals.Add(new MultiTurnDeal(
			DealType.DiplomaticAgreement, DealSubType.RightOfPassage, DealDetails.Exchange, 0, null, 20, 0, null));
		mover.playerRelationships[tileOwner.id] = relationship;
	}

	private Tile AddForeignTile(Player owner, bool barricaded, bool fortress = false) {
		Tile tile = AddNeighborsAndUpdateMap(startTile, MakePlainsTile(), TileDirection.NORTH);
		tile.owningCity = new City(tile, owner, "Foreign City", ID.None("city"));
		if (barricaded)
			tile.overlays.Add(barricade);
		if (fortress)
			tile.overlays.Add(new TerrainImprovement(Tile.TileOverlays.FORTRESS, TerrainImprovement.Layer.Holdings));
		return tile;
	}

	[Fact]
	public void ABarricadeInForeignTerritoryConsumesEveryRemainingMovementPoint() {
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		Player mover = MakeDiplomaticPlayer("player-1");
		Tile destination = AddForeignTile(MakeDiplomaticPlayer("player-2"), barricaded: true);

		MapUnit unit = MakeLandUnit(movementPoints: 2);
		unit.location = startTile;
		unit.movementPoints.reset(2);

		float cost = TilePath.GetMovementCost(mover, startTile, TileDirection.NORTH, destination, unit);

		Assert.Equal(2.0, cost, Tolerance);
		unit.movementPoints.onUnitMove(cost);
		Assert.False(unit.movementPoints.canMove);
		Assert.Equal(0.0, unit.movementPoints.remaining, Tolerance);
	}

	[Fact]
	public void ARoadedBarricadeInForeignTerritoryConsumesEveryRemainingMovementPoint() {
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		startTile.overlays.Add(road);
		Player mover = MakeDiplomaticPlayer("player-1");
		Tile destination = AddForeignTile(MakeDiplomaticPlayer("player-2"), barricaded: true);
		destination.overlays.Add(road);

		MapUnit unit = MakeLandUnit(movementPoints: 2);
		unit.location = startTile;
		unit.movementPoints.reset(2);

		// The spec applies the barricade override after all the other branches,
		// so the road discount cannot shortcut it (11_movement.md §3.8).
		float cost = TilePath.GetMovementCost(mover, startTile, TileDirection.NORTH, destination, unit);

		Assert.Equal(2.0, cost, Tolerance);
		unit.movementPoints.onUnitMove(cost);
		Assert.False(unit.movementPoints.canMove);
		Assert.Equal(0.0, unit.movementPoints.remaining, Tolerance);
	}

	[Fact]
	public void TheSameRoadedForeignStepCostsTheRoadStepWithoutTheBarricade() {
		// The control for the test above: this exact setup does reach the
		// road/railroad branch, so the barricade, not the foreign territory, is
		// what changes the cost.
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		startTile.overlays.Add(road);
		Player mover = MakeDiplomaticPlayer("player-1");
		Tile destination = AddForeignTile(MakeDiplomaticPlayer("player-2"), barricaded: false);
		destination.overlays.Add(road);

		MapUnit unit = MakeLandUnit(movementPoints: 2);
		unit.location = startTile;
		unit.movementPoints.reset(2);

		Assert.Equal(1.0 / 3.0, TilePath.GetMovementCost(mover, startTile, TileDirection.NORTH, destination, unit), Tolerance);
	}

	[Fact]
	public void ARailroadedBarricadeInForeignTerritoryConsumesEveryRemainingMovementPoint() {
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		startTile.overlays.Add(railroad);
		Player mover = MakeDiplomaticPlayer("player-1");
		Tile destination = AddForeignTile(MakeDiplomaticPlayer("player-2"), barricaded: true);
		destination.overlays.Add(railroad);

		MapUnit unit = MakeLandUnit(movementPoints: 2);
		unit.location = startTile;
		unit.movementPoints.reset(2);

		// A free railroad step is still overridden by the barricade.
		Assert.Equal(2.0, TilePath.GetMovementCost(mover, startTile, TileDirection.NORTH, destination, unit), Tolerance);
	}

	[Fact]
	public void ABarricadedIgnoredTerrainStillConsumesEveryRemainingMovementPoint() {
		// The barricade override comes after the per-terrain ignore rule too, so a
		// Keshik-style unit that would pay one point for a hill pays everything.
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		Player mover = MakeDiplomaticPlayer("player-1");
		Tile destination = AddNeighborsAndUpdateMap(startTile, MakeHillTile(), TileDirection.NORTH);
		destination.owningCity = new City(destination, MakeDiplomaticPlayer("player-2"), "Foreign City", ID.None("city"));
		destination.overlays.Add(barricade);

		MapUnit unit = MakeLandUnit(movementPoints: 2);
		unit.unitType.ignoreMovementCost.Add("hills");
		unit.location = startTile;
		unit.movementPoints.reset(2);

		Assert.Equal(2.0, TilePath.GetMovementCost(mover, startTile, TileDirection.NORTH, destination, unit), Tolerance);
	}

	[Fact]
	public void ARoadedBarricadeWithRightOfPassageCostsTheRoadStep() {
		// The override is conditional: with a right of passage the barricade has
		// no movement effect, so the road discount stands.
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		startTile.overlays.Add(road);
		Player mover = MakeDiplomaticPlayer("player-1");
		Player tileOwner = MakeDiplomaticPlayer("player-2");
		Tile destination = AddForeignTile(tileOwner, barricaded: true);
		destination.overlays.Add(road);
		GiveRightOfPassage(mover, tileOwner);

		MapUnit unit = MakeLandUnit(movementPoints: 2);
		unit.location = startTile;
		unit.movementPoints.reset(2);

		Assert.Equal(1.0 / 3.0, TilePath.GetMovementCost(mover, startTile, TileDirection.NORTH, destination, unit), Tolerance);
	}

	[Fact]
	public void TheBarricadeChargesTheRemainderNotAFixedCost() {
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		Player mover = MakeDiplomaticPlayer("player-1");
		Tile destination = AddForeignTile(MakeDiplomaticPlayer("player-2"), barricaded: true);

		// A unit that already spent most of its movement loses only what is
		// left: the charge is the remaining movement, not a whole move.
		MapUnit unit = MakeLandUnit(movementPoints: 2);
		unit.location = startTile;
		unit.movementPoints.reset(0.5f);

		Assert.Equal(0.5, TilePath.GetMovementCost(mover, startTile, TileDirection.NORTH, destination, unit), Tolerance);
	}

	[Fact]
	public void ABarricadeInTheMoversOwnTerritoryCostsTheNormalTerrainCost() {
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		Player mover = MakeDiplomaticPlayer("player-1");
		Tile destination = AddForeignTile(mover, barricaded: true);

		MapUnit unit = MakeLandUnit(movementPoints: 2);
		unit.location = startTile;
		unit.movementPoints.reset(2);

		Assert.Equal(1.0, TilePath.GetMovementCost(mover, startTile, TileDirection.NORTH, destination, unit), Tolerance);
	}

	[Fact]
	public void ABarricadeOnUnownedTerritoryCostsTheNormalTerrainCost() {
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		Player mover = MakeDiplomaticPlayer("player-1");
		Tile destination = AddNeighborsAndUpdateMap(startTile, MakePlainsTile(), TileDirection.NORTH);
		destination.overlays.Add(barricade);

		MapUnit unit = MakeLandUnit(movementPoints: 2);
		unit.location = startTile;
		unit.movementPoints.reset(2);

		Assert.Equal(1.0, TilePath.GetMovementCost(mover, startTile, TileDirection.NORTH, destination, unit), Tolerance);
	}

	[Fact]
	public void ABarricadeInForeignTerritoryWithRightOfPassageCostsTheNormalTerrainCost() {
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		Player mover = MakeDiplomaticPlayer("player-1");
		Player tileOwner = MakeDiplomaticPlayer("player-2");
		Tile destination = AddForeignTile(tileOwner, barricaded: true);
		GiveRightOfPassage(mover, tileOwner);

		MapUnit unit = MakeLandUnit(movementPoints: 2);
		unit.location = startTile;
		unit.movementPoints.reset(2);

		Assert.Equal(1.0, TilePath.GetMovementCost(mover, startTile, TileDirection.NORTH, destination, unit), Tolerance);
	}

	[Fact]
	public void AFortressInForeignTerritoryHasNoMovementEffect() {
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		Player mover = MakeDiplomaticPlayer("player-1");
		Tile destination = AddForeignTile(MakeDiplomaticPlayer("player-2"), barricaded: false, fortress: true);

		MapUnit unit = MakeLandUnit(movementPoints: 2);
		unit.location = startTile;
		unit.movementPoints.reset(2);

		Assert.Equal(1.0, TilePath.GetMovementCost(mover, startTile, TileDirection.NORTH, destination, unit), Tolerance);
	}

	[Fact]
	public void TheBarricadeRuleNeedsAUnitToCharge() {
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		Player mover = MakeDiplomaticPlayer("player-1");
		Tile destination = AddForeignTile(MakeDiplomaticPlayer("player-2"), barricaded: true);

		// The original gates the whole branch on a non-null unit; a cost query
		// with no unit - the goto turn estimate - gets the terrain cost.
		Assert.Equal(1.0, TilePath.GetMovementCost(mover, startTile, TileDirection.NORTH, destination), Tolerance);
	}
}

/// <summary>
/// The same rule, reached through the two load paths that carry the barricade
/// improvement: the shipped ruleset.json and the Civ3 BIQ-derived improvement
/// list. A game generated without a BIQ must still have the barricade for the
/// rule to fire.
/// </summary>
public sealed class BarricadeRulesetPathTest : MapBase, IClassFixture<SaveGameFixture> {
	private const double Tolerance = 0.0001;

	private readonly SaveGameFixture fixture;

	public BarricadeRulesetPathTest(SaveGameFixture fixture) {
		this.fixture = fixture;
		EngineStorage.InitializeGameDataForTests(new C7GameData.GameData(1234));
	}

	[Fact]
	public void TheShippedRulesetCarriesTheBarricadeOnTheHoldingsLayer() {
		SaveTerrainImprovement barricade = fixture.saveGame.TerrainImprovements
			.Single(i => i.key == Tile.TileOverlays.BARRICADE);

		// The improvement's presence is the carrier of the rule; its
		// movementCost stays the "does not affect the tile's cost" sentinel,
		// which is why nothing else consumes it.
		Assert.Equal(TerrainImprovement.Layer.Holdings, barricade.layer);
		Assert.Equal(-1, barricade.movementCost);

		// The BIQ-independent improvement table carries it too, so a game with
		// no BIQ is not missing the barricade.
		Assert.Contains(SaveTerrainImprovement.Civ3Improvements(), i => i.key == Tile.TileOverlays.BARRICADE);
	}

	[Fact]
	public void TheRulesetBarricadeConsumesAllRemainingMovement() {
		C7GameData.GameData gameData = fixture.saveGame.ToGameData(fixture.behaviors);
		TerrainImprovement barricade = gameData.terrainImprovements
			.First(i => i.key == Tile.TileOverlays.BARRICADE);

		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));

		Player mover = MakePlayer();
		mover.civilization = new Civilization();
		mover.id = ID.FromString("player-1");

		Player tileOwner = MakePlayer();
		tileOwner.civilization = new Civilization();
		tileOwner.id = ID.FromString("player-2");

		Tile destination = AddNeighborsAndUpdateMap(startTile, MakePlainsTile(), TileDirection.NORTH);
		destination.owningCity = new City(destination, tileOwner, "Foreign City", ID.None("city"));
		// The improvement that ships in ruleset.json, not a test double.
		destination.overlays.Add(barricade);

		MapUnit unit = MakeLandUnit(movementPoints: 2);
		unit.location = startTile;
		unit.movementPoints.reset(2);

		Assert.Equal(2.0, TilePath.GetMovementCost(mover, startTile, TileDirection.NORTH, destination, unit), Tolerance);
	}
}
