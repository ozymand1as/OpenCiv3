using System.Collections.Generic;
using System.Linq;
using C7Engine;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using QueryCiv3;
using QueryCiv3.Biq;
using Xunit;

namespace EngineTests.GameData;

/// <summary>
/// Guards the shipped rules data for the per-terrain "ignore movement cost"
/// rule (11_movement.md §3.6).
///
/// Civ3's PRTO.IgnoreMovementCost is one flag byte per terrain. When it is set
/// for the terrain a unit is stepping onto, the step costs one movement point
/// instead of the terrain's MovementCost - but only when a road or railroad did
/// not already discount the step. The expected values below are the whole set
/// probed out of the install's conquests.biq with QueryCiv3: 89 of the 141 unit
/// records carry at least one flag, which is 75 distinct names, and all but two
/// of them carry only the inert Sea entry. Nothing is summarised, so a dropped
/// or mistyped carrier is caught.
/// </summary>
public class IgnoreMovementCostTest : IClassFixture<SaveGameFixture> {
	private readonly SaveGameFixture fixture;
	private readonly SaveGame save;

	public IgnoreMovementCostTest(SaveGameFixture fixture) {
		this.fixture = fixture;
		save = fixture.saveGame;
	}
	// Every unit type in the shipped BIQ whose IgnoreMovementCost array has a
	// flag set, with the terrain keys it sets. The value is the comma-joined
	// key list so the 75-row table stays readable.
	public static TheoryData<string, string> ShippedCarriers => new() {
		{ "AEGIS Cruiser", "sea" },
		{ "Ansar Warrior", "sea" },
		{ "Archer", "sea" },
		{ "Army", "sea" },
		{ "Artillery", "sea" },
		{ "Battleship", "sea" },
		{ "Berserk", "sea" },
		{ "Bowman", "sea" },
		{ "Cannon", "sea" },
		{ "Caravel", "sea" },
		{ "Carrack", "sea" },
		{ "Carrier", "sea" },
		{ "Catapult", "sea" },
		{ "Cavalry", "sea" },
		{ "Chariot", "sea" },
		{ "Chasqui Scout", "hills,mountains" },
		{ "Conquistador", "sea" },
		{ "Cossack", "sea" },
		{ "Cruise Missile", "sea" },
		{ "Destroyer", "sea" },
		{ "Dromon", "sea" },
		{ "Enkidu Warrior", "sea" },
		{ "Explorer", "sea" },
		{ "Frigate", "sea" },
		{ "Galleon", "sea" },
		{ "Galley", "sea" },
		{ "Gallic Swordsman", "sea" },
		{ "Guerilla", "sea" },
		{ "Hoplite", "sea" },
		{ "Horseman", "sea" },
		{ "Hwach'a", "sea" },
		{ "ICBM", "sea" },
		{ "Immortals", "sea" },
		{ "Impi", "sea" },
		{ "Infantry", "sea" },
		{ "Ironclad", "sea" },
		{ "Keshik", "hills,mountains,sea" },
		{ "Knight", "sea" },
		{ "Leader", "sea" },
		{ "Legionary", "sea" },
		{ "Longbowman", "sea" },
		{ "Man-O-War", "sea" },
		{ "Marine", "sea" },
		{ "Mech Infantry", "sea" },
		{ "Medieval Infantry", "sea" },
		{ "Modern Armor", "sea" },
		{ "Modern Paratrooper", "sea" },
		{ "Mounted Warrior", "sea" },
		{ "Musketeer", "sea" },
		{ "Musketman", "sea" },
		{ "Nuclear Submarine", "sea" },
		{ "Numidian Mercenary", "sea" },
		{ "Panzer", "sea" },
		{ "Pikeman", "sea" },
		{ "Privateer", "sea" },
		{ "Radar Artillery", "sea" },
		{ "Rider", "sea" },
		{ "Rifleman", "sea" },
		{ "Samurai", "sea" },
		{ "Scout", "sea" },
		{ "Settler", "sea" },
		{ "Sipahi", "sea" },
		{ "Spearman", "sea" },
		{ "Submarine", "sea" },
		{ "Swiss Mercenary", "sea" },
		{ "Swordsman", "sea" },
		{ "Tactical Nuke", "sea" },
		{ "Tank", "sea" },
		{ "Three-Man Chariot", "sea" },
		{ "Transport", "sea" },
		{ "Trebuchet", "sea" },
		{ "War Chariot", "sea" },
		{ "War Elephant", "sea" },
		{ "Warrior", "sea" },
		{ "Worker", "sea" },
	};

