using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using C7Engine;
using C7Engine.Lua;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using QueryCiv3;
using QueryCiv3.Biq;
using Xunit;

namespace EngineTests.GameData;

/// <summary>
/// The three sea-only movement bonuses of Civ3's get_max_move_points
/// (0x5cddf0), the frontier rows G12 and G13 of 11_movement.md §2.2:
///
///   +1  the civ owns a wonder with wonder-feature flag 0x8
///       (ITW_Plus_One_Ship_Movement)
///   +2  the civ owns a wonder with wonder-feature flag 0x4000
///       (ITW_Plus_Two_Ship_Movement)
///   +1  the civ's race has trait bit 7 (Seafaring)
///
/// All three sit behind the same two guards: the unit type's class field at
/// unit-type +0x9C must be 1 (sea), and the civ id must be greater than 0,
/// which excludes the barbarian player. The wonders are counted with
/// Leader_count_wonders_with_flag (0x55a8d0), which accepts only wonder-class
/// buildings and skips a wonder whose obsoleting advance the leader already
/// knows.
///
/// Measured from the shipped data: conquests.biq sets 0x8 on the Great
/// Lighthouse and on Magellan's Voyage, and sets 0x4000 on nothing at all (the
/// Civilopedia text for both says "increased by one"). The shipped Conquests do
/// use 0x4000 - the Norse Saga, the Navigation School and the Caracol
/// Observatory, the last of which carries both bits and is a small wonder.
/// </summary>
public class SeaMovementBonusTest : IClassFixture<SaveGameFixture> {
	private readonly SaveGameFixture fixture;
	private readonly C7GameData.GameData gd;

	public SeaMovementBonusTest(SaveGameFixture fixture) {
		this.fixture = fixture;
		gd = fixture.saveGame.ToGameData(fixture.behaviors);
		EngineStorage.InitializeGameDataForTests(gd);
		EngineStorage.animationsEnabled = false;
	}

	// ---------- helpers ----------

	private static Player MakePlayer(C7GameData.GameData data, bool seafaring = false, bool barbarian = false) {
		Civilization civ = new Civilization("TestCiv") {
			noun = "TestCivs",
			adjective = "Test",
			isBarbarian = barbarian,
		};
		civ.traits = [];
		if (seafaring) {
			civ.traits.Add(Civilization.Trait.Seafaring);
		}

		return new Player {
			id = data.GenerateID("player"),
			civilization = civ,
			rules = data.rules,
			government = new Government(),
		};
	}

	private static City MakeCity(C7GameData.GameData data, Player owner) {
		Tile tile = new Tile(data.GenerateID("tile")) {
			overlayTerrainType = new TerrainType { Key = "test" },
		};
		City city = new City(tile, owner, "TestCity", data.GenerateID("city"));
		city.residents.Add(new CityResident());
		tile.cityAtTile = city;
		owner.cities.Add(city);
		data.cities.Add(city);
		return city;
	}

	// A building made from a SaveBuilding the way the importer makes one, so the
	// flags go through exactly the path a real building takes. A non-wonder has
	// no greatWonderProperties and is not a small wonder, which is what the
	// binary's wonder-class gate at 0x55a930 rejects.
	private static Building MakeBuilding(C7GameData.GameData data, string name,
			bool greatWonder = false, bool smallWonder = false, Tech obsoletedBy = null) {
		SaveBuilding saveBuilding = new() {
			name = name,
			isSmallWonder = smallWonder,
			greatWonderProperties = greatWonder ? new SaveBuilding.GreatWonderProperties() : null,
		};
		Building building = new(saveBuilding, data);
		building.renderedObsoleteBy = obsoletedBy;
		return building;
	}

	private static MapUnit MakeUnit(C7GameData.GameData data, Player owner, int movement, bool sea) {
		UnitPrototype proto = new UnitPrototype {
			name = $"{movement}-{(sea ? "sea" : "land")}",
			movement = movement,
		};
		proto.categories.Add(sea ? "Sea" : "Land");
		return new MapUnit(data.GenerateID("unit")) {
			unitType = proto,
			owner = owner,
		};
	}

	// ---------- the three bonuses ----------

	[Fact]
	public void ThePlusOneWonderGivesASeaUnitOneMoreMovementPoint() {
		Player player = MakePlayer(gd);
		City city = MakeCity(gd, player);

		Building lighthouse = MakeBuilding(gd, "The Great Lighthouse", greatWonder: true);
		lighthouse.plusOneShipMovement = true;
		city.AddBuilding(lighthouse);

		Assert.Equal(4.0, MakeUnit(gd, player, movement: 3, sea: true).MaxMovementPoints, 0.0001);
	}

