using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using C7Engine;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using QueryCiv3;
using QueryCiv3.Biq;
using Xunit;

namespace EngineTests.GameData;

// Tests for re/specs/12_combat.md sections 2, 5 and 6: the combat resolver's
// odds on the 1/1024 scale, the multiplicative defence bonuses, the river and
// anti-barbarian bonuses, one hit point of damage per round, the retreat rule
// and the defender picker.
//
// The values the resolver reads from the shipped data (the difficulty list,
// the EXPR retreat bonuses, the RULE percentages and the Great Wall's flag)
// are asserted both against the checked-in ruleset and, where a Civ3 install
// is needed, against the shipped conquests.biq itself.
public class CombatResolverTest : IClassFixture<SaveGameFixture> {
	private readonly SaveGameFixture fixture;
	private readonly C7GameData.GameData gd;

	// A Random whose integer rolls are scripted by the test, so that the exact
	// odds boundary and the order of the rolls can be asserted. Every bound
	// the production code rolls against is recorded.
	private class ScriptedRandom : Random {
		public readonly Queue<int> intResults = new();
		public readonly List<int> requestedBounds = new();
		public int fallback;
		public double nextDoubleResult = 1.0;

		public override int Next(int maxValue) {
			requestedBounds.Add(maxValue);
			return intResults.Count > 0 ? intResults.Dequeue() % maxValue : fallback % maxValue;
		}

		public override double NextDouble() => nextDoubleResult;

		public int RollsAgainst(int bound) => requestedBounds.Count(b => b == bound);
	}