	[Theory]
	[MemberData(nameof(ShippedCarriers))]
	public void ShippedCarrierIgnoresExactlyTheProbedTerrains(string name, string expectedTerrains) {
		SaveUnitPrototype proto = save.UnitPrototypes.First(p => p.name == name);

		Assert.Equal(
			expectedTerrains.Split(',').OrderBy(t => t).ToArray(),
			proto.ignoreMovementCost.OrderBy(t => t).ToArray());
	}

	// The Immobile ability (PRTO ability 0xA, Flags1[1] bit 2) as the shipped
	// conquests.biq carries it: every air unit, the ICBM (land-classed) and the
	// regicide Princess (land-classed). This is not an inert flag - the ICBM and
	// the Princess would otherwise walk, which is exactly what the fork did before
	// the step executor's first gate existed.
	public static readonly string[] ShippedImmobileNames = {
		"Bomber", "F-15", "Fighter", "Helicopter", "ICBM", "Jet Fighter", "Princess",
		"Stealth Bomber", "Stealth Fighter",
	};

	public static IEnumerable<object[]> ShippedImmobileCarriers =>
		ShippedImmobileNames.Select(n => new object[] { n });

	[Theory]
	[MemberData(nameof(ShippedImmobileCarriers))]
	public void ShippedImmobileCarrierCarriesTheFlag(string name) {
		SaveUnitPrototype proto = save.UnitPrototypes.First(p => p.name == name);

		Assert.Contains(SaveUnitPrototype.Flag.Immobile, proto.flags);
	}

	[Fact]
	public void NoOtherUnitTypeIsImmobile() {
		string[] carriers = save.UnitPrototypes
			.Where(p => p.flags.Contains(SaveUnitPrototype.Flag.Immobile))
			.Select(p => p.name)
			.OrderBy(n => n)
			.ToArray();

		Assert.Equal(
			new[] {
				"Bomber", "F-15", "Fighter", "Helicopter", "ICBM", "Jet Fighter", "Princess",
				"Stealth Bomber", "Stealth Fighter",
			},
			carriers);
	}

	[Fact]
	public void TheImmobileFlagSurvivesConversionToTheRuntimeUnitPrototype() {
		C7GameData.GameData gameData = save.ToGameData(fixture.behaviors);

		// The step executor reads the runtime prototype, so the flag has to make
		// it across from the save data for the gate to fire.
		Assert.True(gameData.unitPrototypes.First(p => p.name == "ICBM").immobile);
		Assert.True(gameData.unitPrototypes.First(p => p.name == "Princess").immobile);
		Assert.True(gameData.unitPrototypes.First(p => p.name == "Fighter").immobile);
		Assert.False(gameData.unitPrototypes.First(p => p.name == "Warrior").immobile);
	}

	// The tests above hold the checked-in ruleset to values measured from the BIQ
	// by hand. This one re-reads the shipped BIQ, so the copy cannot drift from
	// the data the original engine consumes, and it runs the BIQ import helper
	// itself rather than only the ruleset path.
	[SkippableFact]
	public void TheShippedBiqAndTheRulesetAgreeAboutImmobile() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");

		BiqData biq = BiqData.LoadFile(PathUtils.defaultBicPath);

		// Every unit record whose ability byte carries Immobile, by name.
		string[] fromBiq = biq.Prto
			.Where(p => p.Immobile)
			.Select(p => p.Name)
			.Distinct()
			.OrderBy(n => n)
			.ToArray();
		Assert.Equal(ShippedImmobileNames.OrderBy(n => n).ToArray(), fromBiq);