	[Fact]
	public void ThePlusTwoWonderGivesASeaUnitTwoMoreMovementPoints() {
		Player player = MakePlayer(gd);
		City city = MakeCity(gd, player);

		Building school = MakeBuilding(gd, "Navigation School", smallWonder: true);
		school.plusTwoShipMovement = true;
		city.AddBuilding(school);

		Assert.Equal(5.0, MakeUnit(gd, player, movement: 3, sea: true).MaxMovementPoints, 0.0001);
	}

	[Fact]
	public void BothWonderFlagsOnOneBuildingGiveThreeMoreMovementPoints() {
		// The Caracol Observatory in the Mesoamerica Conquest carries both bits,
		// so one building can be worth three points.
		Player player = MakePlayer(gd);
		City city = MakeCity(gd, player);

		Building observatory = MakeBuilding(gd, "Caracol Observatory", smallWonder: true);
		observatory.plusOneShipMovement = true;
		observatory.plusTwoShipMovement = true;
		city.AddBuilding(observatory);

		Assert.Equal(6.0, MakeUnit(gd, player, movement: 3, sea: true).MaxMovementPoints, 0.0001);
	}

	[Fact]
	public void TheSeafaringTraitGivesASeaUnitOneMoreMovementPoint() {
		Player seafaring = MakePlayer(gd, seafaring: true);
		City city = MakeCity(gd, seafaring);

		Assert.Equal(4.0, MakeUnit(gd, seafaring, movement: 3, sea: true).MaxMovementPoints, 0.0001);

		// The trait is worth one point even when the civ owns no wonder, and a
		// civ with a different trait gets nothing.
		Player other = MakePlayer(gd);
		MakeCity(gd, other);
		Assert.Equal(3.0, MakeUnit(gd, other, movement: 3, sea: true).MaxMovementPoints, 0.0001);
	}

	[Fact]
	public void AllThreeBonusesStackToFourMoreMovementPoints() {
		Player player = MakePlayer(gd, seafaring: true);
		City city = MakeCity(gd, player);

		Building lighthouse = MakeBuilding(gd, "The Great Lighthouse", greatWonder: true);
		lighthouse.plusOneShipMovement = true;
		city.AddBuilding(lighthouse);

		Building school = MakeBuilding(gd, "Navigation School", smallWonder: true);
		school.plusTwoShipMovement = true;
		city.AddBuilding(school);

		Assert.Equal(7.0, MakeUnit(gd, player, movement: 3, sea: true).MaxMovementPoints, 0.0001);
	}

	// ---------- the two guards ----------

	[Fact]
	public void ALandUnitGetsNoneOfTheSeaBonuses() {
		// Unit_Class at unit-type +0x9C gates all three bonuses, so a land unit
		// with a Seafaring civ and both wonders still moves at its own rate.
		Player player = MakePlayer(gd, seafaring: true);
		City city = MakeCity(gd, player);

		Building lighthouse = MakeBuilding(gd, "The Great Lighthouse", greatWonder: true);
		lighthouse.plusOneShipMovement = true;
		city.AddBuilding(lighthouse);

		Building school = MakeBuilding(gd, "Navigation School", smallWonder: true);
		school.plusTwoShipMovement = true;
		city.AddBuilding(school);

		Assert.Equal(3.0, MakeUnit(gd, player, movement: 3, sea: false).MaxMovementPoints, 0.0001);
	}

	[Fact]
	public void AnAirUnitGetsNoneOfTheSeaBonuses() {
		// Unit_Class 2 is air, not sea, so the == 1 test refuses it.
		Player player = MakePlayer(gd, seafaring: true);
		City city = MakeCity(gd, player);

		Building lighthouse = MakeBuilding(gd, "The Great Lighthouse", greatWonder: true);
		lighthouse.plusOneShipMovement = true;
		city.AddBuilding(lighthouse);

		UnitPrototype air = new UnitPrototype { name = "air", movement = 3 };
		air.categories.Add("Air");
		MapUnit unit = new MapUnit(gd.GenerateID("unit")) { unitType = air, owner = player };

		Assert.Equal(3.0, unit.MaxMovementPoints, 0.0001);
	}

