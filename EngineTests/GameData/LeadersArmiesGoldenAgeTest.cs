using System;
using System.Collections.Generic;
using System.Linq;
using C7Engine;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using Xunit;

namespace EngineTests.GameData;

// Tests for re/specs/23_leaders_armies_golden_age.md: the two great-leader
// creation rolls and their cap, army formation/loading/capacity/strength, and
// the Golden Age.
public class LeadersArmiesGoldenAgeTest : IClassFixture<SaveGameFixture> {
	private readonly SaveGameFixture fixture;
	private readonly C7GameData.GameData gd;
	private readonly UnitPrototype armyType;
	private readonly UnitPrototype leaderType;
	private readonly UnitPrototype goldenAgeType;

	// A Random whose integer rolls and odds are fully controlled by the test, so
	// that the exact probability boundary can be asserted. lastMax records the
	// exclusive bound the production code asked for, which is the denominator of
	// the roll.
	private class ScriptedRandom : Random {
		public int nextIntResult = 1;
		public int lastMax = -1;
		public double nextDoubleResult = 0.5;

		public override int Next(int maxValue) {
			lastMax = maxValue;
			return nextIntResult % maxValue;
		}

		public override double NextDouble() {
			return nextDoubleResult;
		}
	}

	public LeadersArmiesGoldenAgeTest(SaveGameFixture fixture) {
		this.fixture = fixture;
		gd = fixture.saveGame.ToGameData(fixture.behaviors);
		EngineStorage.InitializeGameDataForTests(gd);

		armyType = gd.unitPrototypes.First(p => p.isArmy);
		leaderType = gd.unitPrototypes.First(p => p.isLeader);
		goldenAgeType = gd.unitPrototypes.First(p => p.startsGoldenAge);
	}

	// ---------- helpers ----------

	private Player MakePlayer(bool militaristic = false, bool scientific = false, bool barbarian = false) {
		Civilization civ = new Civilization("TestCiv") {
			noun = "TestCivs",
			adjective = "Test",
			isBarbarian = barbarian,
		};
		civ.traits = [];
		if (militaristic) civ.traits.Add(Civilization.Trait.Militaristic);
		if (scientific) civ.traits.Add(Civilization.Trait.Scientific);

		return new Player {
			id = gd.GenerateID("player"),
			civilization = civ,
			rules = gd.rules,
			government = new Government(),
		};
	}

	// A player whose civilization is one of the ruleset's own, which is what
	// unit availability is keyed on.
	private Player MakePlayerOfRulesetCiv() {
		return new Player {
			id = gd.GenerateID("player"),
			civilization = gd.civilizations.First(c => !c.isBarbarian),
			rules = gd.rules,
			government = new Government(),
		};
	}

	private Tile MakeTile(int shields = 0, int commerce = 0) {
		return new Tile(gd.GenerateID("tile")) {
			overlayTerrainType = new TerrainType {
				Key = "test",
				baseShieldProduction = shields,
				baseCommerceProduction = commerce,
			},
		};
	}

	private MapUnit MakeUnit(Player owner, UnitPrototype proto, Tile location, string experience = "Elite") {
		MapUnit unit = proto.GetInstance(gd.GenerateID(proto.name), proto, owner, location: location);
		unit.experienceLevel = gd.experienceLevels.First(e => e.key == experience);
		unit.experienceLevelKey = experience;
		unit.hitPointsRemaining = unit.maxHitPoints;
		location.unitsOnTile.Add(unit);
		gd.mapUnits.Add(unit);
		owner.AddUnit(unit);
		return unit;
	}

	private UnitPrototype MakeLandPrototype(int attack, int defense, int movement = 1) {
		UnitPrototype proto = new UnitPrototype {
			name = $"TestUnit-{attack}-{defense}-{movement}",
			attack = attack,
			defense = defense,
			movement = movement,
		};
		proto.categories.Add("Land");
		return proto;
	}

	private City MakeCity(Player owner, Tile location, bool capital = false) {
		City city = new City(location, owner, "TestCity", gd.GenerateID("city")) {
			capital = capital,
		};
		city.residents.Add(new CityResident());
		location.cityAtTile = city;
		owner.cities.Add(city);
		gd.cities.Add(city);
		return city;
	}

	private ScriptedRandom UseScriptedRandom() {
		ScriptedRandom rng = new ScriptedRandom();
		C7GameData.GameData.rng = rng;
		return rng;
	}

