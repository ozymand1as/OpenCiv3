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
/// Guards the rules data the zone-of-control rule reads (11_movement.md §6):
/// PRTO.ZoneOfControl, the cruise-missile ability and BLDG.NavalPower. Each of
/// them must exist on both the BIQ import path and in the ruleset a generated
/// game reads, so the tests below pin the ruleset against the shipped BIQ.
/// </summary>
public class ZoneOfControlDataTest : IClassFixture<SaveGameFixture> {
	private readonly SaveGameFixture fixture;
	private readonly SaveGame save;

	public ZoneOfControlDataTest(SaveGameFixture fixture) {
		this.fixture = fixture;
		save = fixture.saveGame;
	}

	// The 14 distinct names the shipped conquests.biq marks with ZoneOfControl
	// (record +0x04). Sixteen of its 141 records carry it; Mech Infantry and
	// Conquistador appear twice, once per unique unit.
	private static readonly string[] ShippedCarriers = {
		"AEGIS Cruiser", "Army", "Cavalry", "Conquistador", "Cossack", "Keshik",
		"Mech Infantry", "Modern Armor", "Modern Paratrooper", "Panzer",
		"Paratrooper", "Radar Artillery", "Sipahi", "Tank",
	};

	[Fact]
	public void TheRulesetCarriesTheZoneOfControlFlagOnExactlyTheShippedCarriers() {
		string[] carriers = save.UnitPrototypes
			.Where(p => p.flags.Contains(SaveUnitPrototype.Flag.ZoneOfControl))
			.Select(p => p.name)
			.OrderBy(n => n)
			.ToArray();

		Assert.Equal(ShippedCarriers.OrderBy(n => n).ToArray(), carriers);

		// The Army has no attack or bombard strength of its own, so the flag is
		// what makes it a zone-of-control projector; ordinary combat units do
		// not carry it.
		Assert.Contains("Army", carriers);
		Assert.DoesNotContain("Warrior", carriers);
		Assert.DoesNotContain("Settler", carriers);
	}

	[Fact]
	public void TheRulesetCarriesTheCruiseMissileAbilityOnlyOnTheCruiseMissile() {
		string[] carriers = save.UnitPrototypes
			.Where(p => p.flags.Contains(SaveUnitPrototype.Flag.CruiseMissile))
			.Select(p => p.name)
			.ToArray();

		Assert.Equal(new[] { "Cruise Missile" }, carriers);

		// The ability only matters because the type has a bombard strength to
		// zero (11_movement.md §6.2).
		SaveUnitPrototype cruise = save.UnitPrototypes.First(p => p.name == "Cruise Missile");
		Assert.True(cruise.bombard > 0);
		Assert.False(cruise.flags.Contains(SaveUnitPrototype.Flag.ZoneOfControl));
	}

	[Fact]
	public void TheRulesetGivesOnlyTheCoastalFortressNavalPower() {
		Assert.Equal(8, save.Buildings.First(b => b.name == "Coastal Fortress").navalPower);
		Assert.All(save.Buildings.Where(b => b.name != "Coastal Fortress"),
			b => Assert.Equal(0, b.navalPower));
	}

	// The ruleset path reaching the running game, so a dropped field in
	// UnitPrototype or Building is caught as well as a missing JSON key.
	[Fact]
	public void TheRulesetPathReachesTheRuntimeObjects() {
		C7GameData.GameData gameData = fixture.saveGame.ToGameData(fixture.behaviors);

		Assert.True(gameData.unitPrototypes.First(p => p.name == "Tank").projectsZoneOfControl);
		Assert.True(gameData.unitPrototypes.First(p => p.name == "Army").projectsZoneOfControl);
		Assert.False(gameData.unitPrototypes.First(p => p.name == "Warrior").projectsZoneOfControl);
		Assert.True(gameData.unitPrototypes.First(p => p.name == "Cruise Missile").isCruiseMissile);
		Assert.False(gameData.unitPrototypes.First(p => p.name == "Tank").isCruiseMissile);
		Assert.Equal(8, gameData.Buildings.First(b => b.name == "Coastal Fortress").navalPower);
	}

	// The same values, read back out of the shipped BIQ with QueryCiv3 and run
	// through the BIQ import helper, so the ruleset copy cannot drift from the
	// data the original engine consumes.
	[SkippableFact]
	public void TheShippedBiqAndTheRulesetAgree() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");

		BiqData biq = BiqData.LoadFile(PathUtils.defaultBicPath);

