using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using C7Engine;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using QueryCiv3;
using QueryCiv3.Biq;
using Xunit;

namespace EngineTests.GameData;

/// <summary>
/// Guards the amphibious assault step-cost override (11_movement.md §5 step 9):
/// after the step charge, a land-classed unit stepping from water onto land has
/// spent movement set to its maximum - it spends everything. The original reads
/// the source tile's water flag, the destination tile's water flag and the
/// unit type's domain class (unit-type +0x9c) and stores the maximum over the
/// charge, so it is a SET, not a second add (0x5b94b2-0x5b94e9).
///
/// C7 stores the movement REMAINDER, not the spent field, so "spent = maximum"
/// is "no remainder left". The tests below fail against the step charge alone
/// (the TODO this replaces): the cost-1 and free landings would leave the unit
/// with movement, and the already-spent case would leave the remainder that the
/// charge left behind.
/// </summary>
public sealed class AmphibiousAssaultTest : IClassFixture<SaveGameFixture> {
	private const double Tolerance = 0.0001;
	private const int SourceX = 50;
	private const int SourceY = 50;
	private const int DestinationX = 52;
	private const int DestinationY = 50;

	private readonly C7GameData.GameData gd;

	public AmphibiousAssaultTest(SaveGameFixture fixture) {
		gd = fixture.saveGame.ToGameData(fixture.behaviors);
		EngineStorage.InitializeGameDataForTests(gd);
		// Move waits on an animation-complete message no test UI sends.
		EngineStorage.animationsEnabled = false;
	}

	// ---------- helpers ----------

	private Player MakePlayer() {
		Civilization civ = new Civilization("TestCiv") {
			noun = "TestCivs",
			adjective = "Test",
		};
		civ.traits = [];
		return new Player {
			id = gd.GenerateID("player"),
			civilization = civ,
			rules = gd.rules,
			government = new Government(),
		};
	}

	// A flat tile of the fixture's real map, emptied of everything the generator
	// may have left on it. `isWater` and the movement cost come from the key.
	private Tile CleanTile(int x, int y, string key, int movementCost) {
		Tile tile = gd.map.tileAt(x, y);
		if (tile.cityAtTile != null) {
			tile.cityAtTile = null;   // clears the overlays as a side effect
		}
		tile.unitsOnTile.Clear();
		tile.overlays.Clear();
		tile.hasGoodyHut = false;
		tile.hasBarbarianCamp = false;
		tile.baseTerrainType = new TerrainType { Key = key, movementCost = movementCost };
		tile.overlayTerrainType = tile.baseTerrainType;
		return tile;
	}

	private Tile CleanWaterTile(int x, int y) => CleanTile(x, y, "coast", 1);
	private Tile CleanLandTile(int x, int y, int movementCost = 1) => CleanTile(x, y, "plains", movementCost);

	private MapUnit MakeUnit(Player owner, UnitPrototype proto, Tile tile) {
		MapUnit unit = proto.GetInstance(gd.GenerateID(proto.name), proto, owner, location: tile);
		unit.experienceLevel = gd.experienceLevels.First(e => e.key == "Regular");
		unit.experienceLevelKey = "Regular";
		tile.unitsOnTile.Add(unit);
		gd.mapUnits.Add(unit);
		owner.AddUnit(unit);
		return unit;
	}

	private City MakeCity(Player owner, Tile tile) {
		City city = new City(tile, owner, "Harbour", gd.GenerateID("city"));
		tile.cityAtTile = city;
		owner.cities.Add(city);
		gd.cities.Add(city);
		return city;
	}

	private static UnitPrototype MakeLandPrototype(int movement) {
		UnitPrototype proto = new UnitPrototype { name = $"TestLand{movement}", movement = movement };
		proto.categories.Add("Land");
		return proto;
	}

	private static UnitPrototype MakeSeaPrototype(int movement) {
		UnitPrototype proto = new UnitPrototype { name = $"TestSea{movement}", movement = movement };
		proto.categories.Add("Sea");
		return proto;
	}