	// ---------- the imported data ----------

	[Fact]
	public void BaseRulesetCarriesTheLeaderArmyAndGoldenAgeFlags() {
		Assert.Equal("Leader", leaderType.name);
		Assert.True(leaderType.isLeader);
		Assert.False(leaderType.isArmy);
		Assert.True(leaderType.unproducible, "a leader is created by a roll, never produced");

		Assert.Equal("Army", armyType.name);
		Assert.True(armyType.isArmy);
		Assert.False(armyType.unproducible, "the army is obtainable");
		Assert.Equal(3, armyType.capacity);

		Assert.True(goldenAgeType.startsGoldenAge);

		// Measured from the shipped conquests.biq.
		Assert.Equal(20, gd.rules.GoldenAgeDuration);
		Assert.Equal(4, gd.rules.CitiesNeededToSupportAnArmy);
		Assert.True(gd.rules.AllowScientificLeaders);
	}

	[Fact]
	public void ArmyCanOnlyBeBuiltWithEnoughCitiesToSupportIt() {
		Player player = MakePlayerOfRulesetCiv();
		Tile tile = MakeTile();
		HashSet<Resource> noResources = [];

		City city = MakeCity(player, tile);

		// One city is not enough for the first army.
		Assert.False(armyType.CanProduce(city, noResources));

		for (int i = 1; i < 4; ++i) {
			MakeCity(player, MakeTile());
		}

		// Four cities support one army.
		Assert.True(armyType.CanProduce(city, noResources));

		// With one army already in the field, four cities no longer suffice.
		MapUnit army = MakeUnit(player, armyType, MakeTile(), experience: "Regular");
		Assert.True(army.IsArmy);
		Assert.False(armyType.CanProduce(city, noResources));

		// Eight cities support two armies.
		for (int i = 0; i < 4; ++i) {
			MakeCity(player, MakeTile());
		}
		Assert.True(armyType.CanProduce(city, noResources));
	}

	// ---------- the military leader roll ----------

	private (Player player, Tile tile, MapUnit winner, MapUnit defeated) SetupEliteVictory(bool barbarianVictim = false) {
		Player player = MakePlayer();
		Tile tile = MakeTile();
		MapUnit winner = MakeUnit(player, MakeLandPrototype(4, 2), tile);
		Player victim = barbarianVictim ? MakePlayer(barbarian: true) : MakePlayer();
		MapUnit defeated = MakeUnit(victim, MakeLandPrototype(1, 1), MakeTile());
		return (player, tile, winner, defeated);
	}

	[Fact]
	public void EliteVictoryProducesALeaderOnAZeroRollOutOfSixteen() {
		var (player, tile, winner, defeated) = SetupEliteVictory();
		ScriptedRandom rng = UseScriptedRandom();
		rng.nextIntResult = 0;

		winner.RollForCombatOutcome(defeated, defeatedWasTheDefender: true);

		Assert.Equal(16, rng.lastMax);
		Assert.True(winner.hasProducedLeader);
		Assert.True(player.HasGreatLeader());
		Assert.Equal(MapUnit.LeaderKind.Military, player.units.First(u => u.unitType.isLeader).leaderKind);
	}

	[Fact]
	public void EliteVictoryProducesNoLeaderOnANonZeroRoll() {
		var (player, tile, winner, defeated) = SetupEliteVictory();
		ScriptedRandom rng = UseScriptedRandom();
		rng.nextIntResult = 1;

		winner.RollForCombatOutcome(defeated, defeatedWasTheDefender: true);

		Assert.Equal(16, rng.lastMax);
		Assert.False(winner.hasProducedLeader);
		Assert.False(player.HasGreatLeader());
	}

	[Fact]
	public void HeroicEpicImprovesTheLeaderOddsToTwelve() {
		var (player, tile, winner, defeated) = SetupEliteVictory();
		MakeCity(player, MakeTile()).AddBuilding(gd.Buildings.First(b => b.increasesLeaderChance));
		ScriptedRandom rng = UseScriptedRandom();
		rng.nextIntResult = 0;

		winner.RollForCombatOutcome(defeated, defeatedWasTheDefender: true);

		Assert.Equal(12, rng.lastMax);
		Assert.True(player.HasGreatLeader());
	}