	public CombatResolverTest(SaveGameFixture fixture) {
		this.fixture = fixture;
		gd = fixture.saveGame.ToGameData(fixture.behaviors);
		EngineStorage.InitializeGameDataForTests(gd);
		EngineStorage.animationsEnabled = false;

		// The shipped default difficulty (Regent, AttackBonusAgainstBarbarians
		// 200, measured from conquests.biq RULE.DefaultDifficultyLevel = 2).
		gd.gameDifficulty = gd.difficulties.First(d => d.Name == "Regent");
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

	// A flat, featureless tile: no terrain defence percentage, no river, no
	// city, no improvements.
	private Tile MakeFlatTile() {
		Tile tile = new Tile(gd.GenerateID("tile")) {
			overlayTerrainType = new TerrainType { Key = "test" },
		};
		tile.baseTerrainType = tile.overlayTerrainType;
		tile.overlays.Clear();
		return tile;
	}

	private UnitPrototype MakePrototype(int attack, int defense, int movement = 1, int bombard = 0, int rateOfFire = 0) {
		UnitPrototype proto = new UnitPrototype {
			name = $"CombatTest-{attack}-{defense}-{movement}-{bombard}-{rateOfFire}",
			attack = attack,
			defense = defense,
			movement = movement,
			bombard = bombard,
			rateOfFire = rateOfFire,
		};
		proto.categories.Add("Land");
		return proto;
	}

	private MapUnit MakeUnit(Player owner, UnitPrototype proto, Tile location, string experience = "Regular") {
		MapUnit unit = proto.GetInstance(gd.GenerateID(proto.name), proto, owner, location: location);
		unit.experienceLevel = gd.experienceLevels.First(e => e.key == experience);
		unit.experienceLevelKey = experience;
		unit.hitPointsRemaining = unit.maxHitPoints;
		unit.movementPoints.reset(proto.movement);
		location.unitsOnTile.Add(unit);
		gd.mapUnits.Add(unit);
		owner.AddUnit(unit);
		return unit;
	}

	private static ScriptedRandom UseScriptedRandom() {
		ScriptedRandom rng = new ScriptedRandom();
		C7GameData.GameData.rng = rng;
		return rng;
	}

	// A land tile of the fixture's real generated map, emptied of everything
	// the generator may have left on it, for tests that fight through MapUnit.Fight.
	private Tile CleanMapTile(int x, int y) {
		Tile tile = gd.map.tileAt(x, y);
		tile.unitsOnTile.Clear();
		tile.overlays.Clear();
		tile.hasGoodyHut = false;
		tile.hasBarbarianCamp = false;
		// Flat terrain: the generator's real terrain carries a defence
		// percentage, and these tests want the odds under their own control.
		tile.overlayTerrainType = new TerrainType { Key = "test" };
		tile.baseTerrainType = tile.overlayTerrainType;
		return tile;
	}

	// ---------- odds: scale, clamping and rounding ----------

	[Theory]
	[InlineData(1, 1, 512)]
	[InlineData(1, 2, 682)]
	[InlineData(2, 1, 341)]
	[InlineData(3, 1, 256)]
	[InlineData(1, 3, 768)]
	[InlineData(100, 1, 10)]
	[InlineData(1, 100, 1013)]
	public void OddsAreDefenderStrengthOverTheSumOnThe1024Scale(int attack, int defense, int expected) {
		Player attackerPlayer = MakePlayer();
		Player defenderPlayer = MakePlayer();
		MapUnit attacker = MakeUnit(attackerPlayer, MakePrototype(attack, defense: 0), MakeFlatTile());
		MapUnit defender = MakeUnit(defenderPlayer, MakePrototype(attack: 0, defense), MakeFlatTile());

		Assert.Equal(expected, MapUnit.DefenderCombatOdds(attacker, defender, null));
	}

	[Theory]
	[InlineData(1, 0)]
	[InlineData(100, 0)]
	[InlineData(0, 0)]
	public void TheDefendersOddsAreNeverExactlyZeroOr1024(int attack, int defense) {
		Player attackerPlayer = MakePlayer();
		Player defenderPlayer = MakePlayer();
		MapUnit attacker = MakeUnit(attackerPlayer, MakePrototype(attack, defense), MakeFlatTile());
		MapUnit defender = MakeUnit(defenderPlayer, MakePrototype(attack, defense), MakeFlatTile());

		int odds = MapUnit.DefenderCombatOdds(attacker, defender, null);
		Assert.InRange(odds, MapUnit.MinCombatOdds, MapUnit.MaxCombatOdds);
		Assert.NotEqual(0, odds);
		Assert.NotEqual(MapUnit.CombatOddsScale, odds);
	}

	// A defender with zero strength still cannot lose the fight for certain
	// (odds 1 of 1024), and an attacker with zero strength still cannot win it
	// for certain (odds 1023 of 1024).
	[Fact]
	public void TheClampKeepsBothSidesInTheFight() {
		Player attackerPlayer = MakePlayer();
		Player defenderPlayer = MakePlayer();
		MapUnit strongAttacker = MakeUnit(attackerPlayer, MakePrototype(100, 0), MakeFlatTile());
		MapUnit defenceless = MakeUnit(defenderPlayer, MakePrototype(0, 0), MakeFlatTile());
		MapUnit strongDefender = MakeUnit(defenderPlayer, MakePrototype(0, 100), MakeFlatTile());

		// A defender with zero strength still cannot lose the fight for
		// certain (odds 1 of 1024), and an attacker with zero strength still
		// cannot win it for certain (odds 1023 of 1024).
		Assert.Equal(MapUnit.MinCombatOdds, MapUnit.DefenderCombatOdds(strongAttacker, defenceless, null));
		Assert.Equal(MapUnit.MaxCombatOdds, MapUnit.DefenderCombatOdds(defenceless, strongDefender, null));
		// With no strength on either side there is still a roll to lose.
		Assert.Equal(MapUnit.MinCombatOdds, MapUnit.DefenderCombatOdds(defenceless, defenceless, null));
	}

	// ---------- the multiplicative rule ----------

	// A +100% defence bonus must double the defender's effective strength:
	// 1024 * 2 / (1 + 2) = 682. If percentages were added to strength instead
	// of scaling it, the effective strength would be 1 + 100 and the odds
	// would be 1024 * 101 / 102 = 1013 — this test fails on that.
	[Fact]
	public void ADefenceBonusOfOneHundredPercentDoublesStrength() {
		Player attackerPlayer = MakePlayer();
		Player defenderPlayer = MakePlayer();
		MapUnit attacker = MakeUnit(attackerPlayer, MakePrototype(1, 0), MakeFlatTile());

		Tile mountain = MakeFlatTile();
		mountain.overlayTerrainType.defenseBonus = new StrengthBonus("mountains", 1.0);
		mountain.baseTerrainType = mountain.overlayTerrainType;
		MapUnit defender = MakeUnit(defenderPlayer, MakePrototype(0, 1), mountain);

		var (attackerEffective, defenderEffective) =
			C7GameData.MapUnit.EffectiveCombatStrengths(attacker, defender, null);
		Assert.Equal(100, attackerEffective);
		Assert.Equal(200, defenderEffective);
		Assert.Equal(682, MapUnit.DefenderCombatOdds(attacker, defender, null));
	}

	// ---------- the river bonus ----------

	// A river on the defender's tile facing the attacker adds exactly
	// RULE.RiverDefensiveBonus (25, measured from the shipped BIQ) to the
	// defence side of the crossing, and nothing to the attack side.
	[Fact]
	public void ARiverCrossingAddsTwentyFiveToTheDefenceSide() {
		Player attackerPlayer = MakePlayer();
		Player defenderPlayer = MakePlayer();
		Tile attackerTile = MakeFlatTile();
		Tile defenderTile = MakeFlatTile();
		MapUnit attacker = MakeUnit(attackerPlayer, MakePrototype(1, 0), attackerTile);
		MapUnit defender = MakeUnit(defenderPlayer, MakePrototype(0, 1), defenderTile);

		// The attacker stands west of the defender; the defender's tile holds
		// the river edge on its western side.
		defenderTile.riverWest = true;

		var (attackerEffective, defenderEffective) =
			C7GameData.MapUnit.EffectiveCombatStrengths(attacker, defender, TileDirection.EAST);
		Assert.Equal(100, attackerEffective);
		Assert.Equal(125, defenderEffective);
		Assert.Equal(568, MapUnit.DefenderCombatOdds(attacker, defender, TileDirection.EAST));
	}

	[Fact]
	public void TheRiverOnTheAttackersOwnTileGivesItNothing() {
		Player attackerPlayer = MakePlayer();
		Player defenderPlayer = MakePlayer();
		Tile attackerTile = MakeFlatTile();
		Tile defenderTile = MakeFlatTile();
		MapUnit attacker = MakeUnit(attackerPlayer, MakePrototype(1, 0), attackerTile);
		MapUnit defender = MakeUnit(defenderPlayer, MakePrototype(0, 1), defenderTile);

		// The river edge faces away from the defender: no side gets anything.
		attackerTile.riverWest = true;

		var (attackerEffective, defenderEffective) =
			C7GameData.MapUnit.EffectiveCombatStrengths(attacker, defender, TileDirection.EAST);
		Assert.Equal(100, attackerEffective);
		Assert.Equal(100, defenderEffective);
	}

	// ---------- the amphibious assault bonus ----------

	// A flat water tile: an attacker standing on it is attacking from water
	// (12_combat.md §2.1.2).
	private Tile MakeWaterTile() {
		Tile tile = new Tile(gd.GenerateID("tile")) {
			overlayTerrainType = new TerrainType { Key = "sea" },
		};
		tile.baseTerrainType = tile.overlayTerrainType;
		tile.overlays.Clear();
		return tile;
	}

	private UnitPrototype MakeAmphibiousPrototype(int attack, int defense, int movement = 1) {
		UnitPrototype proto = MakePrototype(attack, defense, movement);
		proto.flags.Add(SaveUnitPrototype.Flag.Amphibious);
		return proto;
	}

	// +25% for an Amphibious land unit with a positive attack strength that
	// attacks from water onto a non-water tile (12_combat.md §2.1.2): the
	// attacker's effective strength goes from 4 * 100 to 4 * 125, and the
	// defender's odds from 512 to 455.
	[Fact]
	public void AnAmphibiousAssaultFromWaterAddsTwentyFivePercent() {
		MapUnit attacker = MakeUnit(MakePlayer(), MakeAmphibiousPrototype(4, 0), MakeWaterTile());
		MapUnit defender = MakeUnit(MakePlayer(), MakePrototype(0, 4), MakeFlatTile());

		var (attackerEffective, defenderEffective) =
			MapUnit.EffectiveCombatStrengths(attacker, defender, TileDirection.WEST);

		Assert.Equal(500, attackerEffective);
		Assert.Equal(400, defenderEffective);
		Assert.Equal(455, MapUnit.DefenderCombatOdds(attacker, defender, TileDirection.WEST));
	}

	// The bonus needs every one of its conditions: the Amphibious ability, a
	// land type, a positive attack strength, the attacker on water and the
	// defender off it.
	[Fact]
	public void TheAmphibiousBonusNeedsWaterAndTheAbility() {
		MapUnit defender = MakeUnit(MakePlayer(), MakePrototype(0, 4), MakeFlatTile());

		MapUnit plain = MakeUnit(MakePlayer(), MakePrototype(4, 0), MakeWaterTile());
		Assert.Equal(400, MapUnit.EffectiveCombatStrengths(plain, defender, null).attackerEffective);

		MapUnit onLand = MakeUnit(MakePlayer(), MakeAmphibiousPrototype(4, 0), MakeFlatTile());
		Assert.Equal(400, MapUnit.EffectiveCombatStrengths(onLand, defender, null).attackerEffective);

		MapUnit onWater = MakeUnit(MakePlayer(), MakeAmphibiousPrototype(4, 0), MakeWaterTile());
		MapUnit waterDefender = MakeUnit(MakePlayer(), MakePrototype(0, 4), MakeWaterTile());
		Assert.Equal(400, MapUnit.EffectiveCombatStrengths(onWater, waterDefender, null).attackerEffective);

		MapUnit defenceless = MakeUnit(MakePlayer(), MakeAmphibiousPrototype(0, 0), MakeWaterTile());
		Assert.Equal(0, MapUnit.EffectiveCombatStrengths(defenceless, defender, null).attackerEffective);
	}

	// The bonus reads the attacker's used-attack status bit: a unit that has
	// already attacked this turn loses it unless its type can Blitz
	// (12_combat.md §2.1.2). This is the helper-level view; the two tests below
	// drive a real Fight and show that the bit is raised only after the odds.
	[Fact]
	public void TheAmphibiousBonusReadsTheUsedAttackBitUnlessTheTypeCanBlitz() {
		MapUnit defender = MakeUnit(MakePlayer(), MakePrototype(0, 4), MakeFlatTile());
		MapUnit attacker = MakeUnit(MakePlayer(), MakeAmphibiousPrototype(4, 0), MakeWaterTile());

		Assert.False(attacker.hasUsedAttack);
		Assert.True(attacker.GetsAmphibiousAssaultBonus(defender));
		Assert.Equal(500, MapUnit.EffectiveCombatStrengths(attacker, defender, null).attackerEffective);

		attacker.hasUsedAttack = true;
		Assert.False(attacker.GetsAmphibiousAssaultBonus(defender));
		Assert.Equal(400, MapUnit.EffectiveCombatStrengths(attacker, defender, null).attackerEffective);

		MapUnit blitz = MakeUnit(MakePlayer(), MakeAmphibiousPrototype(4, 0), MakeWaterTile());
		blitz.unitType.flags.Add(SaveUnitPrototype.Flag.Blitz);
		blitz.hasUsedAttack = true;
		Assert.True(blitz.GetsAmphibiousAssaultBonus(defender));
		Assert.Equal(500, MapUnit.EffectiveCombatStrengths(blitz, defender, null).attackerEffective);
	}

	// The same, but water, so an attacker standing on it is attacking from the
	// sea (12_combat.md §2.1.2).
	private Tile CleanWaterMapTile(int x, int y) {
		Tile tile = CleanMapTile(x, y);
		tile.overlayTerrainType = new TerrainType { Key = "sea" };
		tile.baseTerrainType = tile.overlayTerrainType;
		return tile;
	}

	// The +25% belongs to the FIRST attack's odds. A fight marks the attacker as
	// having used its attack (12_combat.md §6.2 step 1), so if that mark were
	// raised before the odds were computed the bonus could never reach a real
	// attack at all: a non-Blitz type gets exactly one attack per turn and would
	// find its own used-attack bit already set while computing it.
	//
	// The boundary is sharp. Attack 4 with the bonus is 500 against defence 400,
	// so the defender's odds are 455 and a round roll of 500 goes to the
	// attacker; without the bonus the odds are 512 and the same roll kills the
	// attacker. Both units start at one hit point, so that one roll decides.
	[Fact]
	public void TheAmphibiousAssaultBonusBelongsToTheFirstRealAttack() {
		Tile attackerTile = CleanWaterMapTile(50, 50);
		Tile defenderTile = CleanMapTile(52, 50);
		MapUnit attacker = MakeUnit(MakePlayer(), MakeAmphibiousPrototype(4, 0), attackerTile);
		MapUnit defender = MakeUnit(MakePlayer(), MakePrototype(0, 4), defenderTile);
		attacker.hitPointsRemaining = 1;
		defender.hitPointsRemaining = 1;

		Assert.False(attacker.hasUsedAttack);
		ScriptedRandom rng = UseScriptedRandom();
		rng.fallback = 500;

		Assert.Equal(CombatResult.DefenderKilled, attacker.Fight(defender).Result);
		Assert.True(attacker.hasUsedAttack);
	}

	// The bit is still the gate it was: an attack made while it is already set
	// gets no bonus unless the type can Blitz (12_combat.md §2.1.2). Production
	// refuses that second attack in Move; Fight is called directly here to
	// exercise the odds it would have used.
	[Fact]
	public void AnAmphibiousUnitThatAlreadyAttackedLosesTheBonusInTheOdds() {
		Tile attackerTile = CleanWaterMapTile(50, 50);
		Tile defenderTile = CleanMapTile(52, 50);
		MapUnit attacker = MakeUnit(MakePlayer(), MakeAmphibiousPrototype(4, 0), attackerTile);
		MapUnit defender = MakeUnit(MakePlayer(), MakePrototype(0, 4), defenderTile);
		attacker.hitPointsRemaining = 1;
		defender.hitPointsRemaining = 1;
		attacker.hasUsedAttack = true;

		ScriptedRandom rng = UseScriptedRandom();
		rng.fallback = 500;

		// Odds 512 without the bonus, so the roll of 500 loses the round.
		Assert.Equal(CombatResult.AttackerKilled, attacker.Fight(defender).Result);
	}

	// ---------- the anti-barbarian bonus ----------

	// A barbarian attacker gives the defender DIFF.AttackBonusAgainstBarbarians
	// (200 at the shipped default, Regent) as a percentage — a tripling of the
	// defender's effective strength. Two non-barbarians get nothing.
	[Fact]
	public void TheBarbarianBonusTripliesTheNonBarbarianSideAtRegent() {
		Player barbarianPlayer = MakePlayer(barbarian: true);
		Player player = MakePlayer();

		MapUnit barbarianAttacker = MakeUnit(barbarianPlayer, MakePrototype(1, 0), MakeFlatTile());
		MapUnit defender = MakeUnit(player, MakePrototype(0, 1), MakeFlatTile());
		Assert.Equal(300, C7GameData.MapUnit.EffectiveCombatStrengths(barbarianAttacker, defender, null).defenderEffective);

		MapUnit attacker = MakeUnit(player, MakePrototype(1, 0), MakeFlatTile());
		MapUnit barbarianDefender = MakeUnit(barbarianPlayer, MakePrototype(0, 1), MakeFlatTile());
		Assert.Equal(300, C7GameData.MapUnit.EffectiveCombatStrengths(attacker, barbarianDefender, null).attackerEffective);

		// Between two non-barbarians the bonus does not apply.
		MapUnit other = MakeUnit(MakePlayer(), MakePrototype(0, 1), MakeFlatTile());
		var (plainAttacker, plainDefender) = C7GameData.MapUnit.EffectiveCombatStrengths(attacker, other, null);
		Assert.Equal(100, plainAttacker);
		Assert.Equal(100, plainDefender);
	}

	// The Great Wall (the only shipped building with the
	// DoubleCombatVsBarbarians flag, measured from the BIQ) adds a flat +100%
	// on top of the difficulty bonus for its owner.
	[Fact]
	public void OwningTheGreatWallAddsAHundredAgainstBarbarians() {
		Player barbarianPlayer = MakePlayer(barbarian: true);
		Player player = MakePlayer();
		City city = new City(MakeFlatTile(), player, "WallCity", gd.GenerateID("city")) {
			capital = true,
		};
		player.cities.Add(city);
		city.AddBuilding(gd.Buildings.First(b => b.doublesCombatVsBarbarians));

		Assert.Equal(300, C7GameData.MapUnit.AntiBarbarianBonusPercent(player));

		MapUnit barbarianAttacker = MakeUnit(barbarianPlayer, MakePrototype(1, 0), MakeFlatTile());
		MapUnit defender = MakeUnit(player, MakePrototype(0, 1), MakeFlatTile());
		Assert.Equal(400, C7GameData.MapUnit.EffectiveCombatStrengths(barbarianAttacker, defender, null).defenderEffective);
		// The defender fights with 300 (Regent) + 100 (Great Wall) on top of
		// its 100%: 400 vs 100, so 1024 * 400 / 500 = 819.
		Assert.Equal(819, MapUnit.DefenderCombatOdds(barbarianAttacker, defender, null));
	}

	// The whole anti-barbarian value list of the shipped ruleset, so a changed
	// difficulty value cannot slip through unnoticed.
	[Fact]
	public void TheShippedRulesetCarriesTheWholeDifficultyBonusList() {
		Assert.Equal(
			new[] { "Chieftain", "Warlord", "Regent", "Monarch", "Emperor", "Demigod", "Deity", "Creator" },
			gd.difficulties.Select(d => d.Name).ToArray());
		Assert.Equal(
			new[] { 800, 400, 200, 100, 50, 25, 0, 0 },
			gd.difficulties.Select(d => d.AttackBonusAgainstBarbarians).ToArray());
	}

	// The EXPR values the retreat rule reads, and the RULE-derived bonuses the
	// resolver scales by, asserted from the checked-in ruleset.
	[Fact]
	public void TheShippedRulesetCarriesTheCombatValues() {
		Assert.Equal(
			new[] { 2, 3, 4, 5 },
			gd.experienceLevels.Select(e => e.baseHitPoints).ToArray());
		Assert.Equal(
			new[] { 34, 50, 58, 66 },
			gd.experienceLevels.Select(e => e.retreatBonus).ToArray());

		Assert.Equal(0.25, gd.fortificationBonus.amount);
		Assert.Equal(0.25, gd.riverCrossingBonus.amount);
		Assert.Equal(0.0, gd.cityLevel1DefenseBonus.amount);
		Assert.Equal(0.5, gd.cityLevel2DefenseBonus.amount);
		Assert.Equal(1.0, gd.cityLevel3DefenseBonus.amount);

		// Fortress 50 and barricade 100, as percentage scalings.
		Assert.Equal(0.5, gd.terrainImprovements.First(i => i.key == Tile.TileOverlays.FORTRESS).defenseBonus?.amount);
		Assert.Equal(1.0, gd.terrainImprovements.First(i => i.key == Tile.TileOverlays.BARRICADE).defenseBonus?.amount);

		// Exactly one building carries the doubled-combat-versus-barbarians
		// flag, and it is The Great Wall.
		Assert.Equal(
			new[] { "The Great Wall" },
			gd.Buildings.Where(b => b.doublesCombatVsBarbarians).Select(b => b.name).ToArray());
	}

	// The same values, read from the shipped conquests.biq itself rather than
	// the checked-in copy. Needs a Civ3 install, exactly like the rest of the
	// Civ3-dependent suite.
	[SkippableFact]
	public void TheShippedConquestsBiqCarriesTheCombatValues() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");

		BiqData biq = BiqData.LoadFile(PathUtils.defaultBicPath);

		// The whole shipped difficulty list, in order.
		Assert.Equal(
			new[] { "Chieftain", "Warlord", "Regent", "Monarch", "Emperor", "Demigod", "Deity", "Sid" },
			biq.Diff.Select(d => d.Name).ToArray());
		Assert.Equal(
			new[] { 800, 400, 200, 100, 50, 25, 0, 0 },
			biq.Diff.Select(d => d.AttackBonusAgainstBarbarians).ToArray());
		Assert.Equal(2, biq.Rule[0].DefaultDifficultyLevel);

		// The EXPR records the retreat arithmetic reads.
		Assert.Equal(
			new[] { 34, 50, 58, 66 },
			biq.Expr.Select(e => e.RetreatBonus).ToArray());
		Assert.Equal(
			new[] { 2, 3, 4, 5 },
			biq.Expr.Select(e => e.BaseHitPoints).ToArray());

		// The RULE constants the defence bonuses scale by.
		Assert.Equal(25, biq.Rule[0].RiverDefensiveBonus);
		Assert.Equal(50, biq.Rule[0].FortressDefensiveBonus);
		Assert.Equal(25, biq.Rule[0].FortificationsDefensiveBonus);
		Assert.Equal(0, biq.Rule[0].TownDefenseBonus);
		Assert.Equal(50, biq.Rule[0].CityDefenseBonus);
		Assert.Equal(100, biq.Rule[0].MetropolisDefenseBonus);
		Assert.Equal(3, biq.Rule[0].MovementAlongRoads);

		// The Great Wall is the only building with the flag, and the importer
		// maps it.
		Assert.Equal(
			new[] { "The Great Wall" },
			biq.Bldg.Where(b => b.DoubleCombatVsBarbarians).Select(b => b.Name).ToArray());
		Assert.Contains(SaveBuilding.Flag.DoubleCombatVsBarbarians,
			ImportCiv3.LoadBuildingFlags(biq.Bldg.First(b => b.Name == "The Great Wall")));
		Assert.DoesNotContain(SaveBuilding.Flag.DoubleCombatVsBarbarians,
			ImportCiv3.LoadBuildingFlags(biq.Bldg.First(b => b.Name == "Walls")));

		// The King ability the defender picker prefers, and its carriers:
		// the 31 ruler units, and nothing else.
		Assert.Equal(31, biq.Prto.Count(p => p.King));
		foreach (PRTO prto in biq.Prto.Where(p => p.King)) {
			UnitPrototype prototype = gd.unitPrototypes.First(p => p.name == prto.Name);
			Assert.True(prototype.isKing, $"{prto.Name} should carry the King flag");
		}
		Assert.Equal(31, gd.unitPrototypes.Count(p => p.isKing));
	}