	private static UnitPrototype MakeAirPrototype(int movement) {
		UnitPrototype proto = new UnitPrototype { name = $"TestAir{movement}", movement = movement };
		proto.categories.Add("Air");
		return proto;
	}

	// ---------- the rule fires ----------

	// The rule must override the step-8 charge whatever the destination's
	// terrain cost is. The 0- and 1-cost destinations are the falsifying cases:
	// a plain charge would leave the unit with 3 or 2 of its 3 points.
	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(3)]
	public async Task ALandUnitLandingFromWaterSpendsEverythingWhateverTheDestinationCost(int movementCost) {
		Tile source = CleanWaterTile(SourceX, SourceY);
		Tile destination = CleanLandTile(DestinationX, DestinationY, movementCost);
		MapUnit unit = MakeUnit(MakePlayer(), MakeLandPrototype(movement: 3), source);
		Assert.Equal(3.0, unit.movementPoints.remaining, Tolerance);

		Assert.True(await unit.Move(TileDirection.EAST));

		Assert.Equal(destination, unit.location);
		Assert.Equal(0.0, unit.movementPoints.remaining, Tolerance);
	}

	// The "set, not add" case: the unit had already spent movement, so the
	// charge alone would leave it with a point. Setting the spent total to the
	// maximum leaves it with nothing.
	[Fact]
	public async Task ALandUnitAlreadySpentStillSpendsEverythingOnLanding() {
		Tile source = CleanWaterTile(SourceX, SourceY);
		Tile destination = CleanLandTile(DestinationX, DestinationY, movementCost: 1);
		MapUnit unit = MakeUnit(MakePlayer(), MakeLandPrototype(movement: 3), source);
		unit.movementPoints.onUnitMove(1f);   // one point already spent
		Assert.Equal(2.0, unit.movementPoints.remaining, Tolerance);

		Assert.True(await unit.Move(TileDirection.EAST));

		// Spent equals the maximum; in C7's remainder form that is no
		// remainder. A charge-only implementation would leave 1.
		Assert.Equal(0.0, unit.movementPoints.remaining, Tolerance);
		Assert.Equal(unit.MaxMovementPoints, unit.MaxMovementPoints - unit.movementPoints.remaining, Tolerance);
	}

	// The army-aware maximum is the corrected one (11_movement.md §2.2): an
	// army that lands spends everything it has, which is still nothing left.
	// This is here so the rule's "maximum" cannot drift to a different notion.
	[Fact]
	public async Task AnArmyLandingFromWaterSpendsEverythingToo() {
		Tile source = CleanWaterTile(SourceX, SourceY);
		Tile destination = CleanLandTile(DestinationX, DestinationY, movementCost: 1);
		Player player = MakePlayer();

		UnitPrototype armyType = new UnitPrototype { name = "TestArmy", movement = 1, capacity = 3 };
		armyType.flags.Add(SaveUnitPrototype.Flag.Army);
		armyType.categories.Add("Land");
		MapUnit army = MakeUnit(player, armyType, source);
		MapUnit member = MakeUnit(player, MakeLandPrototype(movement: 3), source);
		Assert.True(member.LoadIntoArmy(army));
		Assert.Equal(4.0, army.MaxMovementPoints, Tolerance);

		Assert.True(await army.Move(TileDirection.EAST));

		Assert.Equal(0.0, army.movementPoints.remaining, Tolerance);
	}

	// ---------- the three conditions ----------

	// The class test and the two terrain tests at once, including the
	// water-to-water land step that the passability rules make unreachable as a
	// completed step (a land unit may not enter a water tile): the predicate is
	// the only place the destination-water condition can be isolated for a land
	// mover.
	[Fact]
	public void TheRuleNeedsWaterAsSourceLandAsDestinationAndALandMover() {
		Tile water = CleanWaterTile(SourceX, SourceY);
		Tile land = CleanLandTile(DestinationX, DestinationY);

		MapUnit landUnit = MakeUnit(MakePlayer(), MakeLandPrototype(3), land);
		MapUnit seaUnit = MakeUnit(MakePlayer(), MakeSeaPrototype(3), water);
		MapUnit airUnit = MakeUnit(MakePlayer(), MakeAirPrototype(3), land);

		Assert.True(landUnit.IsAmphibiousAssaultStep(water, land));
		Assert.False(landUnit.IsAmphibiousAssaultStep(land, land));
		Assert.False(landUnit.IsAmphibiousAssaultStep(water, water));
		Assert.False(landUnit.IsAmphibiousAssaultStep(land, water));
		Assert.False(seaUnit.IsAmphibiousAssaultStep(water, land));
		Assert.False(airUnit.IsAmphibiousAssaultStep(water, land));
	}

	// The original has no city or colony branch: only the destination's water
	// flag is read, so a landfall onto a city still counts as land.
	[Fact]
	public void ALandfallOntoACityStillCountsAsLand() {
		Tile water = CleanWaterTile(SourceX, SourceY);
		Tile landWithCity = CleanLandTile(DestinationX, DestinationY);
		Player player = MakePlayer();
		MakeCity(player, landWithCity);
		MapUnit unit = MakeUnit(player, MakeLandPrototype(3), water);

		Assert.True(unit.IsAmphibiousAssaultStep(water, landWithCity));
	}

	// A land-to-land step: source is not water. Falsifies a rule that fires on
	// any land destination.
	[Fact]
	public async Task ALandToLandStepOnlyPaysTheTerrainCost() {
		Tile source = CleanLandTile(SourceX, SourceY);
		Tile destination = CleanLandTile(DestinationX, DestinationY, movementCost: 1);
		MapUnit unit = MakeUnit(MakePlayer(), MakeLandPrototype(movement: 3), source);

		Assert.True(await unit.Move(TileDirection.EAST));

		Assert.Equal(2.0, unit.movementPoints.remaining, Tolerance);
	}

	// A sea unit water to water: source is water but the destination is too.
	[Fact]
	public async Task ASeaUnitSteppingBetweenWaterTilesOnlyPaysTheTerrainCost() {
		Tile source = CleanWaterTile(SourceX, SourceY);
		Tile destination = CleanWaterTile(DestinationX, DestinationY);
		MapUnit unit = MakeUnit(MakePlayer(), MakeSeaPrototype(movement: 3), source);

		Assert.True(await unit.Move(TileDirection.EAST));

		Assert.Equal(2.0, unit.movementPoints.remaining, Tolerance);
	}

	// A sea unit water to land: the terrains qualify, but the mover is not
	// land-classed. A sea unit may enter land only where its own city stands.
	// Falsifies a class test that only excludes air units.
	[Fact]
	public async Task ASeaUnitLandingInItsOwnPortOnlyPaysTheHarbourCost() {
		Tile source = CleanWaterTile(SourceX, SourceY);
		Tile destination = CleanLandTile(DestinationX, DestinationY, movementCost: 3);
		Player player = MakePlayer();
		MakeCity(player, destination);
		MapUnit unit = MakeUnit(player, MakeSeaPrototype(movement: 3), source);

		Assert.True(await unit.Move(TileDirection.EAST));

		// The sea-into-city step is one point however steep the land (spec §3.7).
		Assert.Equal(2.0, unit.movementPoints.remaining, Tolerance);
	}

	// ---------- ordering: only a paid step lands the override ----------

	// A land unit may not step onto water at all, so the land-to-water
	// direction cannot reach the override; the step is refused and the movement
	// is untouched.
	[Fact]
	public async Task ALandUnitSteppingOntoWaterIsRefusedAndKeepsItsMovement() {
		Tile source = CleanLandTile(SourceX, SourceY);
		Tile destination = CleanWaterTile(DestinationX, DestinationY);
		MapUnit unit = MakeUnit(MakePlayer(), MakeLandPrototype(movement: 3), source);

		Assert.False(await unit.Move(TileDirection.EAST));

		Assert.Equal(source, unit.location);
		Assert.Equal(3.0, unit.movementPoints.remaining, Tolerance);
	}

	// A refused landing pays nothing. The destination is impassable, which the
	// original refuses before the cost and the charge (11_movement.md §5 step
	// 6), so the override never runs.
	[Fact]
	public async Task ARefusedLandingDoesNotConsumeTheMovementPoints() {
		Tile source = CleanWaterTile(SourceX, SourceY);
		Tile destination = CleanLandTile(DestinationX, DestinationY, movementCost: 1);
		destination.overlayTerrainType.impassable = true;
		MapUnit unit = MakeUnit(MakePlayer(), MakeLandPrototype(movement: 3), source);

		Assert.False(await unit.Move(TileDirection.EAST));

		Assert.Equal(source, unit.location);
		Assert.Equal(3.0, unit.movementPoints.remaining, Tolerance);
	}

	// Nothing about the rule survives the turn: the start-of-turn reset restores
	// the whole budget (spec §7).
	[Fact]
	public async Task TheSpentMovementDoesNotSurviveIntoTheNextTurn() {
		Tile source = CleanWaterTile(SourceX, SourceY);
		Tile destination = CleanLandTile(DestinationX, DestinationY, movementCost: 1);
		MapUnit unit = MakeUnit(MakePlayer(), MakeLandPrototype(movement: 3), source);

		Assert.True(await unit.Move(TileDirection.EAST));
		Assert.Equal(0.0, unit.movementPoints.remaining, Tolerance);

		unit.OnBeginTurn();

		Assert.Equal(3.0, unit.movementPoints.remaining, Tolerance);
		Assert.Equal(unit.MaxMovementPoints, unit.movementPoints.remaining, Tolerance);
	}
}