		// The import helper maps the ability onto the same flag the ruleset
		// carries, for every record.
		foreach (PRTO prto in biq.Prto) {
			SaveUnitPrototype proto = save.UnitPrototypes.First(p => p.name == prto.Name);

			Assert.Equal(prto.Immobile, proto.flags.Contains(SaveUnitPrototype.Flag.Immobile));
		}
	}

	[Fact]
	public void NoOtherUnitTypeIgnoresATerrainMovementCost() {
		// The set is asserted as a whole: a flag added to the wrong unit is a
		// play difference just as much as a missing one.
		string[] carriers = save.UnitPrototypes
			.Where(p => p.ignoreMovementCost.Count > 0)
			.Select(p => p.name)
			.OrderBy(n => n)
			.ToArray();

		Assert.Equal(
			new[] {
				"AEGIS Cruiser", "Ansar Warrior", "Archer", "Army", "Artillery", "Battleship", "Berserk",
				"Bowman", "Cannon", "Caravel", "Carrack", "Carrier", "Catapult", "Cavalry", "Chariot",
				"Chasqui Scout", "Conquistador", "Cossack", "Cruise Missile", "Destroyer", "Dromon",
				"Enkidu Warrior", "Explorer", "Frigate", "Galleon", "Galley", "Gallic Swordsman", "Guerilla",
				"Hoplite", "Horseman", "Hwach'a", "ICBM", "Immortals", "Impi", "Infantry", "Ironclad",
				"Keshik", "Knight", "Leader", "Legionary", "Longbowman", "Man-O-War", "Marine",
				"Mech Infantry", "Medieval Infantry", "Modern Armor", "Modern Paratrooper", "Mounted Warrior",
				"Musketeer", "Musketman", "Nuclear Submarine", "Numidian Mercenary", "Panzer", "Pikeman",
				"Privateer", "Radar Artillery", "Rider", "Rifleman", "Samurai", "Scout", "Settler", "Sipahi",
				"Spearman", "Submarine", "Swiss Mercenary", "Swordsman", "Tactical Nuke", "Tank",
				"Three-Man Chariot", "Transport", "Trebuchet", "War Chariot", "War Elephant", "Warrior",
				"Worker"
			},
			carriers);
	}

	[Fact]
	public void TheIgnoreSetSurvivesConversionToTheRuntimeUnitPrototype() {
		C7GameData.GameData gameData = save.ToGameData(fixture.behaviors);

		// The movement code reads the runtime unit prototype, so the set has to
		// make it across from the save data for the rule to take effect.
		foreach (string name in new[] { "Swordsman", "Warrior", "Horseman", "Musketman" }) {
			UnitPrototype proto = gameData.unitPrototypes.First(p => p.name == name);

			Assert.Equal(new[] { "sea" }, proto.ignoreMovementCost.OrderBy(t => t).ToArray());
		}
	}

	[Fact]
	public void TerrainLookupMatchesTheIgnoredTerrainKey() {
		C7GameData.GameData gameData = save.ToGameData(fixture.behaviors);

		UnitPrototype keshik = gameData.unitPrototypes.First(p => p.name == "Keshik");
		TerrainType mountains = gameData.terrainTypes.First(t => t.Key == "mountains");
		TerrainType hills = gameData.terrainTypes.First(t => t.Key == "hills");

		Assert.True(keshik.IgnoresMovementCostOf(hills));
		Assert.True(keshik.IgnoresMovementCostOf(mountains));

		UnitPrototype swordsman = gameData.unitPrototypes.First(p => p.name == "Swordsman");
		Assert.False(swordsman.IgnoresMovementCostOf(hills));
		Assert.False(swordsman.IgnoresMovementCostOf(mountains));
	}

	[Fact]
	public void TheInertSeaEntryIsImportedButNeverReadsAsALandTerrain() {
		// The shipped rules set Sea on almost every land unit. It is inert - a
		// land unit cannot legally enter a sea tile - but it is still part of
		// the carrier set, and it must not leak onto a land terrain.
		C7GameData.GameData gameData = save.ToGameData(fixture.behaviors);

		UnitPrototype warrior = gameData.unitPrototypes.First(p => p.name == "Warrior");
		Assert.True(warrior.IgnoresMovementCostOf(gameData.terrainTypes.First(t => t.Key == "sea")));
		Assert.False(warrior.IgnoresMovementCostOf(gameData.terrainTypes.First(t => t.Key == "plains")));
	}

	// The tests above hold the checked-in ruleset to values measured from the
	// BIQ by hand. This one re-reads the shipped BIQ, so the copy cannot drift
	// from the data the original engine consumes, and it runs the BIQ import
	// helper itself rather than only the ruleset path.
	[SkippableFact]
	public void TheShippedBiqAndTheRulesetAgree() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");

		BiqData biq = BiqData.LoadFile(PathUtils.defaultBicPath);

		// 89 unit records carry at least one flag; deduplicated by name the
		// shipped rules have 75 carriers.
		Assert.Equal(89, biq.Prto.Count(p => Enumerable.Range(0, 14).Any(i => p.IgnoreMovementCost[i])));
		Assert.Equal(75, biq.Prto.Select(p => p.Name).Distinct()
			.Count(n => biq.Prto.Where(p => p.Name == n).Any(p => Enumerable.Range(0, 14).Any(i => p.IgnoreMovementCost[i]))));

		// The import helper maps each flagged byte to the same terrain key the
		// ruleset carries, including the mass inert Sea entry.
		foreach (PRTO prto in biq.Prto) {
			string[] imported = ImportCiv3.GetIgnoredMovementCostTerrains(prto).OrderBy(t => t).ToArray();
			SaveUnitPrototype proto = save.UnitPrototypes.First(p => p.name == prto.Name);

			Assert.Equal(imported, proto.ignoreMovementCost.OrderBy(t => t).ToArray());
		}

		// The two units the shipped rules use the flag to actually distinguish:
		// the Keshik's hill and mountain steps, and the Chasqui Scout's.
		Assert.Equal(
			new[] { "hills", "mountains", "sea" },
			ImportCiv3.GetIgnoredMovementCostTerrains(biq.Prto.First(p => p.Name == "Keshik")).OrderBy(t => t).ToArray());
		Assert.Equal(
			new[] { "hills", "mountains" },
			ImportCiv3.GetIgnoredMovementCostTerrains(biq.Prto.First(p => p.Name == "Chasqui Scout")).OrderBy(t => t).ToArray());
		Assert.Empty(ImportCiv3.GetIgnoredMovementCostTerrains(biq.Prto.First(p => p.Name == "Bomber")));
	}
}