		// 16 of the 141 unit records set the flag; deduplicated by name that is
		// the 14 carriers the ruleset lists.
		Assert.Equal(141, biq.Prto.Length);
		Assert.Equal(16, biq.Prto.Count(p => p.ZoneOfControl != 0));
		Assert.Equal(ShippedCarriers.OrderBy(n => n).ToArray(),
			biq.Prto.Where(p => p.ZoneOfControl != 0).Select(p => p.Name).Distinct().OrderBy(n => n).ToArray());

		// The import helper maps every record to exactly the flags the ruleset
		// carries for the same name.
		foreach (PRTO prto in biq.Prto) {
			HashSet<SaveUnitPrototype.Flag> imported = ImportCiv3.GetZoneOfControlFlags(prto).ToHashSet();
			HashSet<SaveUnitPrototype.Flag> fromRuleset = save.UnitPrototypes.First(p => p.name == prto.Name).flags;

			Assert.Equal(imported.Contains(SaveUnitPrototype.Flag.ZoneOfControl),
				fromRuleset.Contains(SaveUnitPrototype.Flag.ZoneOfControl));
			Assert.Equal(imported.Contains(SaveUnitPrototype.Flag.CruiseMissile),
				fromRuleset.Contains(SaveUnitPrototype.Flag.CruiseMissile));
		}

		// The one record whose bombard term the ability zeroes: it carries the
		// ability, has a bombard strength, and is not itself a projector.
		PRTO cruise = biq.Prto.First(p => p.Name == "Cruise Missile");
		Assert.True(cruise.CruiseMissile);
		Assert.True(cruise.BombardStrength > 0);
		Assert.Equal(0, cruise.ZoneOfControl);

		// And the only building the city threat reads.
		Assert.Equal(8, biq.Bldg.First(b => b.Name == "Coastal Fortress").NavalPower);
		Assert.All(biq.Bldg.Where(b => b.Name != "Coastal Fortress"),
			b => Assert.Equal(0, b.NavalPower));
	}
}

/// <summary>
/// The zone-of-control rule itself (11_movement.md §6): the escape formula, the
/// threat scan's candidate rules and the three position exemptions, the guards,
/// and where the rule fires relative to a refused step.
/// </summary>
public sealed class ZoneOfControlTest : IClassFixture<SaveGameFixture> {
	// The step used throughout: from (50, 50) east to (52, 50). The two tiles
	// adjacent to both ends of that step are (51, 49) and (51, 51), which is the
	// scan's whole search space for this step.
	private const int SourceX = 50;
	private const int SourceY = 50;
	private const int DestinationX = 52;
	private const int DestinationY = 50;
	private const int BandX = 51;
	private const int BandY = 49;
	private const int SouthBandY = 51;

	private readonly C7GameData.GameData gd;

	public ZoneOfControlTest(SaveGameFixture fixture) {
		gd = fixture.saveGame.ToGameData(fixture.behaviors);
		EngineStorage.InitializeGameDataForTests(gd);
		// Move waits on an animation-complete message no test UI sends.
		EngineStorage.animationsEnabled = false;
	}

	private class ScriptedRandom : Random {
		public readonly Queue<int> results = new();
		public readonly List<int> requestedBounds = new();
		public int fallback;

		public override int Next(int maxValue) {
			requestedBounds.Add(maxValue);
			return (results.Count > 0 ? results.Dequeue() : fallback) % maxValue;
		}

		public override double NextDouble() => 0.5;
	}

	private ScriptedRandom UseScriptedRandom(int roll) {
		ScriptedRandom rng = new ScriptedRandom { fallback = roll };
		C7GameData.GameData.rng = rng;
		return rng;
	}

	// ---------- helpers ----------

	private Player MakePlayer(bool barbarian = false) {
		Civilization civ = new Civilization(barbarian ? "Barbarians" : "TestCiv") {
			noun = "TestCivs",
			adjective = "Test",
			isBarbarian = barbarian,
		};
		civ.traits = [];
		return new Player {
			id = gd.GenerateID("player"),
			civilization = civ,
			rules = gd.rules,
			government = new Government(),
		};
	}

	// A flat, featureless tile of the fixture's real map, emptied of everything
	// the generator may have left on it.
	private Tile CleanTile(int x, int y) {
		Tile tile = gd.map.tileAt(x, y);
		if (tile.cityAtTile != null) {
			tile.cityAtTile = null;   // clears the overlays as a side effect
		}
		tile.unitsOnTile.Clear();
		tile.overlays.Clear();
		tile.hasGoodyHut = false;
		tile.hasBarbarianCamp = false;
		tile.overlayTerrainType = new TerrainType { Key = "test" };
		tile.baseTerrainType = tile.overlayTerrainType;
		return tile;
	}