	[Fact]
	public void OnlyEliteUnitsRollForALeader() {
		Player player = MakePlayer();
		Tile tile = MakeTile();
		MapUnit winner = MakeUnit(player, MakeLandPrototype(4, 2), tile, experience: "Veteran");
		MapUnit defeated = MakeUnit(MakePlayer(), MakeLandPrototype(1, 1), MakeTile());
		ScriptedRandom rng = UseScriptedRandom();
		rng.nextIntResult = 0;

		winner.RollForCombatOutcome(defeated, defeatedWasTheDefender: true);

		// A veteran takes the promotion branch, so the leader denominator was
		// never requested.
		Assert.Equal(-1, rng.lastMax);
		Assert.False(player.HasGreatLeader());
	}

	[Fact]
	public void NoLeaderIsProducedAgainstBarbarians() {
		var (player, tile, winner, defeated) = SetupEliteVictory(barbarianVictim: true);
		ScriptedRandom rng = UseScriptedRandom();
		rng.nextIntResult = 0;

		winner.RollForCombatOutcome(defeated, defeatedWasTheDefender: true);

		Assert.False(player.HasGreatLeader());
	}

	[Fact]
	public void NavalAndAirVictoriesProduceNoLeader() {
		Player player = MakePlayer();
		UnitPrototype sea = MakeLandPrototype(4, 2);
		sea.categories.Clear();
		sea.categories.Add("Sea");
		MapUnit winner = MakeUnit(player, sea, MakeTile());
		MapUnit defeated = MakeUnit(MakePlayer(), MakeLandPrototype(1, 1), MakeTile());
		ScriptedRandom rng = UseScriptedRandom();
		rng.nextIntResult = 0;

		winner.RollForCombatOutcome(defeated, defeatedWasTheDefender: true);

		Assert.False(player.HasGreatLeader());
	}

	[Fact]
	public void ACivHoldsAtMostOneGreatLeaderAtATime() {
		var (player, tile, winner, defeated) = SetupEliteVictory();
		MakeUnit(player, leaderType, MakeTile(), experience: "Regular");
		ScriptedRandom rng = UseScriptedRandom();
		rng.nextIntResult = 0;

		winner.RollForCombatOutcome(defeated, defeatedWasTheDefender: true);

		// The cap is checked before the roll, so no roll happens at all.
		Assert.Equal(-1, rng.lastMax);
		Assert.Single(player.units.Where(u => u.unitType.isLeader));
	}

	[Fact]
	public void AUnitProducesAtMostOneLeaderInItsLifetime() {
		var (player, tile, winner, defeated) = SetupEliteVictory();
		winner.hasProducedLeader = true;
		ScriptedRandom rng = UseScriptedRandom();
		rng.nextIntResult = 0;

		winner.RollForCombatOutcome(defeated, defeatedWasTheDefender: true);

		Assert.False(player.HasGreatLeader());
	}

	[Fact]
	public void LeaderSpawnsOnTheDefeatedTileWhenThatUnitWasAlone() {
		var (player, tile, winner, defeated) = SetupEliteVictory();
		ScriptedRandom rng = UseScriptedRandom();
		rng.nextIntResult = 0;

		winner.RollForCombatOutcome(defeated, defeatedWasTheDefender: true);

		MapUnit leader = player.units.First(u => u.unitType.isLeader);
		Assert.Equal(defeated.location, leader.location);
	}

	[Fact]
	public void LeaderSpawnsWithTheWinnerWhenTheDefeatedTileIsCrowded() {
		var (player, tile, winner, defeated) = SetupEliteVictory();
		MakeUnit(MakePlayer(), MakeLandPrototype(1, 1), defeated.location);
		ScriptedRandom rng = UseScriptedRandom();
		rng.nextIntResult = 0;

		winner.RollForCombatOutcome(defeated, defeatedWasTheDefender: true);

		MapUnit leader = player.units.First(u => u.unitType.isLeader);
		Assert.Equal(winner.location, leader.location);
	}

	// ---------- the scientific leader roll ----------

	private (Player player, City capital) SetupCapital(bool scientific = false) {
		Player player = MakePlayer(scientific: scientific);
		City capital = MakeCity(player, MakeTile(), capital: true);
		gd.turn = 1;
		return (player, capital);
	}