/// <summary>
/// The behaviour of the per-terrain "ignore movement cost" rule: a step onto an
/// ignored terrain costs ONE WHOLE MOVEMENT POINT, applied after the
/// road/railroad discount was ruled out (11_movement.md §3.6).
///
/// The value is measured, not read off the spec's prose: the original loads
/// RULE.MovementAlongRoads into the cost value for this branch at 0x58036f, the
/// same value the air branch
/// loads at that address and the sea-into-a-city branch loads at 0x580336. It is
/// NOT the raw constant 1 the road branch (0x580282) and the
/// all-terrain-as-roads branch (0x5802a9, 0x5802e7) load. An earlier revision of
/// §3.6 claimed "1 internal unit = a third of a point"; that was wrong, and so
/// was the parity note built on it, which called the one-point charge a
/// three-fold defect. The tests below pin the one-point value so a later round
/// cannot "fix" it to a third of a point.
/// </summary>
public sealed class IgnoreMovementCostMovementTest : MapBase {
	private const double Tolerance = 0.0001;

	public IgnoreMovementCostMovementTest() {
		// Adding a road invalidates the cached trade network, and the river rule
		// reads the game's technology list.
		EngineStorage.InitializeGameDataForTests(new C7GameData.GameData(1234));
		// The movement scale is a rule, read at runtime (11_movement.md §2.1). The
		// test game data carries none, so give it the shipped one explicitly: the
		// value tests below then assert against a scale the test itself owns.
		EngineStorage.gameData.rules = new Rules();
	}