	[Fact]
	public void TheBarbarianPlayerGetsNoneOfTheSeaBonuses() {
		// civId > 0 excludes the barbarian player, who is civ 0.
		Player barbarian = MakePlayer(gd, seafaring: true, barbarian: true);
		City city = MakeCity(gd, barbarian);

		Building lighthouse = MakeBuilding(gd, "The Great Lighthouse", greatWonder: true);
		lighthouse.plusOneShipMovement = true;
		city.AddBuilding(lighthouse);

		Building school = MakeBuilding(gd, "Navigation School", smallWonder: true);
		school.plusTwoShipMovement = true;
		city.AddBuilding(school);

		Assert.True(barbarian.isBarbarians);
		Assert.Equal(3.0, MakeUnit(gd, barbarian, movement: 3, sea: true).MaxMovementPoints, 0.0001);
	}

	[Fact]
	public void AUnitWithNoOwningCivilizationGetsNoneOfTheSeaBonuses() {
		Player ownerless = new Player {
			id = gd.GenerateID("player"),
			rules = gd.rules,
		};

		Assert.Null(ownerless.civilization);
		Assert.Equal(3.0, MakeUnit(gd, ownerless, movement: 3, sea: true).MaxMovementPoints, 0.0001);
	}

	// ---------- the wonder-class and obsolescence gates ----------

	[Fact]
	public void ANonWonderBuildingWithTheFlagDoesNotCount() {
		// Characteristics bit 0x4 (Wonder) or 0x8 (Small Wonder) is required at
		// 0x55a930, so the flag on an ordinary improvement does nothing.
		Player player = MakePlayer(gd);
		City city = MakeCity(gd, player);

		Building harbour = MakeBuilding(gd, "Harbor");
		harbour.plusOneShipMovement = true;
		harbour.plusTwoShipMovement = true;
		city.AddBuilding(harbour);

		Assert.Equal(3.0, MakeUnit(gd, player, movement: 3, sea: true).MaxMovementPoints, 0.0001);
	}

	[Fact]
	public void AnObsoleteWonderStopsGivingTheBonus() {
		// Leader_count_wonders_with_flag skips a wonder whose obsoleting advance
		// the leader already knows (0x55a952-0x55a97e). The shipped Great
		// Lighthouse is obsoleted by tech-42.
		Player player = MakePlayer(gd);
		City city = MakeCity(gd, player);

		Tech magnetism = new Tech {
			id = gd.GenerateID("tech"),
			Name = "Magnetism",
		};
		Building lighthouse = MakeBuilding(gd, "The Great Lighthouse", greatWonder: true, obsoletedBy: magnetism);
		lighthouse.plusOneShipMovement = true;
		city.AddBuilding(lighthouse);

		MapUnit unit = MakeUnit(gd, player, movement: 3, sea: true);
		Assert.Equal(4.0, unit.MaxMovementPoints, 0.0001);

		player.knownTechs.Add(magnetism.id);
		Assert.Equal(3.0, unit.MaxMovementPoints, 0.0001);
	}

	// ---------- the movement scale ----------

	[Fact]
	public void TheSeaBonusIsOneWholeMovementPointAtAnyScale() {
		// The binary adds MovementAlongRoads internal units per point, so the
		// bonus is a whole movement point at any scale. Adding a raw internal
		// unit instead would make it 1 / scale of a point. 0x5cddf0 multiplies
		// both the base and the bonuses by the scale and the boundary divides
		// once, so the ratio is what is invariant (11_movement.md §2.1, §2.2).
		foreach (int scale in new[] { 3, 6, 2 }) {
			Player player = MakePlayer(gd);
			player.rules = new Rules { MovementAlongRoads = scale };
			City city = MakeCity(gd, player);

			Building school = MakeBuilding(gd, "Navigation School", smallWonder: true);
			school.plusTwoShipMovement = true;
			city.AddBuilding(school);

			MapUnit unit = MakeUnit(gd, player, movement: 3, sea: true);
			Assert.Equal(5.0, unit.MaxMovementPoints, 0.0001);
		}
	}

	// ---------- both paths: the shipped ruleset ----------

	[Fact]
	public void TheShippedRulesetCarriesTheTwoShipMovementWonderFlags() {
		// The BIQ-independent half of the both-paths rule: C7/Lua/civ3/
		// ruleset.json is what a game with no BIQ (and the SaveGameFixture) reads.
		// Measured carriers: 0x8 on the Great Lighthouse and Magellan's Voyage,
		// 0x4000 on nothing.
		JsonDocument ruleset = JsonUtils.LoadBaseRuleset();

		Assert.Equal(new List<string> { "Magellan's Voyage", "The Great Lighthouse" },
			NamesWithFlag(ruleset.RootElement.GetProperty("buildings"), "plusOneShipMovement"));
		Assert.Empty(NamesWithFlag(ruleset.RootElement.GetProperty("buildings"), "plusTwoShipMovement"));
	}