	// ---------- retreat ----------

	[Fact]
	public void SlowUnitTypesNeverRetreat() {
		MapUnit slow = MakeUnit(MakePlayer(), MakePrototype(1, 1, movement: 1), MakeFlatTile());
		MapUnit fast = MakeUnit(MakePlayer(), MakePrototype(1, 1, movement: 2), MakeFlatTile());

		// The binary's gate: maximum move points greater than
		// RULE.MovementAlongRoads (3), which for whole-move rates is
		// "movement rate greater than one".
		Assert.False(slow.CanRetreat());
		Assert.True(fast.CanRetreat());
		Assert.Equal(0.0, slow.RetreatChance(fast));
	}

	[Fact]
	public void TheRetreatChanceIsOwnBonusOverOpponentBonusPlusFifty() {
		MapUnit regular = MakeUnit(MakePlayer(), MakePrototype(1, 1, movement: 2), MakeFlatTile(), experience: "Regular");
		MapUnit veteran = MakeUnit(MakePlayer(), MakePrototype(1, 1, movement: 2), MakeFlatTile(), experience: "Veteran");
		MapUnit fastConscript = MakeUnit(MakePlayer(), MakePrototype(1, 1, movement: 2), MakeFlatTile(), experience: "Conscript");
		MapUnit slowConscript = MakeUnit(MakePlayer(), MakePrototype(1, 1, movement: 1), MakeFlatTile(), experience: "Conscript");
		MapUnit elite = MakeUnit(MakePlayer(), MakePrototype(1, 1, movement: 2), MakeFlatTile(), experience: "Elite");

		Assert.Equal(50 / 100.0, regular.RetreatChance(regular));
		Assert.Equal(58 / 108.0, veteran.RetreatChance(veteran));
		Assert.Equal(66 / 116.0, elite.RetreatChance(elite));
		Assert.Equal(34 / 100.0, fastConscript.RetreatChance(regular));
		// The gate applies before the formula: a slow type never retreats,
		// whatever the experience matchup.
		Assert.Equal(0.0, slowConscript.RetreatChance(regular));
	}