	// The shipped Keshik ignores hills, mountains and (inertly) sea.
	private static MapUnit MakeMountainIgnoringUnit() {
		MapUnit unit = MakeLandUnit(movementPoints: 2);
		unit.unitType.ignoreMovementCost.Add("hills");
		unit.unitType.ignoreMovementCost.Add("mountains");
		return unit;
	}

	[Fact]
	public void TerrainKeepsItsFullCostWithoutTheFlag() {
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		var mountain = AddNeighborsAndUpdateMap(startTile, MakeMountainTile(), TileDirection.NORTH);

		MapUnit unit = MakeLandUnit(movementPoints: 2);
		unit.location = startTile;

		Assert.Equal(3.0, TilePath.GetMovementCost(unit.owner, startTile, TileDirection.NORTH, mountain, unit));
	}

	[Fact]
	public void AnIgnoredMountainStepCostsOneMovementPoint() {
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		var mountain = AddNeighborsAndUpdateMap(startTile, MakeMountainTile(), TileDirection.NORTH);

		MapUnit unit = MakeMountainIgnoringUnit();
		unit.location = startTile;

		Assert.Equal(1.0, TilePath.GetMovementCost(unit.owner, startTile, TileDirection.NORTH, mountain, unit));
	}

	// The measured value, in internal units: the original's cost register is
	// loaded with the movement scale itself (0x58036f), so the step is exactly
	// one movement point and NOT the road branch's raw 1 internal unit. This is
	// the assertion that the parity note's "3x the original" claim fails.
	[Fact]
	public void TheIgnoredTerrainStepIsTheScaleItselfAndNotTheRoadConstant() {
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		var mountain = AddNeighborsAndUpdateMap(startTile, MakeMountainTile(), TileDirection.NORTH);

		MapUnit unit = MakeMountainIgnoringUnit();
		unit.location = startTile;

		int scale = EngineStorage.gameData.rules.MovementAlongRoads;
		Assert.Equal(3, scale);

		float cost = TilePath.GetMovementCost(unit.owner, startTile, TileDirection.NORTH, mountain, unit);

		// One whole point, in the internal units the original charges...
		Assert.Equal(Rules.OneMovementPointInternalUnits(scale) / (float)scale, cost, Tolerance);
		// ...and three times the raw constant the road and all-terrain-as-roads
		// branches charge, which is what a misreading of §3.6 would produce.
		Assert.NotEqual(Rules.RoadStepInternalUnits / (float)scale, cost);
		Assert.Equal(Rules.RoadStepInternalUnits / (float)scale * scale, cost, Tolerance);
	}

	// The rule reads the scale at runtime, so a scenario that changes
	// MovementAlongRoads changes the road step but leaves the ignored-terrain
	// step at exactly one movement point. A hard-coded fraction would fail here.
	[Fact]
	public void TheIgnoredTerrainStepStaysOneMovementPointAtADifferentScale() {
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		var mountain = AddNeighborsAndUpdateMap(startTile, MakeMountainTile(), TileDirection.NORTH);

		MapUnit unit = MakeMountainIgnoringUnit();
		unit.location = startTile;

		int originalScale = EngineStorage.gameData.rules.MovementAlongRoads;
		try {
			EngineStorage.gameData.rules.MovementAlongRoads = 4;

			Assert.Equal(1.0, TilePath.GetMovementCost(unit.owner, startTile, TileDirection.NORTH, mountain, unit), Tolerance);

			// The road step still moves with the scale: a quarter of a point.
			startTile.overlays.Add(road);
			mountain.overlays.Add(road);
			Assert.Equal(0.25, TilePath.GetMovementCost(unit.owner, startTile, TileDirection.NORTH, mountain, unit), Tolerance);
		} finally {
			EngineStorage.gameData.rules.MovementAlongRoads = originalScale;
		}
	}