	[Fact]
	public void ResearchingATechProducesAScientificLeaderOnTheBaseThreshold() {
		var (player, capital) = SetupCapital();
		ScriptedRandom rng = UseScriptedRandom();
		rng.nextIntResult = 2;

		player.RollForScientificLeader();

		Assert.Equal(100, rng.lastMax);
		Assert.True(player.HasGreatLeader());
		MapUnit leader = player.units.First(u => u.unitType.isLeader);
		Assert.Equal(MapUnit.LeaderKind.Scientific, leader.leaderKind);
		Assert.Equal(capital.location, leader.location);
	}

	[Fact]
	public void TheBaseScientificLeaderThresholdIsThreePercent() {
		var (player, capital) = SetupCapital();
		ScriptedRandom rng = UseScriptedRandom();
		rng.nextIntResult = 3;

		player.RollForScientificLeader();

		Assert.False(player.HasGreatLeader());
	}

	[Fact]
	public void ScientificCivsGetAFivePercentThreshold() {
		var (player, capital) = SetupCapital(scientific: true);
		ScriptedRandom rng = UseScriptedRandom();
		rng.nextIntResult = 4;

		player.RollForScientificLeader();

		Assert.Equal(100, rng.lastMax);
		Assert.True(player.HasGreatLeader());
	}

	[Fact]
	public void NonScientificCivsDoNotGetTheFivePercentThreshold() {
		var (player, capital) = SetupCapital(scientific: false);
		ScriptedRandom rng = UseScriptedRandom();
		rng.nextIntResult = 4;

		player.RollForScientificLeader();

		Assert.False(player.HasGreatLeader());
	}

	[Fact]
	public void ScientificLeaderRollNeedsATurnAndACapitalAndTheRule() {
		// Before the first turn no leader appears.
		var (player, capital) = SetupCapital();
		gd.turn = 0;
		ScriptedRandom rng = UseScriptedRandom();
		rng.nextIntResult = 0;
		player.RollForScientificLeader();
		Assert.False(player.HasGreatLeader());

		// A civ without a capital gets nothing.
		gd.turn = 1;
		Player capitalLess = MakePlayer();
		MakeCity(capitalLess, MakeTile(), capital: false);
		capitalLess.RollForScientificLeader();
		Assert.False(capitalLess.HasGreatLeader());

		// A ruleset that disallows scientific leaders gets nothing.
		Player disallowed = MakePlayer();
		MakeCity(disallowed, MakeTile(), capital: true);
		disallowed.rules = new Rules { AllowScientificLeaders = false };
		disallowed.RollForScientificLeader();
		Assert.False(disallowed.HasGreatLeader());
	}

	[Fact]
	public void ScientificLeaderRollRespectsTheOneLeaderCap() {
		var (player, capital) = SetupCapital();
		MakeUnit(player, leaderType, MakeTile(), experience: "Regular");
		ScriptedRandom rng = UseScriptedRandom();
		rng.nextIntResult = 0;

		player.RollForScientificLeader();

		Assert.Equal(-1, rng.lastMax);
		Assert.Single(player.units.Where(u => u.unitType.isLeader));
	}

	// ---------- forming an army ----------

	[Fact]
	public void MilitaryLeaderInACityFormsAnArmyAndIsConsumed() {
		Player player = MakePlayer();
		Tile tile = MakeTile();
		MakeCity(player, tile);
		for (int i = 1; i < 4; ++i) {
			MakeCity(player, MakeTile());
		}
		MapUnit leader = MakeUnit(player, leaderType, tile, experience: "Regular");
		leader.leaderKind = MapUnit.LeaderKind.Military;

		Assert.True(leader.CanFormArmy());
		MapUnit army = leader.FormArmy();

		Assert.NotNull(army);
		Assert.True(army.IsArmy);
		Assert.Equal(player, army.owner);
		Assert.Equal(tile, army.location);
		Assert.Equal(leader.nationality, army.nationality);

		Assert.DoesNotContain(leader, player.units);
		Assert.DoesNotContain(leader, tile.unitsOnTile);
	}

	[Fact]
	public void ScientificLeadersCannotFormArmies() {
		Player player = MakePlayer();
		Tile tile = MakeTile();
		MakeCity(player, tile);
		for (int i = 1; i < 4; ++i) {
			MakeCity(player, MakeTile());
		}
		MapUnit leader = MakeUnit(player, leaderType, tile, experience: "Regular");
		leader.leaderKind = MapUnit.LeaderKind.Scientific;

		Assert.False(leader.CanFormArmy());
		Assert.Null(leader.FormArmy());
	}