	private Tile CleanWaterTile(int x, int y) {
		Tile tile = CleanTile(x, y);
		tile.baseTerrainType = new TerrainType { Key = "coast" };
		tile.overlayTerrainType = tile.baseTerrainType;
		return tile;
	}

	// The source, the destination and both tiles of the scan's band, so nothing
	// the generator left behind can add an unexpected threat.
	private (Tile source, Tile destination, Tile band) PrepareLandStep() {
		Tile source = CleanTile(SourceX, SourceY);
		Tile destination = CleanTile(DestinationX, DestinationY);
		CleanTile(BandX, SouthBandY);
		return (source, destination, CleanTile(BandX, BandY));
	}

	private (Tile source, Tile destination, Tile band) PrepareWaterStep() {
		Tile source = CleanWaterTile(SourceX, SourceY);
		Tile destination = CleanWaterTile(DestinationX, DestinationY);
		CleanWaterTile(BandX, SouthBandY);
		return (source, destination, CleanWaterTile(BandX, BandY));
	}

	private MapUnit MakeUnit(Player owner, UnitPrototype proto, Tile tile, int hitPoints = 3) {
		MapUnit unit = proto.GetInstance(gd.GenerateID(proto.name), proto, owner, location: tile, hitPoints: hitPoints);
		unit.experienceLevel = gd.experienceLevels.First(e => e.key == "Regular");
		unit.experienceLevelKey = "Regular";
		unit.hitPointsRemaining = hitPoints;
		tile.unitsOnTile.Add(unit);
		gd.mapUnits.Add(unit);
		owner.AddUnit(unit);
		return unit;
	}

	private static UnitPrototype MakeLandPrototype(int attack, int defense, bool zoneOfControl,
			int bombard = 0, bool cruiseMissile = false, int movement = 2) {
		UnitPrototype proto = new UnitPrototype {
			name = $"TestLand-{attack}-{defense}-{zoneOfControl}-{bombard}-{cruiseMissile}",
			attack = attack,
			defense = defense,
			bombard = bombard,
			movement = movement,
		};
		proto.categories.Add("Land");
		if (zoneOfControl) proto.flags.Add(SaveUnitPrototype.Flag.ZoneOfControl);
		if (cruiseMissile) proto.flags.Add(SaveUnitPrototype.Flag.CruiseMissile);
		return proto;
	}

	private static UnitPrototype MakeSeaPrototype(int attack, int defense, bool zoneOfControl, int movement = 2) {
		UnitPrototype proto = new UnitPrototype {
			name = $"TestSea-{attack}-{defense}-{zoneOfControl}",
			attack = attack,
			defense = defense,
			movement = movement,
		};
		proto.categories.Add("Sea");
		if (zoneOfControl) proto.flags.Add(SaveUnitPrototype.Flag.ZoneOfControl);
		return proto;
	}

	// A player who is at war with the mover and has seen the mover's tile, which
	// is what a candidate needs before it may fire (11_movement.md §6.2). The
	// tile knowledge is granted explicitly so the visibility test below is about
	// the direction of the check, not about how a barbarian AI learns tiles.
	private Player MakeSeeingEnemy(MapUnit mover) {
		Player enemy = MakePlayer(barbarian: true);
		enemy.tileKnowledge.knownTiles.Add(mover.location);
		return enemy;
	}

	private City MakeCity(Player owner, Tile tile, int population, Building building = null) {
		City city = new City(tile, owner, "TestCity", gd.GenerateID("city"));
		for (int i = 0; i < population; i++) {
			city.residents.Add(new CityResident());
		}
		if (building != null) {
			city.AddBuilding(building);
		}
		tile.cityAtTile = city;
		owner.cities.Add(city);
		gd.cities.Add(city);
		return city;
	}

	// ---------- the escape formula (§6.3) ----------

	[Theory]
	// D = E is the "about 963 of 1024" case.
	[InlineData(1, 1, 963)]
	[InlineData(2, 2, 963)]
	[InlineData(10, 10, 963)]
	// The 16x weight against the threat.
	[InlineData(1, 10, 630)]
	[InlineData(1, 100, 141)]
	[InlineData(1, 1000, 16)]
	[InlineData(2, 32, 512)]
	// The clamp: an overwhelming mover cannot be certain and a hopeless one
	// still escapes once in 1024.
	[InlineData(100, 1, 1023)]
	[InlineData(4, 0, 1023)]
	[InlineData(1, 100000, 1)]
	public void TheEscapeChanceIsTheBinaryFormulaClampedTo1Through1023(int defence, int threat, int expected) {
		Assert.Equal(expected, MapUnit.ZoneOfControlEscapeChance(defence, threat));
	}