	// ---------- the round loop ----------

	// Every round removes exactly one hit point from the loser, and the
	// round roll is against rand_int(1024) — the odds scale, not a double.
	[Fact]
	public void EveryRoundRemovesExactlyOneHitPoint() {
		Tile attackerTile = CleanMapTile(50, 50);
		Tile defenderTile = attackerTile.neighbors[TileDirection.EAST];
		defenderTile.unitsOnTile.Clear();
		defenderTile.overlays.Clear();
		Player player = MakePlayer();
		MapUnit attacker = MakeUnit(player, MakePrototype(3, 0, movement: 1), attackerTile);
		MapUnit defender = MakeUnit(MakePlayer(), MakePrototype(0, 3, movement: 1), defenderTile);

		ScriptedRandom rng = UseScriptedRandom();
		for (int i = 0; i < defender.maxHitPoints; ++i) {
			rng.intResults.Enqueue(MapUnit.CombatOddsScale - 1); // attacker wins every round
		}

		CombatResult result = attacker.Fight(defender).Result;

		Assert.Equal(CombatResult.DefenderKilled, result);
		Assert.Equal(0, defender.hitPointsRemaining);
		Assert.Equal(attacker.maxHitPoints, attacker.hitPointsRemaining);
		// Exactly maxHitPoints rounds, each rolling against 1024.
		Assert.Equal(defender.maxHitPoints, rng.RollsAgainst(MapUnit.CombatOddsScale));
	}