/// <summary>
/// The rules data the rule reads, on both load paths: every unit prototype's
/// domain class, which the rule's land test asks for. The BIQ carries it as
/// PRTO.Type (0 land, 1 sea, 2 air), and the generated ruleset carries it as the
/// prototype's categories - the same field the running engine's IsLandUnit
/// reads.
/// </summary>
public sealed class AmphibiousAssaultDataTest : IClassFixture<SaveGameFixture> {
	private readonly SaveGame save;

	public AmphibiousAssaultDataTest(SaveGameFixture fixture) {
		save = fixture.saveGame;
	}

	[Fact]
	public void TheRulesetGivesEveryUnitPrototypeExactlyOneDomainClass() {
		foreach (SaveUnitPrototype proto in save.UnitPrototypes) {
			string[] classes = proto.categories.Where(c => c is "Land" or "Sea" or "Air").ToArray();
			Assert.Single(classes);
		}

		Assert.Contains("Land", save.UnitPrototypes.First(p => p.name == "Warrior").categories);
		Assert.Contains("Sea", save.UnitPrototypes.First(p => p.name == "Galley").categories);
		Assert.Contains("Air", save.UnitPrototypes.First(p => p.name == "Fighter").categories);
		// An army is land-classed by its own type, which is what the rule reads
		// (not a member's class).
		Assert.Contains("Land", save.UnitPrototypes.First(p => p.name == "Army").categories);
	}

	// The same class, read back out of the shipped BIQ with QueryCiv3 and run
	// through the BIQ import helper, so the ruleset copy cannot drift from the
	// data the original engine consumes.
	[SkippableFact]
	public void TheShippedBiqDomainFieldAgreesWithTheRulesetClasses() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");

		BiqData biq = BiqData.LoadFile(PathUtils.defaultBicPath);

		foreach (PRTO prto in biq.Prto) {
			string imported = ImportCiv3.GetUnitCategories(prto).SingleOrDefault();
			Assert.Contains(imported, save.UnitPrototypes.First(p => p.name == prto.Name).categories);
		}

		Assert.Equal("Land", ImportCiv3.GetUnitCategories(biq.Prto.First(p => p.Name == "Warrior")).Single());
		Assert.Equal("Sea", ImportCiv3.GetUnitCategories(biq.Prto.First(p => p.Name == "Galley")).Single());
		Assert.Equal("Air", ImportCiv3.GetUnitCategories(biq.Prto.First(p => p.Name == "Fighter")).Single());
	}
}