	[Fact]
	public void ALeaderCannotFormAnArmyWithoutEnoughCities() {
		Player player = MakePlayer();
		Tile tile = MakeTile();
		MakeCity(player, tile);
		MapUnit leader = MakeUnit(player, leaderType, tile, experience: "Regular");

		Assert.False(leader.CanFormArmy());
	}

	[Fact]
	public void ALeaderMustBeInOneOfItsOwnCitiesToFormAnArmy() {
		Player player = MakePlayer();
		Tile tile = MakeTile();
		for (int i = 0; i < 4; ++i) {
			MakeCity(player, MakeTile());
		}
		MapUnit leader = MakeUnit(player, leaderType, tile, experience: "Regular");

		Assert.False(leader.CanFormArmy());
	}

	// ---------- loading and capacity ----------

	private (Player player, MapUnit army) SetupArmy(int cities = 0) {
		Player player = MakePlayer();
		for (int i = 0; i < cities; ++i) {
			MakeCity(player, MakeTile());
		}
		MapUnit army = MakeUnit(player, armyType, MakeTile(), experience: "Regular");
		return (player, army);
	}

	[Fact]
	public void AnArmyHoldsThreeUnitsAndThePentagonAddsOne() {
		var (player, army) = SetupArmy();

		List<MapUnit> members = [];
		for (int i = 0; i < 3; ++i) {
			MapUnit member = MakeUnit(player, MakeLandPrototype(1, 1), army.location, experience: "Regular");
			Assert.True(member.LoadIntoArmy(army));
			members.Add(member);
		}

		MapUnit extra = MakeUnit(player, MakeLandPrototype(1, 1), army.location, experience: "Regular");
		Assert.False(extra.LoadIntoArmy(army));

		// The Pentagon raises the army's capacity to four.
		MakeCity(player, army.location).AddBuilding(gd.Buildings.First(b => b.allowsLargerArmies));
		Assert.True(extra.LoadIntoArmy(army));
		Assert.Equal(4, army.Members().Count);

		foreach (MapUnit member in members) {
			Assert.Equal(army.id, member.loadedOnUnitId);
		}
	}

	[Fact]
	public void OnlyFriendlyLandUnitsThatAreNotAlreadyCarriedCanLoad() {
		var (player, army) = SetupArmy();
		MapUnit enemy = MakeUnit(MakePlayer(), MakeLandPrototype(1, 1), army.location, experience: "Regular");
		Assert.False(enemy.LoadIntoArmy(army));

		UnitPrototype sea = MakeLandPrototype(1, 1);
		sea.categories.Clear();
		sea.categories.Add("Sea");
		MapUnit boat = MakeUnit(player, sea, army.location, experience: "Regular");
		Assert.False(boat.LoadIntoArmy(army));

		MapUnit member = MakeUnit(player, MakeLandPrototype(1, 1), army.location, experience: "Regular");
		Assert.True(member.LoadIntoArmy(army));
		Assert.False(member.LoadIntoArmy(army));
	}

	[Fact]
	public void LoadingPoolsTheMembersHitPoints() {
		var (player, army) = SetupArmy();

		// An empty army's maximum is floored at one.
		Assert.Equal(1, army.maxHitPoints);

		MapUnit healthy = MakeUnit(player, MakeLandPrototype(1, 1), army.location, experience: "Regular");
		Assert.True(healthy.LoadIntoArmy(army));
		Assert.Equal(3, army.maxHitPoints);
		Assert.Equal(3, army.hitPointsRemaining);

		// A damaged member contributes only its remaining hit points.
		MapUnit damaged = MakeUnit(player, MakeLandPrototype(1, 1), army.location, experience: "Regular");
		damaged.hitPointsRemaining = 1;
		Assert.True(damaged.LoadIntoArmy(army));
		Assert.Equal(6, army.maxHitPoints);
		Assert.Equal(4, army.hitPointsRemaining);

		// The member's own damage is zeroed while it is carried.
		Assert.Equal(damaged.maxHitPoints, damaged.hitPointsRemaining);
	}

	[Fact]
	public void AnArmyMovesAsFastAsItsFastestMember() {
		var (player, army) = SetupArmy();
		Assert.Equal(1, army.movementPoints.remaining);

		MapUnit fast = MakeUnit(player, MakeLandPrototype(1, 1, movement: 3), army.location, experience: "Regular");
		Assert.True(fast.LoadIntoArmy(army));

		Assert.Equal(3, army.movementPoints.remaining);
		Assert.Equal(0, fast.movementPoints.remaining);
	}