	// Starting a fight marks the attacker as having used its attack this turn,
	// which is the state the availability test in MapUnit.Move reads
	// (12_combat.md §6.2 step 1, 23_leaders_armies_golden_age.md §6.8).
	[Fact]
	public void StartingAFightSetsTheUsedAttackBit() {
		Tile attackerTile = CleanMapTile(50, 50);
		Tile defenderTile = attackerTile.neighbors[TileDirection.EAST];
		defenderTile.unitsOnTile.Clear();
		defenderTile.overlays.Clear();
		MapUnit attacker = MakeUnit(MakePlayer(), MakePrototype(3, 0, movement: 1), attackerTile);
		MapUnit defender = MakeUnit(MakePlayer(), MakePrototype(0, 3, movement: 1), defenderTile);

		Assert.False(attacker.hasUsedAttack);

		ScriptedRandom rng = UseScriptedRandom();
		for (int i = 0; i < defender.maxHitPoints; ++i) {
			rng.intResults.Enqueue(MapUnit.CombatOddsScale - 1); // attacker wins every round
		}

		_ = attacker.Fight(defender).Result;

		Assert.True(attacker.hasUsedAttack);
	}

	// A losing attacker escapes only when reduced to exactly one hit point —
	// before the change, the retreat was instead tried at one hit point
	// *before* the damage, which let a 1-HP unit survive a lost round.
	[Fact]
	public void TheAttackerRetreatsWhenReducedToOneHitPoint() {
		Tile attackerTile = CleanMapTile(50, 50);
		Tile defenderTile = attackerTile.neighbors[TileDirection.EAST];
		defenderTile.unitsOnTile.Clear();
		defenderTile.overlays.Clear();
		Player player = MakePlayer();
		MapUnit attacker = MakeUnit(player, MakePrototype(3, 0, movement: 2), attackerTile);
		MapUnit defender = MakeUnit(MakePlayer(), MakePrototype(0, 3, movement: 1), defenderTile);

		ScriptedRandom rng = UseScriptedRandom();
		rng.intResults.Enqueue(0);                        // round: defender wins, attacker 2 -> 1
		rng.intResults.Enqueue(0);                        // retreat roll: 0 < 50 + 50, retreat succeeds

		CombatResult result = attacker.Fight(defender).Result;

		Assert.Equal(CombatResult.AttackerRetreated, result);
		Assert.Equal(1, attacker.hitPointsRemaining);
		Assert.Contains(attacker, gd.mapUnits);
		// The retreat roll used the EXPR formula's denominator: the
		// defender's Regular retreat bonus 50 plus the 50 offset.
		Assert.Equal(1, rng.RollsAgainst(50 + MapUnit.RetreatChanceDenominatorOffset));
	}

