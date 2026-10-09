using System;
using System.Collections.Generic;
using System.Linq;
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
		// The attacks below go through MapUnit.Move and MapUnit.Fight, which
		// wait on an animation-complete message that no test UI sends.
		EngineStorage.animationsEnabled = false;

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

	[Fact]
	public void AnArmyTakesItsMembersWithItWhenItDies() {
		var (player, army) = SetupArmy();
		MapUnit member = MakeUnit(player, MakeLandPrototype(1, 1), army.location, experience: "Regular");
		Assert.True(member.LoadIntoArmy(army));

		army.RemoveFromPlay();

		Assert.DoesNotContain(army, gd.mapUnits);
		Assert.DoesNotContain(member, gd.mapUnits);
		Assert.DoesNotContain(member, player.units);
		Assert.DoesNotContain(member, army.location.unitsOnTile);
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

	// The shipped Army's own attack and defence fields are both 0 — its strength
	// is the aggregation of its members' (12_combat.md §3.1,
	// 23_leaders_armies_golden_age.md §6.6). Both gates the attack order passes
	// must therefore ask the unit, not its prototype, or an army can never start
	// a fight and §6.8's "an army may attack repeatedly in one turn" is
	// unreachable. The order goes through MapUnit.Move, the branch the human's
	// MsgMoveUnit handler and the AI's CombatAI both use.
	[Fact]
	public void AnArmyWithMembersAttacksThroughTheNormalMovePath() {
		Tile attackerTile = CleanMapTile(50, 50);
		Tile defenderTile = CleanMapTile(52, 50);
		Player player = MakePlayer();
		MapUnit army = MakeUnit(player, armyType, attackerTile, experience: "Regular");
		for (int i = 0; i < 2; ++i) {
			MapUnit member = MakeUnit(player, MakeLandPrototype(4, 0), attackerTile, experience: "Regular");
			Assert.True(member.LoadIntoArmy(army));
		}

		// Two members: (2/2 + 4 + 4) / 2 = 4, from a prototype that reads 0.
		Assert.Equal(0, army.unitType.attack);
		Assert.Equal(0, army.unitType.defense);
		Assert.Equal(4, army.AttackStrength());
		Assert.True(army.unitType.isBlitz);

		MapUnit defender = MakeUnit(MakePlayer(barbarian: true), MakeLandPrototype(0, 0), defenderTile);
		ScriptedRandom rng = UseScriptedRandom();
		rng.nextIntResult = MapUnit.CombatOddsScale - 1; // the attacker wins every round

		Assert.True(army.Move(TileDirection.EAST).Result);
		Assert.True(army.hasUsedAttack);
		Assert.DoesNotContain(defender, gd.mapUnits);
		Assert.Same(defenderTile, army.location);
	}

	// An army's combat capability is its members', so the combat-unit predicate
	// the move order consults must see a loaded army as armed. Without this an
	// army is refused even earlier than the attack gate: its tile intent is
	// "notice the unit", not "fight".
	[Fact]
	public void AnArmyWithMembersIsACombatUnit() {
		var (player, army) = SetupArmy();
		Assert.False(army.IsCombatUnit());

		MapUnit member = MakeUnit(player, MakeLandPrototype(4, 0), army.location, experience: "Regular");
		Assert.True(member.LoadIntoArmy(army));
		Assert.True(army.IsCombatUnit());

		// The members' strength, not the prototype's, is what makes it one.
		Assert.Equal(0, army.unitType.attack);
		Assert.Equal(0, army.unitType.defense);
		Assert.Equal(4, army.AttackStrength());
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

	// ---------- the flags the shipped rules carry ----------

	[Fact]
	public void TheArmyKeepsItsRadarFlagAlongsideItsArmyFlag() {
		// Measured from PRTO 48 of the shipped conquests.biq: Flags1[0] bit 5
		// (Radar) and Flags1[0] bit 2 (Blitz) are set next to Flags1[2] bit 2
		// (Army).
		Assert.Equal(
			new[] { SaveUnitPrototype.Flag.Radar, SaveUnitPrototype.Flag.Army, SaveUnitPrototype.Flag.Blitz },
			armyType.flags.OrderBy(f => f).ToArray());

		Assert.Equal(
			new[] { SaveUnitPrototype.Flag.Leader },
			leaderType.flags.OrderBy(f => f).ToArray());
	}

	[Fact]
	public void TheBlitzCarriersAreTheArmyTheTanksAndTheMountedUniqueUnits() {
		// Measured from the shipped conquests.biq: PRTO 19 Tank, 21 Modern
		// Armor, 48 Army, 61 Cossack and 62 Panzer carry Flags1[0] bit 2.
		Assert.Equal(
			new[] { "Army", "Cossack", "Modern Armor", "Panzer", "Tank" },
			gd.unitPrototypes.Where(p => p.isBlitz).Select(p => p.name).OrderBy(n => n).ToArray());
	}

	// The names carrying a flag in one of the ruleset file's collections.
	private static List<string> NamesWithFlag(JsonElement collection, string flag) {
		List<string> result = new();
		foreach (JsonElement entry in collection.EnumerateArray()) {
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

	// C7/Lua/civ3/ruleset.json is a second carrier for the ability flags: a game
	// generated from the ruleset never reads the BIQ importer, so a flag that
	// exists only there is dead in play. This test reads the ruleset file itself
	// and holds it to the same carrier sets measured from the shipped
	// conquests.biq, so dropping a flag fails here rather than silently in a
	// generated game.
	[Fact]
	public void TheRulesetGivesBlitzAndAmphibiousToTheShippedBiqCarriers() {
		using JsonDocument ruleset = JsonUtils.LoadBaseRuleset();
		JsonElement prototypes = ruleset.RootElement.GetProperty("unitPrototypes");

		Assert.Equal(new List<string> { "Army", "Cossack", "Modern Armor", "Panzer", "Tank" },
			NamesWithFlag(prototypes, "blitz"));
		Assert.Equal(new List<string> { "Berserk", "Marine" },
			NamesWithFlag(prototypes, "amphibious"));

		// The loaded ruleset agrees with the file it was read from, which is the
		// same set the flags above name.
		Assert.Equal(new[] { "Army", "Cossack", "Modern Armor", "Panzer", "Tank" },
			gd.unitPrototypes.Where(p => p.isBlitz).Select(p => p.name).OrderBy(n => n).ToArray());
		Assert.Equal(new[] { "Berserk", "Marine" },
			gd.unitPrototypes.Where(p => p.isAmphibious).Select(p => p.name).OrderBy(n => n).ToArray());
	}

	[Fact]
	public void TheAmphibiousCarriersAreTheMarineAndTheBerserk() {
		// Measured from the shipped conquests.biq: PRTO 4 Marine and 68 Berserk
		// carry Flags1[0] bit 6.
		Assert.Equal(
			new[] { "Berserk", "Marine" },
			gd.unitPrototypes.Where(p => p.isAmphibious).Select(p => p.name).OrderBy(n => n).ToArray());
	}

	[Fact]
	public void TheStartsGoldenAgeCarriersAreTheUniqueUnitsAndNothingElse() {
		// 31 of the ruleset's units carry StartsGoldenAge, the number of such
		// carriers among BIQ PRTO 0..123, the range the ruleset holds.
		Assert.Equal(31, gd.unitPrototypes.Count(p => p.startsGoldenAge));
		Assert.All(gd.unitPrototypes.Where(p => p.startsGoldenAge), p => {
			Assert.False(p.isArmy);
			Assert.False(p.isLeader);
		});
	}

	[Fact]
	public void TheRulesetMarksOnlyTheHeroicEpicAndTheMilitaryAcademyAsVictoriousArmyBuildings() {
		// Measured from BLDG 54 and 57 of the shipped conquests.biq.
		Assert.Equal(
			new[] { "Heroic Epic", "Military Academy" },
			gd.Buildings.Where(b => b.requiresVictoriousArmy).Select(b => b.name).OrderBy(n => n).ToArray());
	}

	// The three tests above hold the checked-in ruleset to the flags the shipped
	// BIQ sets, but they read the copy, not the source. These probes read the BIQ
	// itself, so the copy cannot drift from the shipped data unnoticed. They need
	// a Civ3 install and therefore skip in CI, exactly like every other
	// Civ3-dependent test here; ran locally with CIV3_HOME they are the reference.
	[SkippableFact]
	public void LeaderArmyAndGoldenAgeUnitFlagsMatchTheShippedBiq() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");

		BiqData biq = BiqData.LoadFile(PathUtils.defaultBicPath);
		List<PRTO> carriers = biq.Prto.Where(p => p.Army || p.Leader || p.StartsGoldenAge).ToList();

		// The Army, the Leader and the 42 StartsGoldenAge carriers.
		Assert.Equal(44, carriers.Count);

		foreach (PRTO prto in carriers) {
			UnitPrototype prototype = gd.unitPrototypes.FirstOrDefault(p => p.name == prto.Name);
			Assert.NotNull(prototype);
			Assert.Equal(
				ModelledBiqFlags(prto).OrderBy(f => f).ToArray(),
				prototype.flags.OrderBy(f => f).ToArray());
		}
	}

	[SkippableFact]
	public void VictoriousArmyBuildingFlagsMatchTheShippedBiq() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");

		BiqData biq = BiqData.LoadFile(PathUtils.defaultBicPath);
		Assert.Equal(
			biq.Bldg.Where(b => b.RequiresVictoriousArmy).Select(b => b.Name).OrderBy(n => n).ToArray(),
			gd.Buildings.Where(b => b.requiresVictoriousArmy).Select(b => b.name).OrderBy(n => n).ToArray());
	}

	// The Blitz and Amphibious flags are what the attack-availability gate and
	// the amphibious assault bonus read, so both carriers — the shipped BIQ and
	// the checked-in ruleset a generated game is built from — are held to each
	// other. The importer de-dupes the BIQ's 141 PRTO entries by name, hence the
	// name-set comparison.
	[SkippableFact]
	public void BlitzAndAmphibiousUnitFlagsMatchTheShippedBiq() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");

		BiqData biq = BiqData.LoadFile(PathUtils.defaultBicPath);
		Assert.Equal(
			biq.Prto.Where(p => p.Blitz).Select(p => p.Name).Distinct().OrderBy(n => n).ToArray(),
			gd.unitPrototypes.Where(p => p.isBlitz).Select(p => p.name).OrderBy(n => n).ToArray());
		Assert.Equal(
			biq.Prto.Where(p => p.Amphibious).Select(p => p.Name).Distinct().OrderBy(n => n).ToArray(),
			gd.unitPrototypes.Where(p => p.isAmphibious).Select(p => p.name).OrderBy(n => n).ToArray());

		// And the ruleset file itself, not just the ruleset-loaded prototypes.
		using JsonDocument ruleset = JsonUtils.LoadBaseRuleset();
		JsonElement prototypes = ruleset.RootElement.GetProperty("unitPrototypes");
		Assert.Equal(
			biq.Prto.Where(p => p.Blitz).Select(p => p.Name).Distinct().OrderBy(n => n).ToArray(),
			NamesWithFlag(prototypes, "blitz").ToArray());
		Assert.Equal(
			biq.Prto.Where(p => p.Amphibious).Select(p => p.Name).Distinct().OrderBy(n => n).ToArray(),
			NamesWithFlag(prototypes, "amphibious").ToArray());
	}

	// The PRTO booleans the importer maps onto SaveUnitPrototype.Flag.
	private static HashSet<SaveUnitPrototype.Flag> ModelledBiqFlags(PRTO prto) {
		HashSet<SaveUnitPrototype.Flag> flags = [];
		if (prto.TurnToAttack) flags.Add(SaveUnitPrototype.Flag.RotateBeforeAttack);
		if (prto.CanCarryFootUnitsOnly) flags.Add(SaveUnitPrototype.Flag.CanCarryFootUnitsOnly);
		if (prto.CanCarryAircraft) flags.Add(SaveUnitPrototype.Flag.CanCarryAircraft);
		if (prto.CanCarryTacticalMissiles) flags.Add(SaveUnitPrototype.Flag.CanCarryTacticalMissiles);
		if (prto.LethalLandBombardment) flags.Add(SaveUnitPrototype.Flag.LethalLandBombardment);
		if (prto.LethalSeaBombardment) flags.Add(SaveUnitPrototype.Flag.LethalSeaBombardment);
		if (prto.Radar) flags.Add(SaveUnitPrototype.Flag.Radar);
		if (prto.Army) flags.Add(SaveUnitPrototype.Flag.Army);
		if (prto.Leader) flags.Add(SaveUnitPrototype.Flag.Leader);
		if (prto.StartsGoldenAge) flags.Add(SaveUnitPrototype.Flag.StartsGoldenAge);
		if (prto.Blitz) flags.Add(SaveUnitPrototype.Flag.Blitz);
		if (prto.Amphibious) flags.Add(SaveUnitPrototype.Flag.Amphibious);
		if (prto.Wheeled) flags.Add(SaveUnitPrototype.Flag.Wheeled);
		return flags;
	}

	// ---------- the leader's rush ----------

	// A player whose shield cost is the building's own cost: the civ has no
	// traits and, as a human, gets no AI discount.
	private Player MakeRushingPlayer() {
		Player player = MakePlayer();
		player.isHuman = true;
		return player;
	}

	private (Player player, City city, MapUnit leader) SetupCityLeader(MapUnit.LeaderKind kind, string buildingName) {
		Player player = MakeRushingPlayer();
		Tile tile = MakeTile();
		City city = MakeCity(player, tile);
		city.SetItemBeingProduced(gd.Buildings.First(b => b.name == buildingName));
		MapUnit leader = MakeUnit(player, leaderType, tile, experience: "Regular");
		leader.leaderKind = kind;
		return (player, city, leader);
	}

	[Fact]
	public void AMilitaryLeaderRushesAnImprovementAndIsConsumed() {
		var (player, city, leader) = SetupCityLeader(MapUnit.LeaderKind.Military, "Temple");
		Building temple = gd.Buildings.First(b => b.name == "Temple");

		Assert.True(leader.CanHurryProduction());
		Assert.True(leader.HurryProduction());

		// The item's whole cost is in the box untouched, so it completes at the
		// owner's next production phase: no gold was paid and nobody died, unlike
		// the ordinary hurry.
		Assert.Equal(player.ShieldCost(temple), city.shieldsStored);
		Assert.True(city.shieldsStored >= player.ShieldCost(temple));
		Assert.DoesNotContain(leader, player.units);
		Assert.DoesNotContain(leader, city.location.unitsOnTile);
	}

	[Fact]
	public void AMilitaryLeaderCannotRushAGreatWonder() {
		var (player, city, leader) = SetupCityLeader(MapUnit.LeaderKind.Military, "The Pyramids");

		Assert.False(leader.CanHurryProduction());
		Assert.False(leader.HurryProduction());
		Assert.Equal(0, city.shieldsStored);
	}

	[Fact]
	public void AScientificLeaderRushesAGreatWonderAndAnImprovement() {
		var (player, city, leader) = SetupCityLeader(MapUnit.LeaderKind.Scientific, "The Pyramids");
		Assert.True(leader.CanHurryProduction());

		// The shared improvement case works for a scientific leader too.
		Building temple = gd.Buildings.First(b => b.name == "Temple");
		city.SetItemBeingProduced(temple);
		Assert.True(leader.CanHurryProduction());
		Assert.True(leader.HurryProduction());
		Assert.Equal(player.ShieldCost(temple), city.shieldsStored);
	}

	[Fact]
	public void ALeaderNeverRushesAUnit() {
		Player player = MakeRushingPlayer();
		Tile tile = MakeTile();
		City city = MakeCity(player, tile);
		city.SetItemBeingProduced(gd.unitPrototypes.First(p => p.name == "Warrior"));

		MapUnit military = MakeUnit(player, leaderType, tile, experience: "Regular");
		military.leaderKind = MapUnit.LeaderKind.Military;
		MapUnit scientific = MakeUnit(player, leaderType, tile, experience: "Regular");
		scientific.leaderKind = MapUnit.LeaderKind.Scientific;

		Assert.False(military.CanHurryProduction());
		Assert.False(scientific.CanHurryProduction());
		Assert.False(military.HurryProduction());
	}

	[Fact]
	public void ALeaderCanOnlyRushInOneOfItsOwnCities() {
		// A leader standing on a tile without a city.
		MapUnit outside = MakeUnit(MakeRushingPlayer(), leaderType, MakeTile(), experience: "Regular");
		outside.leaderKind = MapUnit.LeaderKind.Military;
		Assert.False(outside.CanHurryProduction());

		// A leader standing in another civ's city.
		Player player = MakeRushingPlayer();
		Tile tile = MakeTile();
		City otherCity = MakeCity(MakeRushingPlayer(), tile);
		otherCity.SetItemBeingProduced(gd.Buildings.First(b => b.name == "Temple"));
		MapUnit guest = MakeUnit(player, leaderType, tile, experience: "Regular");
		guest.leaderKind = MapUnit.LeaderKind.Military;
		Assert.False(guest.CanHurryProduction());
	}

	[Fact]
	public void TheRushShieldFloorAppliesOnlyWhenTheCallerAsksForIt() {
		Player player = MakeRushingPlayer();
		Tile tile = MakeTile();
		City city = MakeCity(player, tile);
		MapUnit leader = MakeUnit(player, leaderType, tile, experience: "Regular");
		leader.leaderKind = MapUnit.LeaderKind.Military;

		Building barracks = gd.Buildings.First(b => b.name == "Barracks");
		Assert.True(player.ShieldCost(barracks) < MapUnit.MilitaryLeaderRushMinimumShields);
		city.SetItemBeingProduced(barracks);
		Assert.True(leader.CanHurryProduction());
		Assert.False(leader.CanHurryProduction(requireMinimumShieldCost: true));

		Building bank = gd.Buildings.First(b => b.name == "Bank");
		Assert.True(player.ShieldCost(bank) >= MapUnit.MilitaryLeaderRushMinimumShields);
		city.SetItemBeingProduced(bank);
		Assert.True(leader.CanHurryProduction(requireMinimumShieldCost: true));

		// The scientific floor is higher, so the Bank is still refused for a
		// scientific leader while the University is not.
		leader.leaderKind = MapUnit.LeaderKind.Scientific;
		Assert.True(player.ShieldCost(bank) < MapUnit.ScientificLeaderRushMinimumShields);
		Assert.False(leader.CanHurryProduction(requireMinimumShieldCost: true));

		Building university = gd.Buildings.First(b => b.name == "University");
		Assert.True(player.ShieldCost(university) >= MapUnit.ScientificLeaderRushMinimumShields);
		city.SetItemBeingProduced(university);
		Assert.True(leader.CanHurryProduction(requireMinimumShieldCost: true));
	}

	[Fact]
	public void ALeaderOfUnsetKindRushesOnlyBuildingLikeItems() {
		Player player = MakeRushingPlayer();
		Tile tile = MakeTile();
		City city = MakeCity(player, tile);
		MapUnit leader = MakeUnit(player, leaderType, tile, experience: "Regular");
		Assert.Equal(MapUnit.LeaderKind.None, leader.leaderKind);

		// A plain improvement carries no wonder flag and no required building.
		city.SetItemBeingProduced(gd.Buildings.First(b => b.name == "Temple"));
		Assert.False(leader.CanHurryProduction());

		// A small wonder is accepted, so is an improvement that requires
		// another building, and so is a Great Wonder.
		city.SetItemBeingProduced(gd.Buildings.First(b => b.name == "Heroic Epic"));
		Assert.True(leader.CanHurryProduction());
		city.SetItemBeingProduced(gd.Buildings.First(b => b.name == "Cathedral"));
		Assert.True(leader.CanHurryProduction());
		city.SetItemBeingProduced(gd.Buildings.First(b => b.name == "The Pyramids"));
		Assert.True(leader.CanHurryProduction());
	}

	// ---------- the victorious-army status bit ----------

	[Fact]
	public void AVictoryByAUnitCarriedInAnArmySetsTheVictoriousArmyFlag() {
		var (player, army) = SetupArmy();
		MapUnit member = MakeUnit(player, MakeLandPrototype(4, 2), army.location);
		Assert.True(member.LoadIntoArmy(army));
		Assert.True(member.IsCarriedInsideArmy());

		ScriptedRandom rng = UseScriptedRandom();
		rng.nextIntResult = 1;
		Assert.False(player.hasVictoriousArmy);

		member.RollForCombatOutcome(MakeUnit(MakePlayer(), MakeLandPrototype(1, 1), MakeTile()), defeatedWasTheDefender: true);

		Assert.True(player.hasVictoriousArmy);
	}

	[Fact]
	public void OnlyAVictoryFromInsideAnArmySetsTheVictoriousArmyFlag() {
		ScriptedRandom rng = UseScriptedRandom();
		rng.nextIntResult = 1;

		// A plain unit on the map never sets it.
		Player player = MakePlayer();
		MapUnit lone = MakeUnit(player, MakeLandPrototype(4, 2), MakeTile());
		lone.RollForCombatOutcome(MakeUnit(MakePlayer(), MakeLandPrototype(1, 1), MakeTile()), defeatedWasTheDefender: true);
		Assert.False(player.hasVictoriousArmy);

		// Neither does a unit carried by an ordinary transport.
		Player carried = MakePlayer();
		Tile tile = MakeTile();
		UnitPrototype sea = MakeLandPrototype(1, 1);
		sea.categories.Clear();
		sea.categories.Add("Sea");
		MapUnit transport = MakeUnit(carried, sea, tile);
		MapUnit passenger = MakeUnit(carried, MakeLandPrototype(4, 2), tile);
		passenger.loadedOnUnitId = transport.id;
		Assert.False(passenger.IsCarriedInsideArmy());
		passenger.RollForCombatOutcome(MakeUnit(MakePlayer(), MakeLandPrototype(1, 1), MakeTile()), defeatedWasTheDefender: true);
		Assert.False(carried.hasVictoriousArmy);
	}

	// ---------- the used-attack state and the Blitz gate ----------

	// A map tile the fixture's generator produced, emptied of units and
	// features, for attacks that go through MapUnit.Move.
	private Tile CleanMapTile(int x, int y) {
		Tile tile = gd.map.tileAt(x, y);
		tile.unitsOnTile.Clear();
		tile.overlays.Clear();
		tile.hasGoodyHut = false;
		tile.hasBarbarianCamp = false;
		tile.overlayTerrainType = new TerrainType { Key = "test" };
		tile.baseTerrainType = tile.overlayTerrainType;
		return tile;
	}

	private UnitPrototype MakeBlitzPrototype(int attack, int defense, int movement) {
		UnitPrototype proto = MakeLandPrototype(attack, defense, movement);
		proto.flags.Add(SaveUnitPrototype.Flag.Blitz);
		return proto;
	}

	// The Blitz gate at its boundary: a non-Blitz unit that has attacked is
	// refused a second attack even though movement points remain, so the second
	// defender is untouched and the attacker never moves. The attack goes
	// through MapUnit.Move, the branch the human's MsgMoveUnit handler and the
	// AI's CombatAI both use.
	[Fact]
	public void AUnitThatAttackedIsRefusedASecondAttackEvenWithMovementLeft() {
		Tile attackerTile = CleanMapTile(50, 50);
		Tile defenderTile = CleanMapTile(52, 50);
		Player player = MakePlayer();
		MapUnit attacker = MakeUnit(player, MakeLandPrototype(100, 0, movement: 3), attackerTile);
		MapUnit first = MakeUnit(MakePlayer(barbarian: true), MakeLandPrototype(0, 0), defenderTile);
		MapUnit second = MakeUnit(MakePlayer(barbarian: true), MakeLandPrototype(0, 0), defenderTile);

		ScriptedRandom rng = UseScriptedRandom();
		rng.nextIntResult = MapUnit.CombatOddsScale - 1; // the attacker wins every round

		// The first attack kills the first defender and leaves the second one on
		// the tile, so the unit is still adjacent to an enemy with movement to
		// spare.
		Assert.True(attacker.Move(TileDirection.EAST).Result);
		Assert.True(attacker.hasUsedAttack);
		Assert.DoesNotContain(first, gd.mapUnits);
		Assert.Same(attackerTile, attacker.location);
		Assert.True(attacker.movementPoints.canMove);
		Assert.False(attacker.unitType.isBlitz);

		// The second attack is refused: the used-attack bit blocks it even
		// though movement points remain.
		Assert.True(attacker.Move(TileDirection.EAST).Result);
		Assert.Same(attackerTile, attacker.location);
		Assert.Contains(second, gd.mapUnits);
		Assert.Equal(second.maxHitPoints, second.hitPointsRemaining);
	}

	// The Blitz escape: the same setup with a Blitz type attacks twice in one
	// turn. Once the movement points are gone the unit still may attack in
	// principle - only the movement points stop it.
	[Fact]
	public void ABlitzUnitAttacksAgainInTheSameTurnUntilItsMovementRunsOut() {
		Tile attackerTile = CleanMapTile(50, 50);
		Tile defenderTile = CleanMapTile(52, 50);
		Player player = MakePlayer();
		MapUnit attacker = MakeUnit(player, MakeBlitzPrototype(100, 0, movement: 2), attackerTile);
		MapUnit first = MakeUnit(MakePlayer(barbarian: true), MakeLandPrototype(0, 0), defenderTile);
		MapUnit second = MakeUnit(MakePlayer(barbarian: true), MakeLandPrototype(0, 0), defenderTile);

		ScriptedRandom rng = UseScriptedRandom();
		rng.nextIntResult = MapUnit.CombatOddsScale - 1;

		Assert.True(attacker.Move(TileDirection.EAST).Result);
		Assert.True(attacker.Move(TileDirection.EAST).Result);

		Assert.True(attacker.hasUsedAttack);
		Assert.DoesNotContain(first, gd.mapUnits);
		Assert.DoesNotContain(second, gd.mapUnits);
		// The bit does not block the Blitz type; the exhausted movement points
		// do.
		Assert.True(attacker.CanAttack());
		Assert.False(attacker.movementPoints.canMove);
	}

	[Fact]
	public void OnlyABlitzTypeMayAttackTwiceInOneTurn() {
		Player player = MakePlayer();
		MapUnit plain = MakeUnit(player, MakeLandPrototype(4, 2, movement: 3), MakeTile());
		MapUnit blitz = MakeUnit(player, MakeBlitzPrototype(4, 2, movement: 3), MakeTile());

		Assert.True(plain.CanAttack());
		Assert.True(blitz.CanAttack());

		plain.hasUsedAttack = true;
		blitz.hasUsedAttack = true;
		Assert.False(plain.CanAttack());
		Assert.True(blitz.CanAttack());

		// The refused unit still has all its movement points.
		Assert.True(plain.movementPoints.canMove);
	}

	// A refused attack must not leave the ordered move pending. Both movement
	// drivers re-issue the move while the unit has movement points left — the
	// AI's UnitAI.PlayTurn loop and the goto path's MoveAlongPath — so a refusal
	// that spends nothing and keeps the path would spin on the same tile for as
	// long as the unit can move. The loop here is a bounded copy of the AI's, so
	// a regression fails the assertions instead of hanging the suite.
	[Fact]
	public void ARefusedAttackEndsTheOrderedMoveInsteadOfSpinning() {
		Tile attackerTile = CleanMapTile(50, 50);
		Tile defenderTile = CleanMapTile(52, 50);
		Player player = MakePlayer();
		MapUnit attacker = MakeUnit(player, MakeLandPrototype(100, 0, movement: 3), attackerTile);
		MapUnit first = MakeUnit(MakePlayer(barbarian: true), MakeLandPrototype(0, 0), defenderTile);
		MapUnit second = MakeUnit(MakePlayer(barbarian: true), MakeLandPrototype(0, 0), defenderTile);

		ScriptedRandom rng = UseScriptedRandom();
		rng.nextIntResult = MapUnit.CombatOddsScale - 1;

		// The first attack kills the first defender and leaves the second one on
		// the tile, so the unit still has movement points to spare.
		Assert.True(attacker.Move(TileDirection.EAST).Result);
		Assert.DoesNotContain(first, gd.mapUnits);
		Assert.True(attacker.movementPoints.canMove);

		// The ordered move is still aiming at that tile.
		attacker.path = new TilePath(defenderTile, new Queue<Tile>([defenderTile]));

		int attempts = 0;
		while (attacker.movementPoints.canMove && attempts < 5) {
			++attempts;
			Assert.True(attacker.Move(TileDirection.EAST).Result);
		}

		// One refusal and the loop is done: no movement left and no path left.
		Assert.Equal(1, attempts);
		Assert.False(attacker.movementPoints.canMove);
		Assert.Equal(TilePath.NONE, attacker.path);
		Assert.Contains(second, gd.mapUnits);
		Assert.Same(attackerTile, attacker.location);
	}

	// The state is per turn: the per-turn unit update clears it with the
	// movement points, so it never survives into the next turn.
	[Fact]
	public void TheUsedAttackStateResetsAtTheTurnBoundary() {
		Player player = MakePlayer();
		MapUnit unit = MakeUnit(player, MakeLandPrototype(4, 2, movement: 2), MakeTile());
		unit.hasUsedAttack = true;
		unit.movementPoints.onUnitMove(1);
		Assert.False(unit.CanAttack());

		TurnHandling.InitTurnData(player);

		Assert.False(unit.hasUsedAttack);
		Assert.True(unit.CanAttack());
		Assert.Equal(2f, unit.movementPoints.remaining);

		// A direct begin-turn does the same.
		unit.hasUsedAttack = true;
		unit.OnBeginTurn();
		Assert.False(unit.hasUsedAttack);
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
		player.hasVictoriousArmy = true;

		// A leader that has already produced one leader.
		MapUnit leader = MakeUnit(player, leaderType, tile, experience: "Regular");
		leader.leaderKind = MapUnit.LeaderKind.Scientific;
		leader.hasProducedLeader = true;

		// An army carrying two members, which has already attacked this turn.
		UnitPrototype memberType = gd.unitPrototypes.First(p => p.name == "Warrior");
		MapUnit army = MakeUnit(player, armyType, tile, experience: "Regular");
		army.hitPointsRemaining = army.maxHitPoints;
		army.hasUsedAttack = true;
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
			Assert.True(reloadedPlayer.hasVictoriousArmy);

			MapUnit reloadedLeader = reloaded.mapUnits.First(u => u.id == leader.id);
			Assert.Equal(MapUnit.LeaderKind.Scientific, reloadedLeader.leaderKind);
			Assert.True(reloadedLeader.hasProducedLeader);
			Assert.True(reloadedLeader.unitType.isLeader);

			MapUnit reloadedArmy = reloaded.mapUnits.First(u => u.id == army.id);
			Assert.True(reloadedArmy.IsArmy);
			Assert.Equal(2, reloadedArmy.Members().Count);
			Assert.Equal(6, reloadedArmy.maxHitPoints);
			Assert.Equal(6, reloadedArmy.hitPointsRemaining);
			Assert.True(reloadedArmy.hasUsedAttack);
			Assert.True(reloadedArmy.CanAttack(), "the reloaded army is a Blitz type");
		} finally {
			System.IO.File.Delete(path);
		}
	}
}