	// Civ3's Unit_ignores_terrain_move_cost (0x5bc830) consults an army's lead
	// member's type as well as the army's own (0x5bc862 reads the lead member's
	// record and returns true when either has the flag). The step still costs one
	// point; only the decision who ignores the terrain changes.
	[Fact]
	public void AnArmyInheritsTheIgnoredTerrainsOfItsLeadMember() {
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		var mountain = AddNeighborsAndUpdateMap(startTile, MakeMountainTile(), TileDirection.NORTH);

		Player player = MakePlayer();
		MapUnit army = MakeArmyUnit(player, startTile);
		MapUnit member = MakeMountainIgnoringUnit();
		GiveExperience(member);
		member.owner = player;
		member.location = startTile;
		startTile.unitsOnTile.Add(member);
		Assert.True(member.LoadIntoArmy(army));

		// The army's own type ignores nothing; its lead member's type ignores the
		// mountain, so the army pays one point rather than the mountain's three.
		Assert.False(army.unitType.IgnoresMovementCostOf(mountain.overlayTerrainType));
		Assert.True(army.IgnoresMovementCostOf(mountain.overlayTerrainType));
		Assert.Equal(1.0, TilePath.GetMovementCost(army.owner, startTile, TileDirection.NORTH, mountain, army));
	}

	// The lead-member lookup yields nothing for an army with mixed members
	// (the lookup at 0x5bc6d0 returns -1 as soon as two members' type ids differ,
	// 0x5bc7d3), and the caller then reads the army's own record only. So a mixed
	// army keeps its own type's flags - which here means it pays the mountain's
	// full cost.
	[Fact]
	public void AMixedArmyFallsBackToItsOwnType() {
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		var mountain = AddNeighborsAndUpdateMap(startTile, MakeMountainTile(), TileDirection.NORTH);

		Player player = MakePlayer();
		MapUnit army = MakeArmyUnit(player, startTile);

		MapUnit hillIgnorer = MakeMountainIgnoringUnit();
		GiveExperience(hillIgnorer);
		hillIgnorer.owner = player;
		hillIgnorer.location = startTile;
		startTile.unitsOnTile.Add(hillIgnorer);
		Assert.True(hillIgnorer.LoadIntoArmy(army));

		// A second member of a different type: the lead-member lookup gives up.
		MapUnit other = MakeLandUnit(movementPoints: 2);
		GiveExperience(other);
		other.owner = player;
		other.location = startTile;
		startTile.unitsOnTile.Add(other);
		Assert.True(other.LoadIntoArmy(army));

		Assert.Null(army.LeadMemberType());
		Assert.False(army.IgnoresMovementCostOf(mountain.overlayTerrainType));
		Assert.Equal(3.0, TilePath.GetMovementCost(army.owner, startTile, TileDirection.NORTH, mountain, army));
	}

	// An army with no members has no lead member either, so it too reads only its
	// own record (the no-member path leaves the running value at -1 and
	// 0x5bc838 sends the caller to the own-type-only exit).
	[Fact]
	public void AnEmptyArmyFallsBackToItsOwnType() {
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		var mountain = AddNeighborsAndUpdateMap(startTile, MakeMountainTile(), TileDirection.NORTH);

		MapUnit army = MakeArmyUnit(MakePlayer(), startTile);

		Assert.Empty(army.Members());
		Assert.Null(army.LeadMemberType());
		Assert.False(army.IgnoresMovementCostOf(mountain.overlayTerrainType));
		Assert.Equal(3.0, TilePath.GetMovementCost(army.owner, startTile, TileDirection.NORTH, mountain, army));
	}

	private static MapUnit MakeArmyUnit(Player owner, Tile location) {
		UnitPrototype armyType = new UnitPrototype { name = "TestArmy", movement = 1, capacity = 3 };
		armyType.flags.Add(SaveUnitPrototype.Flag.Army);
		armyType.categories.Add("Land");

		// A distinct id: the loading path keys membership on the container's id,
		// and ID.None("") would make two bare units look like the same container.
		MapUnit army = new(ID.None("army")) { unitType = armyType, owner = owner, location = location };
		GiveExperience(army);
		location.unitsOnTile.Add(army);
		return army;
	}

	// Loading and the hit-point arithmetic read the experience level, which the
	// bare MapBase helpers do not set.
	private static void GiveExperience(MapUnit unit) {
		unit.experienceLevel = new ExperienceLevel("Regular", "Regular", 3, 0.5, 0.25);
		unit.experienceLevelKey = "Regular";
		unit.hitPointsRemaining = unit.maxHitPoints;
	}