	[Fact]
	public void EqualDefenceAndThreatAlwaysEscape963TimesIn1024() {
		for (int strength = 1; strength <= 64; strength++) {
			Assert.Equal(963, MapUnit.ZoneOfControlEscapeChance(strength, strength));
		}
	}

	// ---------- the roll (§6.3) ----------

	[Fact]
	public void TheRollEscapesBelowTheChanceAndDamagesAtIt() {
		(Tile source, Tile destination, Tile band) = PrepareLandStep();
		MapUnit mover = MakeUnit(MakePlayer(), MakeLandPrototype(0, 1, zoneOfControl: false), source);
		MakeUnit(MakeSeeingEnemy(mover), MakeLandPrototype(1, 1, zoneOfControl: true), band);

		Assert.Equal(1, mover.DefenseStrength());
		Assert.Equal(1, mover.ZoneOfControlThreatStrength(destination));
		Assert.Equal(963, MapUnit.ZoneOfControlEscapeChance(1, 1));

		// One below the chance: the roll escapes.
		ScriptedRandom rng = UseScriptedRandom(962);
		Assert.False(mover.ApplyZoneOfControl(destination));
		Assert.Equal(3, mover.hitPointsRemaining);
		Assert.Equal(new List<int> { 1024 }, rng.requestedBounds);

		// Exactly the chance: it does not.
		rng.results.Enqueue(963);
		Assert.True(mover.ApplyZoneOfControl(destination));
		Assert.Equal(2, mover.hitPointsRemaining);
	}

	// ---------- the threat scan (§6.2) ----------

	[Fact]
	public void TheStrongestCandidateSetsTheThreat() {
		(Tile source, Tile destination, Tile band) = PrepareLandStep();
		MapUnit mover = MakeUnit(MakePlayer(), MakeLandPrototype(0, 1, zoneOfControl: false), source);
		Player enemy = MakeSeeingEnemy(mover);
		MapUnit weak = MakeUnit(enemy, MakeLandPrototype(1, 1, zoneOfControl: true), band);
		MapUnit strong = MakeUnit(enemy, MakeLandPrototype(10, 1, zoneOfControl: true), band);

		Assert.Equal(10, mover.ZoneOfControlThreatStrength(destination));

		// The weak candidate alone would have spared the mover: its escape
		// chance is 963, the strong one's is 630, and the roll below is 700.
		band.unitsOnTile.Remove(strong);
		Assert.Equal(1, mover.ZoneOfControlThreatStrength(destination));
		ScriptedRandom rng = UseScriptedRandom(700);
		Assert.False(mover.ApplyZoneOfControl(destination));
		Assert.Equal(3, mover.hitPointsRemaining);

		// Put the strong candidate back and the same roll now damages.
		band.unitsOnTile.Add(strong);
		Assert.Equal(10, mover.ZoneOfControlThreatStrength(destination));
		Assert.Equal(630, MapUnit.ZoneOfControlEscapeChance(1, 10));
		rng.results.Enqueue(700);
		Assert.True(mover.ApplyZoneOfControl(destination));
		Assert.Equal(2, mover.hitPointsRemaining);
		Assert.NotEmpty(band.unitsOnTile);
	}

	[Fact]
	public void ACandidateOfTheMoversOwnOwnerContributesNothing() {
		(Tile source, Tile destination, Tile band) = PrepareLandStep();
		Player moverOwner = MakePlayer();
		MapUnit mover = MakeUnit(moverOwner, MakeLandPrototype(0, 1, zoneOfControl: false), source);
		MakeUnit(moverOwner, MakeLandPrototype(10, 1, zoneOfControl: true), band);

		Assert.Equal(0, mover.ZoneOfControlThreatStrength(destination));
	}

	[Fact]
	public void ACandidateWhoseOwnerCannotSeeTheMoverContributesNothing() {
		(Tile source, Tile destination, Tile band) = PrepareLandStep();
		MapUnit mover = MakeUnit(MakePlayer(), MakeLandPrototype(0, 1, zoneOfControl: false), source);
		Player enemy = MakePlayer(barbarian: true);
		MakeUnit(enemy, MakeLandPrototype(10, 1, zoneOfControl: true), band);

		// The enemy has never seen the mover's tile, so the mover is invisible
		// to it and the candidate does not fire.
		Assert.False(enemy.HasExploredTile(mover.location));
		Assert.Equal(0, mover.ZoneOfControlThreatStrength(destination));

		// The test is the mover's visibility to the candidate's owner, so
		// teaching the *enemy* about the mover's tile is what turns the threat
		// on. What the mover knows is irrelevant.
		enemy.tileKnowledge.knownTiles.Add(mover.location);
		Assert.Equal(10, mover.ZoneOfControlThreatStrength(destination));
	}