	// The retreat roll happens exactly once, at one hit point: at two there is
	// none, and at one a failed roll loses the unit.
	[Fact]
	public void ThereIsNoRetreatRollWhileTwoHitPointsRemain() {
		Tile attackerTile = CleanMapTile(50, 50);
		Tile defenderTile = attackerTile.neighbors[TileDirection.EAST];
		defenderTile.unitsOnTile.Clear();
		defenderTile.overlays.Clear();
		MapUnit attacker = MakeUnit(MakePlayer(), MakePrototype(3, 0, movement: 2), attackerTile);
		MapUnit defender = MakeUnit(MakePlayer(), MakePrototype(0, 3, movement: 1), defenderTile);

		ScriptedRandom rng = UseScriptedRandom();
		rng.intResults.Enqueue(0);                        // round 1: attacker 3 -> 2, no retreat roll
		rng.intResults.Enqueue(0);                        // round 2: attacker 2 -> 1, retreat roll...
		rng.intResults.Enqueue(100 - 1);                  // ...which fails (99 is not below 100)
		rng.intResults.Enqueue(0);                        // round 3: attacker 1 -> 0, destroyed

		CombatResult result = attacker.Fight(defender).Result;

		Assert.Equal(CombatResult.AttackerKilled, result);
		// Four rolls in total: three rounds against 1024 and exactly one
		// retreat roll against the formula's 50 + 50, only once the attacker
		// had been reduced to one hit point.
		Assert.Equal(4, rng.requestedBounds.Count);
		Assert.Equal(1, rng.RollsAgainst(50 + MapUnit.RetreatChanceDenominatorOffset));
	}

	[Fact]
	public void ASlowAttackerIsDestroyedWithoutARetreatRoll() {
		Tile attackerTile = CleanMapTile(50, 50);
		Tile defenderTile = attackerTile.neighbors[TileDirection.EAST];
		defenderTile.unitsOnTile.Clear();
		defenderTile.overlays.Clear();
		MapUnit attacker = MakeUnit(MakePlayer(), MakePrototype(3, 0, movement: 1), attackerTile);
		MapUnit defender = MakeUnit(MakePlayer(), MakePrototype(0, 3, movement: 1), defenderTile);

		ScriptedRandom rng = UseScriptedRandom();
		rng.intResults.Enqueue(0);
		rng.intResults.Enqueue(0);
		rng.intResults.Enqueue(0);

		CombatResult result = attacker.Fight(defender).Result;

		Assert.Equal(CombatResult.AttackerKilled, result);
		Assert.Empty(rng.requestedBounds.Where(b => b != MapUnit.CombatOddsScale));
	}

	// When both sides are fast enough to retreat, neither may: the retreat
	// permission is cancelled for both.
	[Fact]
	public void WhenBothSidesMayRetreatThePermissionIsCancelled() {
		Tile attackerTile = CleanMapTile(50, 50);
		Tile defenderTile = attackerTile.neighbors[TileDirection.EAST];
		defenderTile.unitsOnTile.Clear();
		defenderTile.overlays.Clear();
		MapUnit attacker = MakeUnit(MakePlayer(), MakePrototype(3, 0, movement: 2), attackerTile);
		MapUnit defender = MakeUnit(MakePlayer(), MakePrototype(0, 3, movement: 2), defenderTile);

		ScriptedRandom rng = UseScriptedRandom();
		rng.intResults.Enqueue(0);
		rng.intResults.Enqueue(0);
		rng.intResults.Enqueue(0);

		CombatResult result = attacker.Fight(defender).Result;

		Assert.Equal(CombatResult.AttackerKilled, result);
		Assert.Empty(rng.requestedBounds.Where(b => b != MapUnit.CombatOddsScale));
	}