	// ---------- army strength in combat ----------

	private MapUnit MakeArmyWith(params (int attack, int defense)[] members) {
		var (player, army) = SetupArmy();
		foreach ((int attack, int defense) in members) {
			MapUnit member = MakeUnit(player, MakeLandPrototype(attack, defense), army.location, experience: "Regular");
			Assert.True(member.LoadIntoArmy(army));
		}
		return army;
	}

	[Fact]
	public void ArmyStrengthIsCountOverTwoPlusTheSumOverCount() {
		MapUnit empty = MakeArmyWith();
		Assert.Equal(0, empty.AttackStrength());
		Assert.Equal(0, empty.DefenseStrength());

		// One member: (0 + 4) / 1 = 4.
		MapUnit one = MakeArmyWith((4, 6));
		Assert.Equal(4, one.AttackStrength());
		Assert.Equal(6, one.DefenseStrength());

		// Two members: (1 + 4 + 6) / 2 = 5 and (1 + 6 + 4) / 2 = 5.
		MapUnit two = MakeArmyWith((4, 6), (6, 4));
		Assert.Equal(5, two.AttackStrength());
		Assert.Equal(5, two.DefenseStrength());

		// Three members: (1 + 4 + 6 + 8) / 3 = 6 and (1 + 3 + 5 + 7) / 3 = 5.
		MapUnit three = MakeArmyWith((4, 3), (6, 5), (8, 7));
		Assert.Equal(6, three.AttackStrength());
		Assert.Equal(5, three.DefenseStrength());

		// The combat odds use the same aggregation.
		Assert.Equal(6.0, three.BaseStrength(CombatRole.Attack));
		Assert.Equal(5.0, three.BaseStrength(CombatRole.Defense));
	}

	[Fact]
	public void NonArmyUnitsUseTheirTypeStrength() {
		Player player = MakePlayer();
		MapUnit unit = MakeUnit(player, MakeLandPrototype(7, 9), MakeTile());

		Assert.Equal(7, unit.AttackStrength());
		Assert.Equal(9, unit.DefenseStrength());
		Assert.Equal(7.0, unit.BaseStrength(CombatRole.Attack));
		Assert.Equal(9.0, unit.BaseStrength(CombatRole.Defense));
	}

	// ---------- the golden age ----------

	private (Player player, MapUnit winner, MapUnit defeated) SetupGoldenAgeVictory() {
		Player player = MakePlayer();
		Tile tile = MakeTile();
		MapUnit winner = MakeUnit(player, goldenAgeType, tile);
		MapUnit defeated = MakeUnit(MakePlayer(), MakeLandPrototype(1, 1), MakeTile());
		gd.turn = 10;
		return (player, winner, defeated);
	}

	[Fact]
	public void AUniqueUnitVictoryStartsATwentyTurnGoldenAge() {
		var (player, winner, defeated) = SetupGoldenAgeVictory();

		winner.MaybeStartGoldenAge(defeated);

		Assert.True(player.HasHadGoldenAge);
		Assert.Equal(30, player.goldenAgeEndTurn);
		Assert.True(player.GoldenAgeActive);
	}

	[Fact]
	public void BarbarianVictoriesDoNotStartAGoldenAge() {
		var (player, winner, defeated) = SetupGoldenAgeVictory();
		defeated.owner.civilization.isBarbarian = true;

		winner.MaybeStartGoldenAge(defeated);

		Assert.False(player.HasHadGoldenAge);
	}

	[Fact]
	public void AnArmyCarryingAUniqueUnitStartsAGoldenAge() {
		var (player, winner, defeated) = SetupGoldenAgeVictory();
		var (_, army) = SetupArmy();
		army.owner = player;
		MapUnit unique = MakeUnit(player, goldenAgeType, army.location);
		Assert.True(unique.LoadIntoArmy(army));

		army.MaybeStartGoldenAge(defeated);

		Assert.True(player.HasHadGoldenAge);
	}

	[Fact]
	public void AGoldenAgeHappensAtMostOncePerGame() {
		var (player, winner, defeated) = SetupGoldenAgeVictory();
		winner.MaybeStartGoldenAge(defeated);
		Assert.Equal(30, player.goldenAgeEndTurn);

		gd.turn = 25;
		winner.MaybeStartGoldenAge(defeated);

		Assert.Equal(30, player.goldenAgeEndTurn);
	}