	[Fact]
	public void ACruiseMissileContributesNoBombard() {
		(Tile source, Tile destination, Tile band) = PrepareLandStep();
		MapUnit mover = MakeUnit(MakePlayer(), MakeLandPrototype(0, 1, zoneOfControl: false), source);
		Player enemy = MakeSeeingEnemy(mover);

		// A bombard-only candidate fires with its bombard strength.
		MakeUnit(enemy, MakeLandPrototype(0, 1, zoneOfControl: true, bombard: 16), band);
		Assert.Equal(16, mover.ZoneOfControlThreatStrength(destination));

		// The same type with the cruise-missile ability contributes nothing: the
		// ability zeroes the bombard term and it has no attack strength.
		band.unitsOnTile.Clear();
		MakeUnit(enemy, MakeLandPrototype(0, 1, zoneOfControl: true, bombard: 16, cruiseMissile: true), band);
		Assert.Equal(0, mover.ZoneOfControlThreatStrength(destination));

		// The ability only zeroes the bombard term; an attack strength survives.
		band.unitsOnTile.Clear();
		MakeUnit(enemy, MakeLandPrototype(5, 1, zoneOfControl: true, bombard: 16, cruiseMissile: true), band);
		Assert.Equal(5, mover.ZoneOfControlThreatStrength(destination));
	}

	[Fact]
	public void ACandidateWithoutTheFlagAndWithoutABypassContributesNothing() {
		(Tile source, Tile destination, Tile band) = PrepareLandStep();
		MapUnit mover = MakeUnit(MakePlayer(), MakeLandPrototype(0, 1, zoneOfControl: false), source);
		MakeUnit(MakeSeeingEnemy(mover), MakeLandPrototype(10, 1, zoneOfControl: false), band);

		Assert.Equal(0, mover.ZoneOfControlThreatStrength(destination));
	}

	// ---------- the three position exemptions ----------

	[Fact]
	public void ACandidateCarriedInsideAnArmyNeedsNoFlag() {
		(Tile source, Tile destination, Tile band) = PrepareLandStep();
		MapUnit mover = MakeUnit(MakePlayer(), MakeLandPrototype(0, 1, zoneOfControl: false), source);
		Player enemy = MakeSeeingEnemy(mover);

		// An army type of its own, without the flag, so the member is the only
		// thing on the tile that can fire.
		UnitPrototype armyType = new UnitPrototype { name = "TestArmy", capacity = 3 };
		armyType.categories.Add("Land");
		armyType.flags.Add(SaveUnitPrototype.Flag.Army);

		MapUnit army = MakeUnit(enemy, armyType, band);
		MapUnit member = MakeUnit(enemy, MakeLandPrototype(4, 1, zoneOfControl: false), band);

		// On its own the member contributes nothing.
		Assert.False(member.IsCarriedInsideArmy());
		Assert.Equal(0, mover.ZoneOfControlThreatStrength(destination));

		Assert.True(member.LoadIntoArmy(army));
		Assert.True(member.IsCarriedInsideArmy());
		Assert.False(member.unitType.projectsZoneOfControl);
		Assert.Equal(4, mover.ZoneOfControlThreatStrength(destination));
	}

	[Fact]
	public void ACandidateOnAFortressNeedsNoFlag() {
		(Tile source, Tile destination, Tile band) = PrepareLandStep();
		MapUnit mover = MakeUnit(MakePlayer(), MakeLandPrototype(0, 1, zoneOfControl: false), source);
		MakeUnit(MakeSeeingEnemy(mover), MakeLandPrototype(6, 1, zoneOfControl: false), band);

		Assert.Equal(0, mover.ZoneOfControlThreatStrength(destination));

		band.overlays.Add(new TerrainImprovement(Tile.TileOverlays.FORTRESS, TerrainImprovement.Layer.Holdings));
		Assert.True(band.HasFortress());
		Assert.Equal(6, mover.ZoneOfControlThreatStrength(destination));

		// A barricade is a different holding, and it takes the same layer, so
		// the fortress is gone and the candidate is ordinary again.
		band.overlays.Add(new TerrainImprovement(Tile.TileOverlays.BARRICADE, TerrainImprovement.Layer.Holdings));
		Assert.False(band.HasFortress());
		Assert.Equal(0, mover.ZoneOfControlThreatStrength(destination));
	}