	// Barbarians never retreat, however fast their type is.
	[Fact]
	public void BarbariansNeverRetreat() {
		Tile attackerTile = CleanMapTile(50, 50);
		Tile defenderTile = attackerTile.neighbors[TileDirection.EAST];
		defenderTile.unitsOnTile.Clear();
		defenderTile.overlays.Clear();
		Player barbarianPlayer = MakePlayer(barbarian: true);
		MapUnit attacker = MakeUnit(barbarianPlayer, MakePrototype(3, 0, movement: 2), attackerTile);
		MapUnit defender = MakeUnit(MakePlayer(), MakePrototype(0, 3, movement: 1), defenderTile);

		ScriptedRandom rng = UseScriptedRandom();
		rng.nextDoubleResult = 0.0; // would retreat anything that may retreat
		rng.intResults.Enqueue(0);
		rng.intResults.Enqueue(0);
		rng.intResults.Enqueue(0);

		CombatResult result = attacker.Fight(defender).Result;

		Assert.Equal(CombatResult.AttackerKilled, result);
		Assert.Empty(rng.requestedBounds.Where(b => b != MapUnit.CombatOddsScale));
	}

	// The defender escapes at one hit point when its type is faster than the
	// attacker's and the retreat direction is open.
	[Fact]
	public void TheDefenderRetreatsWhenReducedToOneHitPoint() {
		Tile attackerTile = CleanMapTile(50, 50);
		Tile defenderTile = attackerTile.neighbors[TileDirection.EAST];
		defenderTile.unitsOnTile.Clear();
		defenderTile.overlays.Clear();
		Tile retreatTile = defenderTile.neighbors[TileDirection.EAST];
		retreatTile.unitsOnTile.Clear();
		retreatTile.overlays.Clear();
		MapUnit attacker = MakeUnit(MakePlayer(), MakePrototype(3, 0, movement: 1), attackerTile);
		MapUnit defender = MakeUnit(MakePlayer(), MakePrototype(0, 3, movement: 2), defenderTile);

		ScriptedRandom rng = UseScriptedRandom();
		rng.fallback = MapUnit.CombatOddsScale - 1;         // attacker wins every round
		rng.intResults.Enqueue(MapUnit.CombatOddsScale - 1);
		rng.intResults.Enqueue(MapUnit.CombatOddsScale - 1);
		rng.intResults.Enqueue(0);                          // retreat roll succeeds

		CombatResult result = attacker.Fight(defender).Result;

		Assert.Equal(CombatResult.DefenderRetreated, result);
		Assert.Equal(1, defender.hitPointsRemaining);
		Assert.Equal(retreatTile, defender.location);
		Assert.Equal(1, rng.RollsAgainst(50 + MapUnit.RetreatChanceDenominatorOffset));
	}

	// The barbarian bonus really decides rounds: at Regent the defender's odds
	// against a barbarian attacker are 768 of 1024, so a roll of 600 is a
	// defender win — without the bonus the same roll (odds 512) would favour
	// the attacker.
	[Fact]
	public void TheBarbarianBonusDecidesARoundInPlay() {
		Tile attackerTile = CleanMapTile(50, 50);
		Tile defenderTile = attackerTile.neighbors[TileDirection.EAST];
		defenderTile.unitsOnTile.Clear();
		defenderTile.overlays.Clear();

		ScriptedRandom rng = UseScriptedRandom();

		MapUnit barbarianAttacker = MakeUnit(MakePlayer(barbarian: true), MakePrototype(1, 0), attackerTile);
		barbarianAttacker.hitPointsRemaining = 1;
		MapUnit defender = MakeUnit(MakePlayer(), MakePrototype(0, 1), defenderTile);
		defender.hitPointsRemaining = 1;

		rng.intResults.Enqueue(600);
		CombatResult againstBarbarians = barbarianAttacker.Fight(defender).Result;
		Assert.Equal(CombatResult.AttackerKilled, againstBarbarians);

		// Between two non-barbarians the same roll of 600 wins the round for
		// the attacker.
		MapUnit plainAttacker = MakeUnit(MakePlayer(), MakePrototype(1, 0), attackerTile);
		plainAttacker.hitPointsRemaining = 1;
		MapUnit plainDefender = MakeUnit(MakePlayer(), MakePrototype(0, 1), defenderTile);
		plainDefender.hitPointsRemaining = 1;

		rng.intResults.Enqueue(600);
		CombatResult betweenCivs = plainAttacker.Fight(plainDefender).Result;
		Assert.Equal(CombatResult.DefenderKilled, betweenCivs);
	}

	// The mirrored case: a barbarian defender hands its attacker the same
	// bonus, so the attacker's odds are 256 of 1024 and a roll of 300 is an
	// attacker win (it would be a defender win at the un-bonused 512).
	[Fact]
	public void ABarbarianDefenderHandsTheAttackerTheBonus() {
		Tile attackerTile = CleanMapTile(50, 50);
		Tile defenderTile = attackerTile.neighbors[TileDirection.EAST];
		defenderTile.unitsOnTile.Clear();
		defenderTile.overlays.Clear();

		ScriptedRandom rng = UseScriptedRandom();

		MapUnit attacker = MakeUnit(MakePlayer(), MakePrototype(1, 0), attackerTile);
		attacker.hitPointsRemaining = 1;
		MapUnit barbarianDefender = MakeUnit(MakePlayer(barbarian: true), MakePrototype(0, 1), defenderTile);
		barbarianDefender.hitPointsRemaining = 1;

		rng.intResults.Enqueue(300);
		CombatResult result = attacker.Fight(barbarianDefender).Result;

		Assert.Equal(CombatResult.DefenderKilled, result);
	}

	// ---------- the defender picker ----------