	[Fact]
	public void TheGoldenAgeWindowEndsOnItsEndTurn() {
		Player player = MakePlayer();
		gd.turn = 10;
		player.StartGoldenAge();
		Assert.Equal(30, player.goldenAgeEndTurn);

		Assert.True(player.GoldenAgeActive);
		gd.turn = 29;
		Assert.True(player.GoldenAgeActive);
		gd.turn = 30;
		Assert.False(player.GoldenAgeActive);

		// Before the first trigger the window is closed.
		Player never = MakePlayer();
		Assert.False(never.GoldenAgeActive);
	}

	[Fact]
	public void AGoldenAgeAddsOneShieldAndOneCommerceToProductiveTiles() {
		Player player = MakePlayer();
		Tile productive = MakeTile(shields: 1, commerce: 2);
		Tile barren = MakeTile(shields: 0, commerce: 0);

		Assert.Equal(1, productive.ProductionYield(player).yield);
		Assert.Equal(2, productive.CommerceYield(player).yield);

		gd.turn = 5;
		player.goldenAgeEndTurn = 10;

		Assert.Equal(2, productive.ProductionYield(player).yield);
		Assert.Equal(3, productive.CommerceYield(player).yield);

		// A tile producing nothing gains nothing.
		Assert.Equal(0, barren.ProductionYield(player).yield);
		Assert.Equal(0, barren.CommerceYield(player).yield);

		// Once the window closes the bonus is gone.
		gd.turn = 10;
		Assert.Equal(1, productive.ProductionYield(player).yield);
		Assert.Equal(2, productive.CommerceYield(player).yield);
	}

	[Fact]
	public void GreatWonderTraitsStartAGoldenAgeButSmallWondersDoNot() {
		Player player = MakePlayer();
		player.civilization.traits.Add(Civilization.Trait.Religious);
		player.civilization.traits.Add(Civilization.Trait.Industrious);
		City city = MakeCity(player, MakeTile());
		gd.turn = 3;

		// The Pyramids carry Religious, Industrious and Agricultural, which
		// covers both of this civ's traits.
		city.AddBuilding(gd.Buildings.First(b => b.name == "The Pyramids"));
		Assert.True(player.HasHadGoldenAge);
		Assert.Equal(23, player.goldenAgeEndTurn);

		// A civ with a trait no owned wonder carries gets no Golden Age.
		Player other = MakePlayer();
		other.civilization.traits.Add(Civilization.Trait.Religious);
		other.civilization.traits.Add(Civilization.Trait.Militaristic);
		MakeCity(other, MakeTile()).AddBuilding(gd.Buildings.First(b => b.name == "The Pyramids"));
		Assert.False(other.HasHadGoldenAge);

		// Small wonders never count, even when their trait flags match.
		Player small = MakePlayer();
		small.civilization.traits.Add(Civilization.Trait.Religious);
		small.civilization.traits.Add(Civilization.Trait.Seafaring);
		MakeCity(small, MakeTile()).AddBuilding(gd.Buildings.First(b => b.name == "Heroic Epic"));
		Assert.False(small.HasHadGoldenAge);
	}

	// ---------- the age of science ----------

	[Fact]
	public void AScientificLeaderInACityStartsTheAgeOfScienceAndIsConsumed() {
		Player player = MakePlayer();
		Tile tile = MakeTile();
		MakeCity(player, tile);
		MapUnit leader = MakeUnit(player, leaderType, tile, experience: "Regular");
		leader.leaderKind = MapUnit.LeaderKind.Scientific;
		gd.turn = 4;

		Assert.True(leader.CanStartScienceAge());
		Assert.True(leader.StartScienceAge());

		Assert.True(player.AgeOfScienceActive);
		Assert.Equal(4 + Player.AgeOfScienceDuration, player.ageOfScienceEndTurn);
		Assert.DoesNotContain(leader, player.units);
	}

	[Fact]
	public void MilitaryLeadersCannotStartTheAgeOfScience() {
		Player player = MakePlayer();
		Tile tile = MakeTile();
		MakeCity(player, tile);
		MapUnit leader = MakeUnit(player, leaderType, tile, experience: "Regular");
		leader.leaderKind = MapUnit.LeaderKind.Military;

		Assert.False(leader.CanStartScienceAge());
		Assert.False(leader.StartScienceAge());
		Assert.False(player.AgeOfScienceActive);
	}