	[Fact]
	public void ALandCandidateInASmallWalledCityNeedsNoFlag() {
		(Tile source, Tile destination, Tile band) = PrepareLandStep();
		MapUnit mover = MakeUnit(MakePlayer(), MakeLandPrototype(0, 1, zoneOfControl: false), source);
		Player enemy = MakeSeeingEnemy(mover);
		Building walls = gd.Buildings.First(b => b.providesWalls);

		MakeUnit(enemy, MakeLandPrototype(7, 1, zoneOfControl: false), band);
		MakeCity(enemy, band, population: 6, building: walls);

		Assert.Equal(7, mover.ZoneOfControlThreatStrength(destination));

		// Without the walls the candidate is an ordinary un-flagged unit.
		band.cityAtTile.constructed_buildings.Clear();
		Assert.Equal(0, mover.ZoneOfControlThreatStrength(destination));

		// And the walls only count while the city is still a town.
		band.cityAtTile.AddBuilding(walls);
		Assert.Equal(7, mover.ZoneOfControlThreatStrength(destination));
		while (band.cityAtTile.residents.Count <= gd.rules.MaximumLevel1CitySize) {
			band.cityAtTile.residents.Add(new CityResident());
		}
		Assert.Equal(0, mover.ZoneOfControlThreatStrength(destination));
	}

	// ---------- the guards (§6.1) ----------

	[Fact]
	public void AMoverWithOneHitPointLeftIsNeverHit() {
		(Tile source, Tile destination, Tile band) = PrepareLandStep();
		MapUnit mover = MakeUnit(MakePlayer(), MakeLandPrototype(0, 1, zoneOfControl: false), source, hitPoints: 1);
		MakeUnit(MakeSeeingEnemy(mover), MakeLandPrototype(10, 1, zoneOfControl: true), band);

		ScriptedRandom rng = UseScriptedRandom(1023);

		// The guard returns before the scan, so not even a roll is requested.
		Assert.False(mover.ApplyZoneOfControl(destination));
		Assert.Equal(1, mover.hitPointsRemaining);
		Assert.Empty(rng.requestedBounds);
	}

	[Fact]
	public void AMoverWithNoDefenceStrengthIsNeverHit() {
		(Tile source, Tile destination, Tile band) = PrepareLandStep();
		MapUnit mover = MakeUnit(MakePlayer(), MakeLandPrototype(0, 0, zoneOfControl: false), source);
		MakeUnit(MakeSeeingEnemy(mover), MakeLandPrototype(10, 1, zoneOfControl: true), band);

		ScriptedRandom rng = UseScriptedRandom(1023);
		Assert.False(mover.ApplyZoneOfControl(destination));
		Assert.Equal(3, mover.hitPointsRemaining);
		Assert.Empty(rng.requestedBounds);
	}

	[Fact]
	public void AnAirMoverIsNeverHit() {
		(Tile source, Tile destination, Tile band) = PrepareLandStep();
		UnitPrototype airType = new UnitPrototype { name = "TestAir", attack = 0, defense = 5 };
		airType.categories.Add("Air");
		MapUnit mover = MakeUnit(MakePlayer(), airType, source);
		MakeUnit(MakeSeeingEnemy(mover), MakeLandPrototype(10, 1, zoneOfControl: true), band);

		ScriptedRandom rng = UseScriptedRandom(1023);
		Assert.False(mover.ApplyZoneOfControl(destination));
		Assert.Equal(3, mover.hitPointsRemaining);
		Assert.Empty(rng.requestedBounds);
	}

	// ---------- the class and adjacency filters (§6.2) ----------

	[Fact]
	public void ALandCandidateDoesNotThreatenASeaMover() {
		(Tile source, Tile destination, Tile band) = PrepareWaterStep();
		MapUnit mover = MakeUnit(MakePlayer(), MakeSeaPrototype(0, 2, zoneOfControl: false), source);
		MakeUnit(MakeSeeingEnemy(mover), MakeLandPrototype(10, 1, zoneOfControl: true), band);

		Assert.Equal(0, mover.ZoneOfControlThreatStrength(destination));
	}