	[Fact]
	public void ThePickerPrefersAKingUnitOverAnEqualDefender() {
		Player defenderPlayer = MakePlayer();
		Tile tile = MakeFlatTile();
		UnitPrototype kingProto = MakePrototype(1, 4);
		kingProto.flags.Add(SaveUnitPrototype.Flag.King);
		MapUnit commoner = MakeUnit(defenderPlayer, MakePrototype(1, 4), tile);
		MapUnit king = MakeUnit(defenderPlayer, kingProto, tile);
		// The commoner is examined first; the King preference must override
		// the equal score.
		MapUnit attacker = MakeUnit(MakePlayer(), MakePrototype(2, 0), MakeFlatTile());

		Assert.Same(king, tile.FindTopDefender(attacker));
	}

	[Fact]
	public void ThePickerScoreScalesStrengthByTheEntrenchmentBonus() {
		Player defenderPlayer = MakePlayer();
		Tile tile = MakeFlatTile();
		MapUnit fortified = MakeUnit(defenderPlayer, MakePrototype(1, 4), tile);
		MapUnit unfortified = MakeUnit(defenderPlayer, MakePrototype(1, 8), tile);
		fortified.Fortify();

		// (100 + 25) * 4 * 3 / 100 = 15 for the fortified unit, against
		// 100 * 8 * 3 / 100 = 24 for the stronger unfortified one, which
		// therefore holds the tile.
		MapUnit attacker = MakeUnit(MakePlayer(), MakePrototype(2, 0), MakeFlatTile());
		Assert.Same(unfortified, tile.FindTopDefender(attacker));

		// The fortify percentage must scale strength: 125% of 4 beats 100% of
		// an equal 4. If the percentage were added to strength instead, both
		// units would score alike and this pick would fall through to the
		// tie-breaks.
		MapUnit weak = MakeUnit(defenderPlayer, MakePrototype(1, 4), MakeFlatTile());
		Assert.Equal(15, fortified.DefenderPickerScore());
		Assert.Equal(12, weak.DefenderPickerScore());
		Assert.Same(fortified, tile.FindTopDefender(attacker, new List<MapUnit> { weak, fortified }));
	}

	[Fact]
	public void EqualScoresKeepTheLessValuableUnitForLater() {
		Player defenderPlayer = MakePlayer();
		Tile tile = MakeFlatTile();
		// The more valuable unit (higher attack) stands in front in the tile's
		// unit list; on equal scores the picker must still return the weaker
		// one, keeping the better unit for later rounds.
		MapUnit valuable = MakeUnit(defenderPlayer, MakePrototype(5, 4), tile);
		MapUnit fodder = MakeUnit(defenderPlayer, MakePrototype(1, 4), tile);

		MapUnit attacker = MakeUnit(MakePlayer(), MakePrototype(2, 0), MakeFlatTile());
		Assert.Same(fodder, tile.FindTopDefender(attacker));
	}

	// ---------- bombardment of a tile ----------

	// The bombardment target's strength is a fixed 16 scaled by the tile's
	// defence percentages, and the defender's odds sit on the same 1/1024
	// scale, clamped to [1, 1023] (12_combat.md §6.3).
	[Fact]
	public void BombardOddsScaleTheSixteenByTheTilesPercentages() {
		Player player = MakePlayer();
		UnitPrototype artillery = MakePrototype(0, 0, movement: 1, bombard: 12, rateOfFire: 2);
		MapUnit bomber = MakeUnit(player, artillery, MakeFlatTile());

		Tile plain = MakeFlatTile();
		Assert.Equal(16, bomber.BombardTargetDefense(plain));
		// 16 * 1024 / (12 + 16) = 585.
		Assert.Equal(585, bomber.DefenderOddsAgainstBombard(plain));

		// A fortress doubles the target's scaled defence: 16 * 150 / 100 = 24,
		// and 24 * 1024 / (12 + 24) = 682.
		TerrainImprovement fortress = gd.terrainImprovements.First(i => i.key == Tile.TileOverlays.FORTRESS);
		plain.overlays.Add(fortress);
		Assert.Equal(24, bomber.BombardTargetDefense(plain));
		Assert.Equal(682, bomber.DefenderOddsAgainstBombard(plain));
	}

	// RateOfFire's role: the bombardment rolls that many independent shots and
	// succeeds as soon as one is at least the defender's odds. The round loop
	// of a fight never rolls RateOfFire times — it always rolls 1024.
	[Fact]
	public void BombardmentRollsOncePerRateOfFireAndSucceedsOnTheFirstHit() {
		Tile tile = CleanMapTile(50, 50);
		TerrainImprovement fortress = gd.terrainImprovements.First(i => i.key == Tile.TileOverlays.FORTRESS);
		tile.overlays.Add(fortress);

		Player player = MakePlayer();
		UnitPrototype artillery = MakePrototype(0, 0, movement: 2, bombard: 12, rateOfFire: 3);
		MapUnit bomber = MakeUnit(player, artillery, CleanMapTile(48, 50));

		ScriptedRandom rng = UseScriptedRandom();
		// Defender's odds are 682; the first two shots miss, the third hits,
		// and no fourth roll happens.
		rng.intResults.Enqueue(681);
		rng.intResults.Enqueue(681);
		rng.intResults.Enqueue(682);

		bomber.Bombard(tile).Wait();

		Assert.Equal(3, rng.RollsAgainst(MapUnit.CombatOddsScale));
		Assert.Empty(tile.overlays.GetManMadeImprovements());
	}

	[Fact]
	public void ABombardmentWhereEveryShotMissesDestroysNothing() {
		Tile tile = CleanMapTile(50, 50);
		TerrainImprovement fortress = gd.terrainImprovements.First(i => i.key == Tile.TileOverlays.FORTRESS);
		tile.overlays.Add(fortress);

		Player player = MakePlayer();
		UnitPrototype artillery = MakePrototype(0, 0, movement: 2, bombard: 12, rateOfFire: 2);
		MapUnit bomber = MakeUnit(player, artillery, CleanMapTile(48, 50));

		ScriptedRandom rng = UseScriptedRandom();
		rng.fallback = 681; // below the defender's odds of 682

		bomber.Bombard(tile).Wait();

		Assert.Equal(2, rng.RollsAgainst(MapUnit.CombatOddsScale));
		Assert.Contains(fortress, tile.overlays.GetManMadeImprovements());
	}
}