	[Fact]
	public void TheShippedRulesetGamesSeeTheFlagOnTheImportedBuildings() {
		// The flag has to reach the runtime Building, or the rule never fires.
		Assert.True(gd.Buildings.Single(b => b.name == "The Great Lighthouse").plusOneShipMovement);
		Assert.True(gd.Buildings.Single(b => b.name == "Magellan's Voyage").plusOneShipMovement);
		Assert.False(gd.Buildings.Single(b => b.name == "The Great Lighthouse").plusTwoShipMovement);
		Assert.DoesNotContain(gd.Buildings, b => b.plusTwoShipMovement);

		// And nothing else in the shipped rules carries the +1 flag.
		Assert.Equal(new List<string> { "Magellan's Voyage", "The Great Lighthouse" },
			gd.Buildings.Where(b => b.plusOneShipMovement).Select(b => b.name).OrderBy(n => n).ToList());
	}

	[Fact]
	public void TheShippedGreatLighthouseGivesASeaUnitOneMoreMovementPoint() {
		// End to end through the shipped rules: the real Building out of
		// gd.Buildings, put in a city, changes the unit's computed maximum.
		Player player = MakePlayer(gd);
		City city = MakeCity(gd, player);
		city.AddBuilding(gd.Buildings.Single(b => b.name == "The Great Lighthouse"));

		MapUnit sea = MakeUnit(gd, player, movement: 3, sea: true);
		MapUnit land = MakeUnit(gd, player, movement: 3, sea: false);

		Assert.Equal(4.0, sea.MaxMovementPoints, 0.0001);
		Assert.Equal(3.0, land.MaxMovementPoints, 0.0001);
	}

	[Fact]
	public void TheShippedMagellansVoyageGivesASeaUnitOneMoreMovementPoint() {
		// The measurement that corrects 11_movement.md §8.5: in the shipped
		// conquests.biq Magellan's Voyage carries 0x8, the same +1 as the Great
		// Lighthouse, not 0x4000. Its Civilopedia text says the same thing the
		// Great Lighthouse's does: "The movement rate of all naval units is
		// increased by one."
		Player player = MakePlayer(gd);
		City city = MakeCity(gd, player);
		city.AddBuilding(gd.Buildings.Single(b => b.name == "Magellan's Voyage"));

		Assert.Equal(4.0, MakeUnit(gd, player, movement: 3, sea: true).MaxMovementPoints, 0.0001);
	}

	[Fact]
	public void TwoWondersThatCarryTheSameFlagStillGiveOnlyOnePoint() {
		// 0x5cddf0 adds a flat 1 * MovementAlongRoads when the count of
		// flag-carrying wonders is greater than zero (0x5cde39-0x5cde3d), so
		// the flag is a boolean, not a per-wonder total. Owning both of the
		// shipped +1 ship movement wonders is therefore +1, not +2.
		Player player = MakePlayer(gd);
		City city = MakeCity(gd, player);
		city.AddBuilding(gd.Buildings.Single(b => b.name == "Magellan's Voyage"));
		city.AddBuilding(gd.Buildings.Single(b => b.name == "The Great Lighthouse"));

		Assert.Equal(4.0, MakeUnit(gd, player, movement: 3, sea: true).MaxMovementPoints, 0.0001);

		// And a second copy of an already-present flag changes nothing. Two
		// different flags, by contrast, do each apply their own amount, which
		// is what the Caracol Observatory test above covers.
		Building another = MakeBuilding(gd, "Another Lighthouse", greatWonder: true);
		another.plusOneShipMovement = true;
		city.AddBuilding(another);
		Assert.Equal(4.0, MakeUnit(gd, player, movement: 3, sea: true).MaxMovementPoints, 0.0001);
	}

	// ---------- both paths: the BIQ ----------