	[Fact]
	public void ASeaCandidateDoesNotThreatenALandMover() {
		(Tile source, Tile destination, Tile band) = PrepareLandStep();
		MapUnit mover = MakeUnit(MakePlayer(), MakeLandPrototype(0, 1, zoneOfControl: false), source);
		MakeUnit(MakeSeeingEnemy(mover), MakeSeaPrototype(10, 2, zoneOfControl: true), band);

		Assert.Equal(0, mover.ZoneOfControlThreatStrength(destination));
	}

	[Fact]
	public void ATileAdjacentToTheSourceButNotTheDestinationIsNotScanned() {
		(Tile source, Tile destination, Tile band) = PrepareLandStep();
		MapUnit mover = MakeUnit(MakePlayer(), MakeLandPrototype(0, 1, zoneOfControl: false), source);
		// (50, 48) is the source's northern neighbour and two tiles from the
		// destination, so it is outside the band of this step.
		Tile northOfSource = CleanTile(SourceX, SourceY - 2);
		MakeUnit(MakeSeeingEnemy(mover), MakeLandPrototype(10, 1, zoneOfControl: true), northOfSource);

		Assert.Equal(2, northOfSource.DistanceTo(destination));
		Assert.Equal(1, northOfSource.DistanceTo(source));
		Assert.Equal(0, mover.ZoneOfControlThreatStrength(destination));

		// The same candidate on a band tile does fire, which is the control for
		// the assertion above.
		MakeUnit(MakeSeeingEnemy(mover), MakeLandPrototype(10, 1, zoneOfControl: true), band);
		Assert.Equal(10, mover.ZoneOfControlThreatStrength(destination));
	}

	// ---------- the city threat against a sea mover (§6.2) ----------

	[Fact]
	public void AHostileCityContributesItsNavalPowerToASeaMover() {
		(Tile source, Tile destination, Tile band) = PrepareWaterStep();
		MapUnit mover = MakeUnit(MakePlayer(), MakeSeaPrototype(0, 2, zoneOfControl: false), source);
		Player enemy = MakeSeeingEnemy(mover);
		Building coastalFortress = gd.Buildings.First(b => b.navalPower > 0);
		MakeCity(enemy, band, population: 8, building: coastalFortress);

		Assert.Equal(8, mover.ZoneOfControlThreatStrength(destination));

		// The city needs the building...
		band.cityAtTile.constructed_buildings.Clear();
		Assert.Equal(0, mover.ZoneOfControlThreatStrength(destination));
		band.cityAtTile.AddBuilding(coastalFortress);

		// ...a hostile owner...
		Player friend = MakePlayer();
		friend.tileKnowledge.knownTiles.Add(mover.location);
		band.cityAtTile.owner = friend;
		Assert.Equal(0, mover.ZoneOfControlThreatStrength(destination));

		// ...and an owner who can see the mover.
		band.cityAtTile.owner = MakePlayer(barbarian: true);
		Assert.Equal(0, mover.ZoneOfControlThreatStrength(destination));

		// A land unit in the city does not add to the city's contribution: the
		// unit scan keeps sea units for a sea mover.
		band.cityAtTile.owner = enemy;
		MakeUnit(enemy, MakeLandPrototype(10, 1, zoneOfControl: true), band);
		Assert.Equal(8, mover.ZoneOfControlThreatStrength(destination));

		// A sea unit does.
		MakeUnit(enemy, MakeSeaPrototype(6, 2, zoneOfControl: true), band);
		Assert.Equal(8, mover.ZoneOfControlThreatStrength(destination));
	}

	// ---------- where the rule fires (§5 step 5) ----------

	[Fact]
	public async Task ALandToWaterStepFiresNothingBecauseOfTheCallerGate() {
		(Tile source, Tile destination, Tile band) = PrepareLandStep();
		MapUnit mover = MakeUnit(MakePlayer(), MakeLandPrototype(0, 1, zoneOfControl: false), source);
		MakeUnit(MakeSeeingEnemy(mover), MakeLandPrototype(10, 1, zoneOfControl: true), band);

		// Turn the destination into water. The scan itself does not know about
		// the domain gate: a land candidate beside this step is a threat. The
		// gate is at the call site, exactly as in the original
		// (0x5b9416-0x5b9434).
		destination.baseTerrainType = new TerrainType { Key = "coast" };
		destination.overlayTerrainType = destination.baseTerrainType;
		Assert.Equal(10, mover.ZoneOfControlThreatStrength(destination));

		ScriptedRandom rng = UseScriptedRandom(1023);
		Assert.False(await mover.Move(TileDirection.EAST));
		Assert.Equal(3, mover.hitPointsRemaining);
		Assert.Empty(rng.requestedBounds);
	}