	[Fact]
	public void AScientificLeaderOutsideACityCannotStartTheAgeOfScience() {
		Player player = MakePlayer();
		MapUnit leader = MakeUnit(player, leaderType, MakeTile(), experience: "Regular");
		leader.leaderKind = MapUnit.LeaderKind.Scientific;

		Assert.False(leader.CanStartScienceAge());
	}

	[Fact]
	public void TheAgeOfScienceIsTwentyTurnsAndCanBeRestarted() {
		Player player = MakePlayer();
		gd.turn = 4;
		player.StartAgeOfScience();
		Assert.Equal(24, player.ageOfScienceEndTurn);
		Assert.True(player.AgeOfScienceActive);

		gd.turn = 24;
		Assert.True(player.AgeOfScienceActive);
		gd.turn = 25;
		Assert.False(player.AgeOfScienceActive);

		// Unlike the Golden Age, a later scientific leader can start another
		// window.
		player.StartAgeOfScience();
		Assert.Equal(45, player.ageOfScienceEndTurn);
	}

	[Fact]
	public void TheAgeOfScienceMultipliesResearchByOnePointTwoFive() {
		Player player = MakePlayer();
		player.scienceRate = 10;

		// A capital city centre produces four commerce, which all goes to
		// research at a 100% science rate.
		Tile tile = MakeTile();
		City city = new City(tile, player, "ScienceCity", gd.GenerateID("city")) {
			capital = true,
		};
		tile.cityAtTile = city;
		player.cities.Add(city);
		gd.cities.Add(city);

		Assert.Equal(4, city.CurrentCommerceYield().beakers);

		gd.turn = 4;
		player.StartAgeOfScience();

		Assert.Equal((int)(4 * Player.AgeOfScienceResearchMultiplier), city.CurrentCommerceYield().beakers);
		Assert.Equal(5, city.CurrentCommerceYield().beakers);
	}

	// ---------- persistence ----------

	[Fact]
	public void LeadersArmiesAndTheGoldenAgeSurviveASaveRoundTrip() {
		Player player = gd.players.First(p => !p.isBarbarians);

		Tile tile = gd.map.tileAt(50, 50);
		gd.turn = 7;
		player.goldenAgeEndTurn = 27;
		player.ageOfScienceActive = true;
		player.ageOfScienceEndTurn = 12;

		// A leader that has already produced one leader.
		MapUnit leader = MakeUnit(player, leaderType, tile, experience: "Regular");
		leader.leaderKind = MapUnit.LeaderKind.Scientific;
		leader.hasProducedLeader = true;

		// An army carrying two members.
		UnitPrototype memberType = gd.unitPrototypes.First(p => p.name == "Warrior");
		MapUnit army = MakeUnit(player, armyType, tile, experience: "Regular");
		army.hitPointsRemaining = army.maxHitPoints;
		for (int i = 0; i < 2; ++i) {
			MapUnit member = MakeUnit(player, memberType, tile, experience: "Regular");
			Assert.True(member.LoadIntoArmy(army));
		}

		string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"c7-leader-save-{Guid.NewGuid()}.json");
		try {
			SaveGame.FromGameData(gd).Save(path);
			C7GameData.GameData reloaded = SaveGame.Load(path, (string unused) => unused).ToGameData(fixture.behaviors);

			Player reloadedPlayer = reloaded.players.First(p => p.id == player.id);
			Assert.Equal(27, reloadedPlayer.goldenAgeEndTurn);
			Assert.True(reloadedPlayer.ageOfScienceActive);
			Assert.Equal(12, reloadedPlayer.ageOfScienceEndTurn);

			MapUnit reloadedLeader = reloaded.mapUnits.First(u => u.id == leader.id);
			Assert.Equal(MapUnit.LeaderKind.Scientific, reloadedLeader.leaderKind);
			Assert.True(reloadedLeader.hasProducedLeader);
			Assert.True(reloadedLeader.unitType.isLeader);

			MapUnit reloadedArmy = reloaded.mapUnits.First(u => u.id == army.id);
			Assert.True(reloadedArmy.IsArmy);
			Assert.Equal(2, reloadedArmy.Members().Count);
			Assert.Equal(6, reloadedArmy.maxHitPoints);
			Assert.Equal(6, reloadedArmy.hitPointsRemaining);
		} finally {
			System.IO.File.Delete(path);
		}
	}
}