	[SkippableFact]
	public void TheShippedConquestsBiqCarriesTheShipMovementWonderFlags() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), SkipReason);

		BiqData biq = BiqData.LoadFile(PathUtils.defaultBicPath);

		// BLDG wonder-feature word bit 0x8, as measured out of the shipped file.
		Assert.Equal(new List<string> { "Magellan's Voyage", "The Great Lighthouse" },
			biq.Bldg.Where(b => b.IncreasedShipMovement).Select(b => b.Name).OrderBy(n => n).ToList());

		// Nothing in the base rules carries 0x4000, even though the engine has
		// the branch.
		Assert.Empty(biq.Bldg.Where(b => b.PlusTwoShipMovement).Select(b => b.Name));
	}

	[SkippableFact]
	public void AShippedConquestsScenarioUsesThePlusTwoShipMovementFlag() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), SkipReason);

		BiqData biq = BiqData.LoadFile(ScenarioPath("5 Mesoamerica.biq"));

		BLDG caracol = biq.Bldg.Single(b => b.Name == "Caracol Observatory");

		// Both bits on one small wonder: +1 and +2, three points in total.
		Assert.True(caracol.SmallWonder);
		Assert.True(caracol.IncreasedShipMovement);
		Assert.True(caracol.PlusTwoShipMovement);
	}

	[SkippableFact]
	public void ImportBiqCarriesTheTwoShipMovementWonderFlags() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), SkipReason);

		// A scenario with a map runs the whole ImportBiq path, so the two field
		// assignments in ImportCiv3's building flag map are covered by the real
		// importer rather than by a hand-built SaveBuilding.
		EngineStorage.animationsEnabled = false;
		SaveGame imported = ImportCiv3.ImportBiq(ScenarioPath("5 Mesoamerica.biq"),
			PathUtils.defaultBicPath, GetPediaIconsPath("Conquests/Conquests"));

		SaveBuilding caracol = imported.Buildings.Single(b => b.name == "Caracol Observatory");
		Assert.Contains(SaveBuilding.Flag.PlusOneShipMovement, caracol.flags);
		Assert.Contains(SaveBuilding.Flag.PlusTwoShipMovement, caracol.flags);

		// A building in the same file without the bits keeps them off.
		SaveBuilding barracks = imported.Buildings.Single(b => b.name == "Barracks");
		Assert.DoesNotContain(SaveBuilding.Flag.PlusOneShipMovement, barracks.flags);
		Assert.DoesNotContain(SaveBuilding.Flag.PlusTwoShipMovement, barracks.flags);
	}

	[SkippableFact]
	public void AnImportedScenarioWonderGivesASeaUnitThreeMoreMovementPoints() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), SkipReason);

		// The real import path end to end: the BIQ's BLDG table, through
		// QueryCiv3 and ImportCiv3, through SaveBuilding and Building, into the
		// movement arithmetic. Caracol Observatory carries 0x8 and 0x4000, so a
		// sea unit of its owner moves three points further than its type says.
		EngineStorage.animationsEnabled = false;
		C7GameData.GameData imported = ImportCiv3.ImportBiq(ScenarioPath("5 Mesoamerica.biq"),
			PathUtils.defaultBicPath, GetPediaIconsPath("Conquests/Conquests"))
			.ToGameData(fixture.behaviors);

		try {
			Building caracol = imported.Buildings.Single(b => b.name == "Caracol Observatory");
			Assert.True(caracol.plusOneShipMovement);
			Assert.True(caracol.plusTwoShipMovement);

			// A civ with no Seafaring trait, so only the two wonder flags can
			// move the number.
			Player player = MakePlayer(imported);
			player.civilization.traits = [Civilization.Trait.Militaristic];
			City city = MakeCity(imported, player);
			city.AddBuilding(caracol);

			Assert.Equal(6.0, MakeUnit(imported, player, movement: 3, sea: true).MaxMovementPoints, 0.0001);
			Assert.Equal(3.0, MakeUnit(imported, player, movement: 3, sea: false).MaxMovementPoints, 0.0001);
		} finally {
			// The import replaced the global game data; put the fixture's back so
			// the rest of this test's neighbours are unaffected.
			EngineStorage.InitializeGameDataForTests(gd);
		}
	}

	// ---------- helpers for the data tests ----------

	private const string SkipReason = "No Civ3 install found.";

	private static string ScenarioPath(string fileName) {
		return Path.Combine(Civ3Location.GetCiv3Path(), "Conquests", "Conquests", fileName);
	}

	private static Func<string, string> GetPediaIconsPath(string subfolder) {
		return (relativeModPath) => {
			if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
				relativeModPath = relativeModPath.Replace("\\conquests\\", "/Conquests/");
			}

			return Path.GetFullPath(Path.Combine(
				Civ3Location.GetCiv3Path(), subfolder, relativeModPath, "Text", "PediaIcons.txt"));
		};
	}

	private static List<string> NamesWithFlag(JsonElement entries, string flag) {
		List<string> result = new();
		foreach (JsonElement entry in entries.EnumerateArray()) {
			if (!entry.TryGetProperty("flags", out JsonElement flags)) {
				continue;
			}
			if (flags.EnumerateArray().Any(f => f.GetString() == flag)) {
				result.Add(entry.GetProperty("name").GetString());
			}
		}
		result.Sort();
		return result;
	}
}