	[Fact]
	public async Task ARefusedStepStillCostsTheHitPoint() {
		(Tile source, Tile destination, Tile band) = PrepareLandStep();
		MapUnit mover = MakeUnit(MakePlayer(), MakeLandPrototype(0, 1, zoneOfControl: false, movement: 1), source);
		MakeUnit(MakeSeeingEnemy(mover), MakeLandPrototype(10, 1, zoneOfControl: true), band);

		// No movement points left, which the original refuses at the cost and
		// legality stage - after the zone of control (11_movement.md §5 step 6).
		mover.movementPoints.onConsumeAll();
		Assert.False(mover.movementPoints.canMove);

		// escape(1, 10) = 630, so this roll does not escape.
		ScriptedRandom rng = UseScriptedRandom(700);
		Assert.False(await mover.Move(TileDirection.EAST));

		Assert.Equal(2, mover.hitPointsRemaining);
		Assert.Equal(new List<int> { 1024 }, rng.requestedBounds);
		Assert.Equal(SourceX, mover.location.XCoordinate);
	}

	[Fact]
	public async Task AStepOntoATileHeldByACivTheMoverIsNotAtWarWithDoesNotFire() {
		(Tile source, Tile destination, Tile band) = PrepareLandStep();
		MapUnit mover = MakeUnit(MakePlayer(), MakeLandPrototype(0, 1, zoneOfControl: false), source);
		MakeUnit(MakeSeeingEnemy(mover), MakeLandPrototype(10, 1, zoneOfControl: true), band);

		// A unit of a third civ, at peace with the mover, holds the destination.
		// The original refuses that step before the zone-of-control call, with
		// the "declare war?" preview, so the hit point is not spent
		// (11_movement.md §5 steps 2 and 5).
		MapUnit peaceful = MakeUnit(MakePlayer(), MakeLandPrototype(0, 2, zoneOfControl: false), destination);
		Assert.True(mover.owner.IsAtPeaceWith(peaceful.owner));
		Assert.False(mover.CanEnter(destination, out MapUnit.Intent intent));
		Assert.Equal(MapUnit.Intent.WarDeclaration, intent);

		ScriptedRandom rng = UseScriptedRandom(1023);
		Assert.False(await mover.Move(TileDirection.EAST));
		Assert.Equal(3, mover.hitPointsRemaining);
		Assert.Empty(rng.requestedBounds);
	}

	[Fact]
	public async Task AStepThatFightsSkipsTheZoneOfControl() {
		(Tile source, Tile destination, Tile band) = PrepareLandStep();
		// A mover that cannot fight, so the destination resolution draws no
		// dice and the only possible roll is the zone of control's.
		MapUnit mover = MakeUnit(MakePlayer(), MakeLandPrototype(0, 1, zoneOfControl: false, movement: 1), source);
		MakeUnit(MakeSeeingEnemy(mover), MakeLandPrototype(10, 1, zoneOfControl: true), band);
		MakeUnit(MakePlayer(barbarian: true), MakeLandPrototype(0, 1, zoneOfControl: false), destination);

		// The original resolves the destination before it fires the zone of
		// control, and a fight that leaves the mover alive jumps past the call
		// (0x5b9321), so an attacking step never pays.
		Assert.True(mover.CanEnter(destination, out MapUnit.Intent intent));
		Assert.Equal(MapUnit.Intent.Fight, intent);
		Assert.Equal(10, mover.ZoneOfControlThreatStrength(destination));

		ScriptedRandom rng = UseScriptedRandom(1023);
		Assert.True(await mover.Move(TileDirection.EAST));
		Assert.Empty(rng.requestedBounds);
		Assert.Equal(3, mover.hitPointsRemaining);
	}

	[Fact]
	public async Task AnOrdinaryStepFiresTheRule() {
		(Tile source, Tile destination, Tile band) = PrepareLandStep();
		MapUnit mover = MakeUnit(MakePlayer(), MakeLandPrototype(0, 1, zoneOfControl: false, movement: 1), source);
		MakeUnit(MakeSeeingEnemy(mover), MakeLandPrototype(10, 1, zoneOfControl: true), band);

		ScriptedRandom rng = UseScriptedRandom(700);
		Assert.True(await mover.Move(TileDirection.EAST));

		Assert.Equal(2, mover.hitPointsRemaining);
		Assert.Equal(new List<int> { 1024 }, rng.requestedBounds);
		Assert.Equal(DestinationX, mover.location.XCoordinate);
	}
}