	[Fact]
	public void AnIgnoredHillStepCostsOneMovementPoint() {
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		var hill = AddNeighborsAndUpdateMap(startTile, MakeHillTile(), TileDirection.NORTH);

		MapUnit unit = MakeMountainIgnoringUnit();
		unit.location = startTile;

		// Hills cost 2, so the rule is visible on them too.
		Assert.Equal(1.0, TilePath.GetMovementCost(unit.owner, startTile, TileDirection.NORTH, hill, unit));
	}

	[Fact]
	public void IgnoringTheTerrainDoesNotOverrideARoadDiscount() {
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		startTile.overlays.Add(road);
		var mountain = AddNeighborsAndUpdateMap(startTile, MakeMountainTile(), TileDirection.NORTH);
		mountain.overlays.Add(road);

		MapUnit unit = MakeMountainIgnoringUnit();
		unit.location = startTile;

		// The road/railroad branch runs first: a roaded mountain still costs the
		// road cost, which is cheaper than the ignored terrain's one point.
		Assert.Equal(1.0 / 3.0, TilePath.GetMovementCost(unit.owner, startTile, TileDirection.NORTH, mountain, unit), Tolerance);
	}

	[Fact]
	public void ARailroadStepStaysFreeForAUnitThatIgnoresTheTerrain() {
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		startTile.overlays.Add(railroad);
		var mountain = AddNeighborsAndUpdateMap(startTile, MakeMountainTile(), TileDirection.NORTH);
		mountain.overlays.Add(railroad);

		MapUnit unit = MakeMountainIgnoringUnit();
		unit.location = startTile;

		Assert.Equal(0.0, TilePath.GetMovementCost(unit.owner, startTile, TileDirection.NORTH, mountain, unit));
	}

	[Fact]
	public void IgnoringTheTerrainStillAppliesWhenARiverDeniesTheRoadDiscount() {
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		startTile.overlays.Add(road);
		// A river edge between the two roaded tiles, and no Bridge Building.
		startTile.riverNorth = true;
		var mountain = AddNeighborsAndUpdateMap(startTile, MakeMountainTile(), TileDirection.NORTH);
		mountain.overlays.Add(road);

		MapUnit unit = MakeMountainIgnoringUnit();
		unit.location = startTile;

		// The river denies the road discount; it does not deny the per-terrain
		// ignore rule, which the original applies to the same step.
		Assert.Equal(1.0, TilePath.GetMovementCost(unit.owner, startTile, TileDirection.NORTH, mountain, unit));
	}

	[Fact]
	public void AUnitThatIgnoresNothingPaysTheTerrainCostEvenWhenARiverDeniesTheRoad() {
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		startTile.overlays.Add(road);
		startTile.riverNorth = true;
		var mountain = AddNeighborsAndUpdateMap(startTile, MakeMountainTile(), TileDirection.NORTH);
		mountain.overlays.Add(road);

		MapUnit unit = MakeLandUnit(movementPoints: 2);
		unit.location = startTile;

		Assert.Equal(3.0, TilePath.GetMovementCost(unit.owner, startTile, TileDirection.NORTH, mountain, unit));
	}

	[Fact]
	public void TheIgnoreRuleIsChargedOnTheUnitThatMoves() {
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		var mountain = AddNeighborsAndUpdateMap(startTile, MakeMountainTile(), TileDirection.NORTH);

		MapUnit unit = MakeMountainIgnoringUnit();
		unit.location = startTile;
		unit.movementPoints.reset(2);

		// The unit starts with two movement points and pays one for the step,
		// so a second ignored step is still possible - unlike a normal unit,
		// which would spend everything on the first mountain.
		Assert.Equal(2.0, unit.movementPoints.remaining);
		unit.movementPoints.onUnitMove(TilePath.GetMovementCost(unit.owner, startTile, TileDirection.NORTH, mountain, unit));
		Assert.True(unit.movementPoints.canMove);
		Assert.Equal(1.0, unit.movementPoints.remaining);
	}
}
