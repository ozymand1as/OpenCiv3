using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using C7Engine;
using C7GameData;
using C7GameData.Save;
using Xunit;

namespace EngineTests.GameData;

/// <summary>
/// The nuclear launch, its 3x3 detonation effect, the once-per-detonation
/// incident counter and the diplomacy bookkeeping: 26_nuclear_weapons.md. Each
/// rule from the spec has its own falsifiable test here. The two-data-path
/// requirement (the ability must exist on the BIQ import path and in
/// ruleset.json) is pinned in NuclearWeaponParityTest.
/// </summary>
public class NuclearStrikeTest {
	private const int TestSeed = 42;
	private const int UnderlyingId = TerrainType.UnderlyingTerrainPollutionEffect;

	private static TerrainType MakeTerrain(string key, int pollutionEffect) => new TerrainType {
		Key = key,
		DisplayName = key,
		baseFoodProduction = 1,
		baseShieldProduction = 1,
		baseCommerceProduction = 0,
		movementCost = 1,
		pollutionEffect = pollutionEffect,
	};

	private static C7GameData.GameData MakeGameData() {
		C7GameData.GameData gameData = new(TestSeed);
		gameData.rules = new Rules { MaximumLevel1CitySize = 6, MaximumLevel2CitySize = 12, MaxRankOfWorkableTiles = 2 };
		gameData.terrainTypes = new List<TerrainType> {
			MakeTerrain("desert", -1),
			MakeTerrain("plains", 0),
			MakeTerrain("grassland", 1),
			MakeTerrain("tundra", -1),
			MakeTerrain("hills", -1),
			MakeTerrain("forest", UnderlyingId),
		};
		gameData.terrainImprovements = new List<TerrainImprovement> {
			new("road", TerrainImprovement.Layer.Roads),
			new("railroad", TerrainImprovement.Layer.Roads),
			new("mine", TerrainImprovement.Layer.ResourceDevelopment),
			new("irrigation", TerrainImprovement.Layer.ResourceDevelopment),
			new("fortress", TerrainImprovement.Layer.Holdings),
			new("pollution", TerrainImprovement.Layer.Pollution),
		};
		EngineStorage.InitializeGameDataForTests(gameData);
		C7GameData.GameData.rng = new Random(TestSeed);
		return gameData;
	}

	// A map of the given size, every tile grassland, with neighbours resolved.
	private static (GameMap map, C7GameData.GameData gd) MakeMap(int width, int height) {
		C7GameData.GameData gd = MakeGameData();
		GameMap map = new() { numTilesWide = width, numTilesTall = height };
		TerrainType grass = gd.terrainTypes.First(t => t.Key == "grassland");
		List<Tile> tiles = new();
		for (int y = 0; y < height; ++y) {
			for (int x = y % 2; x < width; x += 2) {
				Tile tile = new(ID.None("tile")) { baseTerrainType = grass, overlayTerrainType = grass };
				tile.XCoordinate = x;
				tile.YCoordinate = y;
				tile.map = map;
				tile.continent = 0;
				tiles.Add(tile);
			}
		}
		map.tiles = tiles;
		map.computeNeighbors();
		gd.map = map;
		return (map, gd);
	}

	private static Player MakePlayer(C7GameData.GameData gd, string name) {
		Player p = new() {
			id = gd.GenerateID("player"),
			isHuman = false,
			civilization = new Civilization(name),
			government = new Government(),
		};
		p.rules = gd.rules;
		gd.players.Add(p);
		return p;
	}

	private static UnitPrototype MakeType(C7GameData.GameData gd, string name, int defense, int range = 6,
			bool nuclear = false, bool icbm = false, bool tactical = false, bool hidden = false, bool army = false,
			SaveUnitPrototype.AIStrategy? strategy = null) {
		HashSet<SaveUnitPrototype.Flag> flags = new();
		if (nuclear) flags.Add(SaveUnitPrototype.Flag.NuclearWeapon);
		if (icbm) flags.Add(SaveUnitPrototype.Flag.ICBM);
		if (tactical) flags.Add(SaveUnitPrototype.Flag.TacticalMissile);
		if (hidden) flags.Add(SaveUnitPrototype.Flag.HiddenNationality);
		if (army) flags.Add(SaveUnitPrototype.Flag.Army);
		HashSet<SaveUnitPrototype.AIStrategy> strategies = new();
		if (strategy != null) strategies.Add(strategy.Value);
		return new UnitPrototype(new SaveUnitPrototype {
			name = name,
			flags = flags,
			aiStrategies = strategies,
			attack = 0,
			defense = defense,
			bombard = 0,
			bombardRange = range,
			movement = 1,
		}, new List<Terraform>());
	}

	private static MapUnit PlaceUnit(C7GameData.GameData gd, UnitPrototype proto, Player owner, Tile tile, int hp = 3) {
		MapUnit u = proto.GetInstance(gd.GenerateID(proto.name), proto, owner, location: tile);
		u.hitPointsRemaining = hp;
		tile.unitsOnTile.Add(u);
		gd.mapUnits.Add(u);
		owner.units.Add(u);
		return u;
	}

	private static Building MakeBuilding(string name, bool small = false, bool great = false,
			bool center = false, bool aqueduct = false, bool hospital = false, bool sdi = false) {
		HashSet<SaveBuilding.Flag> flags = new();
		if (center) flags.Add(SaveBuilding.Flag.IsCenterOfEmpire);
		if (aqueduct) flags.Add(SaveBuilding.Flag.AllowsCitySize2);
		if (hospital) flags.Add(SaveBuilding.Flag.AllowsCitySize3);
		if (sdi) flags.Add(SaveBuilding.Flag.DecreasesMissileSuccess);
		return new Building(new SaveBuilding {
			name = name,
			flags = flags,
			isSmallWonder = small,
			greatWonderProperties = great ? new SaveBuilding.GreatWonderProperties() : null,
		}, EngineStorage.gameData);
	}

	private static City MakeCityOn(C7GameData.GameData gd, Tile tile, Player owner, int population, params Building[] buildings) {
		City city = new(tile, owner, owner.civilization.name + " city", ID.None("city"));
		for (int i = 0; i < population; ++i)
			city.residents.Add(new CityResident());
		foreach (Building b in buildings)
			city.AddBuilding(b);
		tile.cityAtTile = city;
		owner.cities.Add(city);
		gd.cities.Add(city);
		return city;
	}

	private static void FireOnce(C7GameData.GameData gd, UnitPrototype nukeProto, Player launcher, Tile launcherTile, Tile target, Func<int, int> randInt) {
		if (launcher.cities.Count == 0)
			GiveHomeCity(gd, launcher);
		MapUnit nuke = PlaceUnit(gd, nukeProto, launcher, launcherTile);
		nuke.LaunchNuclearWeapon(target, gd, randInt);
	}

	// A launcher that owns a city survives losing its one nuclear unit. A civ
	// with no cities and no units is eliminated when its last unit is spent
	// (CheckForCivDestruction), which would wrongly wipe the very reputation
	// records a launch writes. The home city is placed far from every blast
	// used here. Give the striking civ's enemies a home city too, or a struck
	// unit's owner is eliminated when its last non-spared unit dies, which
	// would take a spared unit with it.
	private static void GiveHomeCity(C7GameData.GameData gd, Player owner, bool farSide = false) {
		GameMap map = gd.map;
		Tile home = map.tileAt(farSide ? map.numTilesWide - 2 : 0, map.numTilesTall - 1);
		if (home == Tile.NONE)
			home = map.tiles.Last();
		MakeCityOn(gd, home, owner, 3);
	}

	// The 3x3 block a launcher sees, centre first, matching the engine walk.
	private static List<Tile> BlockOf(Tile target) {
		List<Tile> block = new() { target };
		block.AddRange(target.neighbors.Values.Where(n => n != Tile.NONE));
		return block;
	}

	// ---- The gate reads the ability, not the AI strategy ----

	[Fact]
	public void NuclearGate_ReadsTheAbilityNotTheAIStrategy() {
		// The trap this project was caught by: ability 16 and the AI-strategy
		// word are DIFFERENT fields of the unit-type record (+0x88 vs +0x8c). A
		// type that carries only the tactical-nuke AI strategy is NOT a nuclear
		// weapon; a type that carries only the ability is.
		C7GameData.GameData gd = MakeGameData();
		UnitPrototype strategyOnly = MakeType(gd, "strategy only", 1,
			strategy: SaveUnitPrototype.AIStrategy.TacticalNuke);
		UnitPrototype abilityOnly = MakeType(gd, "ability only", 1, nuclear: true);

		Assert.False(strategyOnly.isNuclearWeapon);
		Assert.True(abilityOnly.isNuclearWeapon);
	}

	[Fact]
	public async Task Dispatch_SendsANuclearTypeToTheLaunchRoutineNotTheBombard() {
		// The bombard dispatch diverts a type with the ability-16 gate to the
		// launch routine, which detonates (counter++). A type without the gate
		// takes the ordinary bombard path, which never touches the counter.
		EngineStorage.animationsEnabled = false;
		(GameMap map, C7GameData.GameData gd) = MakeMap(20, 3);
		Tile target = map.tileAt(5, 1);

		Player launcher = MakePlayer(gd, "launcher");
		UnitPrototype nuke = MakeType(gd, "Nuke", defense: 0, nuclear: true);
		MapUnit nukeUnit = PlaceUnit(gd, nuke, launcher, map.tileAt(1, 1));
		await nukeUnit.Bombard(target);
		Assert.Equal(1, gd.nukesUsed);

		(GameMap m2, C7GameData.GameData gd2) = MakeMap(20, 3);
		Tile t2 = m2.tileAt(5, 1);
		Player launcher2 = MakePlayer(gd2, "launcher2");
		UnitPrototype catapult = MakeType(gd2, "Catapult", defense: 1);
		MapUnit catapultUnit = PlaceUnit(gd2, catapult, launcher2, m2.tileAt(1, 1));
		await catapultUnit.Bombard(t2);
		Assert.Equal(0, gd2.nukesUsed);
	}

	// ---- The counter, once per detonation (the seam that unblocks warming) ----

	[Fact]
	public void Counter_IncrementsOncePerDetonation() {
		(GameMap map, C7GameData.GameData gd) = MakeMap(20, 3);
		Player launcher = MakePlayer(gd, "launcher");
		UnitPrototype nuke = MakeType(gd, "Nuke", defense: 0, nuclear: true);
		Assert.Equal(0, gd.nukesUsed);

		FireOnce(gd, nuke, launcher, map.tileAt(1, 1), map.tileAt(5, 1), _ => 0);

		Assert.Equal(1, gd.nukesUsed);
	}

	[Fact]
	public void Counter_FeedsTheGlobalWarmingNukesSquaredTermEndToEnd() {
		// The #1 seam: the incident counter at 0xa52698 is what the global-warming
		// pass squares (nukes^2). Nothing used to increment it, so the term was
		// always zero. Three detonations must make it 3 and feed +9 into the
		// accumulated-pollution sum that drives warming.
		(GameMap map, C7GameData.GameData gd) = MakeMap(40, 3);
		Player launcher = MakePlayer(gd, "launcher");
		UnitPrototype nuke = MakeType(gd, "Nuke", defense: 0, nuclear: true);

		Assert.Equal(0, Pollution.AccumulatedPollution(gd));

		FireOnce(gd, nuke, launcher, map.tileAt(1, 1), map.tileAt(10, 1), _ => 0);
		FireOnce(gd, nuke, launcher, map.tileAt(1, 2), map.tileAt(10, 2), _ => 0);
		FireOnce(gd, nuke, launcher, map.tileAt(12, 0), map.tileAt(20, 0), _ => 0);

		Assert.Equal(3, gd.nukesUsed);
		// No city pollution at all, so the whole sum is the nukes-squared term.
		Assert.Equal(9, Pollution.AccumulatedPollution(gd));
		Assert.Equal(1, Pollution.GlobalWarmingSeverity(gd));
	}

	[Fact]
	public void Interception_DoesNotIncrementTheCounter() {
		// The counter counts detonations, not launches: a shot-down nuke adds
		// nothing to it.
		(GameMap map, C7GameData.GameData gd) = MakeMap(20, 3);
		Tile target = map.tileAt(5, 1);
		Player launcher = MakePlayer(gd, "launcher");
		Player victim = MakePlayer(gd, "victim");
		launcher.EnsureRelationshipExists(victim);
		City defenderCity = MakeCityOn(gd, target, victim, 2,
			MakeBuilding("Strategic Missile Defense", small: true, sdi: true));

		GiveHomeCity(gd, launcher);
		UnitPrototype nuke = MakeType(gd, "Nuke", defense: 0, nuclear: true);
		MapUnit nukeUnit = PlaceUnit(gd, nuke, launcher, map.tileAt(1, 1));
		// A non-zero roll is shot down by the defender's missile defence.
		nukeUnit.LaunchNuclearWeapon(target, gd, _ => 1);

		Assert.Equal(0, gd.nukesUsed);
	}

	[Fact]
	public void Interception_RollZeroGetsThroughTheDefence() {
		(GameMap map, C7GameData.GameData gd) = MakeMap(20, 3);
		Tile target = map.tileAt(5, 1);
		Player launcher = MakePlayer(gd, "launcher");
		Player victim = MakePlayer(gd, "victim");
		launcher.EnsureRelationshipExists(victim);
		MakeCityOn(gd, target, victim, 2,
			MakeBuilding("Strategic Missile Defense", small: true, sdi: true));

		GiveHomeCity(gd, launcher);
		UnitPrototype nuke = MakeType(gd, "Nuke", defense: 0, nuclear: true);
		MapUnit nukeUnit = PlaceUnit(gd, nuke, launcher, map.tileAt(1, 1));
		nukeUnit.LaunchNuclearWeapon(target, gd, _ => 0);   // roll 0 -> through

		Assert.Equal(1, gd.nukesUsed);
	}

	// ---- Interception roll rule (pure predicate) ----

	[Theory]
	[InlineData(0, true)]
	[InlineData(1, false)]
	[InlineData(2, false)]
	[InlineData(3, false)]
	public void Interception_LetsTheNukeThroughOnlyOnARollOfZero(int roll, bool expected) {
		Assert.Equal(expected, MapUnit.InterceptionLetsNukeThrough(roll));
	}

	// ---- The diplomacy bookkeeping, through the reputation model ----

	[Fact]
	public void Detonation_IncrementsTheIcmbCounterForABlockCiv() {
		(GameMap map, C7GameData.GameData gd) = MakeMap(20, 3);
		Tile target = map.tileAt(5, 1);
		Player launcher = MakePlayer(gd, "launcher");
		Player victim = MakePlayer(gd, "victim");
		MakeCityOn(gd, target, victim, 3);

		FireOnce(gd, MakeType(gd, "Nuke", 0, nuclear: true), launcher, map.tileAt(1, 1), target, _ => 0);

		// Index 11 (icbm): "I nuked you".
		Assert.Equal(1, DiplomaticAttitude.Reputation(victim, launcher).icbm);
	}

	[Fact]
	public void Detonation_RecordsWarDamageToEveryBlockCiv() {
		(GameMap map, C7GameData.GameData gd) = MakeMap(20, 3);
		Tile target = map.tileAt(5, 1);
		Player launcher = MakePlayer(gd, "launcher");
		Player victim = MakePlayer(gd, "victim");
		MakeCityOn(gd, target, victim, 3);

		FireOnce(gd, MakeType(gd, "Nuke", 0, nuclear: true), launcher, map.tileAt(1, 1), target, _ => 0);

		DiplomaticReputation rep = DiplomaticAttitude.Reputation(victim, launcher);
		Assert.Equal(8, rep.warDamageMemory);
		Assert.Equal(8, rep.warDamageThisTurn);
	}

	[Fact]
	public void Detonation_IncrementsField14AndDeclaresWarOnABlockCiv() {
		(GameMap map, C7GameData.GameData gd) = MakeMap(20, 3);
		Tile target = map.tileAt(5, 1);
		Player launcher = MakePlayer(gd, "launcher");
		Player victim = MakePlayer(gd, "victim");
		launcher.EnsureRelationshipExists(victim);
		Assert.False(PlayerRelationship.AtWar(launcher, victim));
		MakeCityOn(gd, target, victim, 3);

		FireOnce(gd, MakeType(gd, "Nuke", 0, nuclear: true), launcher, map.tileAt(1, 1), target, _ => 0);

		Assert.True(PlayerRelationship.AtWar(launcher, victim));
		Assert.Equal(1, DiplomaticAttitude.Reputation(victim, launcher).field14);
	}

	[Fact]
	public void HiddenNationality_NukesStrikeWithoutDeclaringWarButStillAngerTheVictim() {
		(GameMap map, C7GameData.GameData gd) = MakeMap(20, 3);
		Tile target = map.tileAt(5, 1);
		Player launcher = MakePlayer(gd, "launcher");
		Player victim = MakePlayer(gd, "victim");
		launcher.EnsureRelationshipExists(victim);
		MakeCityOn(gd, target, victim, 3);

		FireOnce(gd, MakeType(gd, "Nuke", 0, nuclear: true, hidden: true), launcher, map.tileAt(1, 1), target, _ => 0);

		// A privateer-style raid is not an act of war, but the field_14 memory
		// of the strike still fires.
		Assert.False(PlayerRelationship.AtWar(launcher, victim));
		Assert.Equal(1, DiplomaticAttitude.Reputation(victim, launcher).field14);
	}

	[Fact]
	public void Detonation_IncrementsIcmbOtherForAThirdCivNotFightingTheBlock() {
		(GameMap map, C7GameData.GameData gd) = MakeMap(20, 3);
		Tile target = map.tileAt(5, 1);
		Player launcher = MakePlayer(gd, "launcher");
		Player victim = MakePlayer(gd, "victim");
		Player bystander = MakePlayer(gd, "bystander");   // not in the block
		bystander.EnsureRelationshipExists(victim);       // at peace with the victim
		MakeCityOn(gd, target, victim, 3);

		FireOnce(gd, MakeType(gd, "Nuke", 0, nuclear: true), launcher, map.tileAt(1, 1), target, _ => 0);

		DiplomaticReputation rep = DiplomaticAttitude.Reputation(bystander, launcher);
		Assert.Equal(0, rep.icbm);
		Assert.Equal(1, rep.icbmOther);   // "you nuked someone I was not fighting"
	}

	[Fact]
	public void Detonation_SkipsIcmbOtherWhenTheBystanderFightsTheBlock() {
		(GameMap map, C7GameData.GameData gd) = MakeMap(20, 3);
		Tile target = map.tileAt(5, 1);
		Player launcher = MakePlayer(gd, "launcher");
		Player victim = MakePlayer(gd, "victim");
		Player bystander = MakePlayer(gd, "bystander");
		bystander.EnsureRelationshipExists(victim);
		bystander.DeclareWarOn(victim, gd.turn);   // already fighting the victim
		Assert.True(PlayerRelationship.AtWar(bystander, victim));
		MakeCityOn(gd, target, victim, 3);

		FireOnce(gd, MakeType(gd, "Nuke", 0, nuclear: true), launcher, map.tileAt(1, 1), target, _ => 0);

		Assert.Equal(0, DiplomaticAttitude.Reputation(bystander, launcher).icbmOther);
	}

	[Fact]
	public void Interception_StillAdvancesTheIcmbCounter() {
		// Section 8.1's resolved rule: the icbm counters live in the launch
		// routine's second player loop, which has no break, so an SDI
		// interception does NOT stop them even though the detonation (and the
		// 0xa52698 counter) never runs.
		(GameMap map, C7GameData.GameData gd) = MakeMap(20, 3);
		Tile target = map.tileAt(5, 1);
		Player launcher = MakePlayer(gd, "launcher");
		Player victim = MakePlayer(gd, "victim");
		launcher.EnsureRelationshipExists(victim);
		MakeCityOn(gd, target, victim, 2,
			MakeBuilding("Strategic Missile Defense", small: true, sdi: true));

		GiveHomeCity(gd, launcher);
		MapUnit nuke = PlaceUnit(gd, MakeType(gd, "Nuke", 0, nuclear: true), launcher, map.tileAt(1, 1));
		nuke.LaunchNuclearWeapon(target, gd, _ => 1);   // non-zero roll -> intercepted

		Assert.Equal(0, gd.nukesUsed);                         // no detonation
		Assert.Equal(1, DiplomaticAttitude.Reputation(victim, launcher).icbm);   // but icbm advanced
	}

	// ---- Target city buildings (section 5.3) ----

	[Fact]
	public void Strike_SparesProtectedBuildingsEvenOnALosingRoll() {
		// Palace, Aqueduct, Hospital, a small wonder and a great wonder are safe
		// even when the roll (0) would lose every eligible building.
		(GameMap map, C7GameData.GameData gd) = MakeMap(20, 3);
		Tile target = map.tileAt(5, 1);
		Player launcher = MakePlayer(gd, "launcher");
		Player victim = MakePlayer(gd, "victim");
		City city = MakeCityOn(gd, target, victim, 3,
			MakeBuilding("Palace", center: true),
			MakeBuilding("Aqueduct", aqueduct: true),
			MakeBuilding("Hospital", hospital: true),
			MakeBuilding("Women's Suffrage", small: true),
			MakeBuilding("Great Library", great: true));

		FireOnce(gd, MakeType(gd, "Nuke", 0, nuclear: true), launcher, map.tileAt(1, 1), target, _ => 0);

		Assert.Equal(5, city.GetBuildings().Count);   // nothing lost
	}

	[Fact]
	public void Strike_LosesEachUnprotectedBuildingWithHalfChance() {
		// rand_int(2) per unprotected building: a 0 loses it, a 1 keeps it. Three
		// plain temples with rolls (after the interception draw) 0, 1, 0 leave
		// exactly one.
		(GameMap map, C7GameData.GameData gd) = MakeMap(20, 3);
		Tile target = map.tileAt(5, 1);
		Player launcher = MakePlayer(gd, "launcher");
		Player victim = MakePlayer(gd, "victim");
		City city = MakeCityOn(gd, target, victim, 3,
			MakeBuilding("Temple A"),
			MakeBuilding("Temple B"),
			MakeBuilding("Temple C"));
		Assert.Equal(3, city.GetBuildings().Count);

		int[] rolls = { 0, 0, 1, 0 };   // interception, temple A, temple B, temple C
		int i = 0;
		FireOnce(gd, MakeType(gd, "Nuke", 0, nuclear: true), launcher, map.tileAt(1, 1), target,
			_ => rolls[Math.Min(i++, rolls.Length - 1)]);

		Assert.Single(city.GetBuildings());
	}

	[Theory]
	[InlineData(0, true)]
	[InlineData(1, false)]
	public void RollDestroysBuilding_OnARollOfZero(int roll, bool lost) {
		Assert.Equal(lost, MapUnit.RollDestroysBuilding(roll));
	}

	// ---- Population (section 5.4) ----

	[Fact]
	public void Strike_CentreLosesHalfPopulationAndNeighboursLoseOne() {
		(GameMap map, C7GameData.GameData gd) = MakeMap(20, 3);
		Tile target = map.tileAt(5, 1);
		Player launcher = MakePlayer(gd, "launcher");
		Player victim = MakePlayer(gd, "victim");
		City centre = MakeCityOn(gd, target, victim, 9);
		Tile neighbour = BlockOf(target)[1];
		City edge = MakeCityOn(gd, neighbour, victim, 4);

		FireOnce(gd, MakeType(gd, "Nuke", 0, nuclear: true), launcher, map.tileAt(1, 1), target, _ => 0);

		Assert.Equal(5, centre.residents.Count);   // 9 - floor(9/2) = 5
		Assert.Equal(3, edge.residents.Count);     // 4 - 1 = 3
	}

	[Fact]
	public void Strike_NeverReducesASizeOneCity() {
		(GameMap map, C7GameData.GameData gd) = MakeMap(20, 3);
		Tile target = map.tileAt(5, 1);
		Player launcher = MakePlayer(gd, "launcher");
		Player victim = MakePlayer(gd, "victim");
		City centre = MakeCityOn(gd, target, victim, 1);
		Tile neighbour = BlockOf(target)[1];
		City edge = MakeCityOn(gd, neighbour, victim, 1);

		FireOnce(gd, MakeType(gd, "Nuke", 0, nuclear: true), launcher, map.tileAt(1, 1), target, _ => 0);

		Assert.Single(centre.residents);
		Assert.Single(edge.residents);
	}

	// ---- Units (section 5.7) ----

	[Fact]
	public void Strike_SparesNuclearWeapons() {
		(GameMap map, C7GameData.GameData gd) = MakeMap(20, 3);
		Tile target = map.tileAt(5, 1);
		Player launcher = MakePlayer(gd, "launcher");
		Player enemy = MakePlayer(gd, "enemy");
		GiveHomeCity(gd, enemy, farSide: true);

		MapUnit otherNuke = PlaceUnit(gd, MakeType(gd, "Other Nuke", 1, nuclear: true), enemy, target);
		MapUnit strong = PlaceUnit(gd, MakeType(gd, "Warrior", 1), enemy, target, hp: 8);

		// A warrior (defense 1, hp 8) is rolled; a losing roll destroys it. The
		// other nuclear weapon survives by rule, not by the roll.
		FireOnce(gd, MakeType(gd, "Nuke", 0, nuclear: true), launcher, map.tileAt(1, 1), target, _ => 1);

		Assert.True(gd.mapUnits.Contains(otherNuke), "a nuclear weapon is spared");
		Assert.False(gd.mapUnits.Contains(strong), "a plain military unit is rolled and lost");
	}

	[Fact]
	public void Strike_SparesArmyCargo() {
		(GameMap map, C7GameData.GameData gd) = MakeMap(20, 3);
		Tile target = map.tileAt(5, 1);
		Tile farCarrierTile = map.tileAt(15, 1);   // clear of the 3x3 blast on the target
		Player launcher = MakePlayer(gd, "launcher");
		Player enemy = MakePlayer(gd, "army owner");
		GiveHomeCity(gd, enemy, farSide: true);

		// The army carrier sits on a tile clear of the blast, so the strike never
		// touches it and its death cannot cascade onto the cargo. The cargo is on
		// the blasted tile and records the carrier as its transport.
		UnitPrototype armyProto = MakeType(gd, "Army", 1, army: true);
		MapUnit carrier = PlaceUnit(gd, armyProto, enemy, farCarrierTile);
		MapUnit cargo = PlaceUnit(gd, MakeType(gd, "Swordsman", 1), enemy, target, hp: 8);
		cargo.loadedOnUnitId = carrier.id;
		MapUnit strong = PlaceUnit(gd, MakeType(gd, "Warrior", 1), enemy, target, hp: 8);

		// A losing roll destroys the plain warrior; the cargo is spared by the
		// army rule before any roll is drawn for it.
		FireOnce(gd, MakeType(gd, "Nuke", 0, nuclear: true), launcher, map.tileAt(1, 1), target, _ => 1);

		Assert.True(gd.mapUnits.Contains(cargo), "army cargo is spared");
		Assert.False(gd.mapUnits.Contains(strong), "a plain unit beside it is destroyed");
	}

	[Fact]
	public void Strike_DestroysDefenceZeroUnitsOutright() {
		(GameMap map, C7GameData.GameData gd) = MakeMap(20, 3);
		Tile target = map.tileAt(5, 1);
		Player launcher = MakePlayer(gd, "launcher");
		Player enemy = MakePlayer(gd, "enemy");
		MapUnit worker = PlaceUnit(gd, MakeType(gd, "Worker", 0), enemy, target, hp: 3);

		// Even a surviving roll cannot save a defence-0 unit: no roll is drawn
		// for it, so a forced-0 roll still sees the worker destroyed.
		FireOnce(gd, MakeType(gd, "Nuke", 0, nuclear: true), launcher, map.tileAt(1, 1), target, _ => 0);

		Assert.DoesNotContain(worker, gd.mapUnits);
	}

	[Fact]
	public void Strike_DestroysAUnitAtOneHitPoint() {
		(GameMap map, C7GameData.GameData gd) = MakeMap(20, 3);
		Tile target = map.tileAt(5, 1);
		Player launcher = MakePlayer(gd, "launcher");
		Player enemy = MakePlayer(gd, "enemy");
		MapUnit wounded = PlaceUnit(gd, MakeType(gd, "Warrior", 1), enemy, target, hp: 1);

		FireOnce(gd, MakeType(gd, "Nuke", 0, nuclear: true), launcher, map.tileAt(1, 1), target, _ => 0);

		Assert.DoesNotContain(wounded, gd.mapUnits);
	}

	[Fact]
	public void Strike_LeavesASurvivingUnitAtHalfItsHitPoints() {
		(GameMap map, C7GameData.GameData gd) = MakeMap(20, 3);
		Tile target = map.tileAt(5, 1);
		Player launcher = MakePlayer(gd, "launcher");
		Player enemy = MakePlayer(gd, "enemy");
		MapUnit strong = PlaceUnit(gd, MakeType(gd, "Warrior", 1), enemy, target, hp: 8);

		// interception(0) then the unit's survival roll(0) -> survives at half.
		int[] rolls = { 0, 0 };
		int i = 0;
		FireOnce(gd, MakeType(gd, "Nuke", 0, nuclear: true), launcher, map.tileAt(1, 1), target,
			_ => rolls[Math.Min(i++, rolls.Length - 1)]);

		Assert.Contains(strong, gd.mapUnits);
		Assert.Equal(4, strong.hitPointsRemaining);
	}

	[Fact]
	public void Strike_DestroysADamagedUnitOnANonZeroRoll() {
		(GameMap map, C7GameData.GameData gd) = MakeMap(20, 3);
		Tile target = map.tileAt(5, 1);
		Player launcher = MakePlayer(gd, "launcher");
		Player enemy = MakePlayer(gd, "enemy");
		MapUnit strong = PlaceUnit(gd, MakeType(gd, "Warrior", 1), enemy, target, hp: 8);

		int[] rolls = { 0, 1 };
		int i = 0;
		FireOnce(gd, MakeType(gd, "Nuke", 0, nuclear: true), launcher, map.tileAt(1, 1), target,
			_ => rolls[Math.Min(i++, rolls.Length - 1)]);

		Assert.DoesNotContain(strong, gd.mapUnits);
	}

	[Theory]
	[InlineData(0, true)]
	[InlineData(1, false)]
	[InlineData(2, false)]
	public void RollSparesDamagedUnit_OnlyOnARollOfZero(int roll, bool spared) {
		Assert.Equal(spared, MapUnit.RollSparesDamagedUnit(roll));
	}

	// ---- Improvements and pollution (section 5.6) ----

	[Fact]
	public void Strike_ClearsRoadOnlyWhereNoCityIsPresent() {
		(GameMap map, C7GameData.GameData gd) = MakeMap(20, 3);
		Tile target = map.tileAt(5, 1);
		Player launcher = MakePlayer(gd, "launcher");
		Player victim = MakePlayer(gd, "victim");

		// A road on the target city tile survives; a road on an empty block
		// tile is stripped.
		MakeCityOn(gd, target, victim, 2);
		TerrainImprovement road = gd.terrainImprovements.First(t => t.key == "road");
		target.overlays.Add(road);
		Assert.True(target.HasRoad());

		Tile neighbour = BlockOf(target)[1];
		neighbour.overlays.Add(road);
		Assert.True(neighbour.HasRoad());

		FireOnce(gd, MakeType(gd, "Nuke", 0, nuclear: true), launcher, map.tileAt(1, 1), target, _ => 0);

		Assert.True(target.HasRoad(), "a city tile keeps its road");
		Assert.False(neighbour.HasRoad(), "a city-less tile loses its road");
	}

	[Fact]
	public void Strike_AlwaysClearsMineFortressAndGoodyHut() {
		(GameMap map, C7GameData.GameData gd) = MakeMap(20, 3);
		Tile target = map.tileAt(5, 1);
		Player launcher = MakePlayer(gd, "launcher");

		Tile mineTile = BlockOf(target)[1];
		Tile fortTile = BlockOf(target)[2];
		Tile goodTile = BlockOf(target)[3];
		mineTile.overlays.Add(gd.terrainImprovements.First(t => t.key == "mine"));
		fortTile.overlays.Add(gd.terrainImprovements.First(t => t.key == "fortress"));
		goodTile.hasGoodyHut = true;

		FireOnce(gd, MakeType(gd, "Nuke", 0, nuclear: true), launcher, map.tileAt(1, 1), target, _ => 0);

		Assert.Null(mineTile.overlays.ImprovementAtLayer(TerrainImprovement.Layer.ResourceDevelopment));
		Assert.False(fortTile.HasFortress());
		Assert.False(goodTile.hasGoodyHut);
	}

	[Fact]
	public void Strike_LeavesOnePollutionMarkerOnEveryNonCityLandTile() {
		(GameMap map, C7GameData.GameData gd) = MakeMap(20, 3);
		Tile target = map.tileAt(5, 1);
		Player launcher = MakePlayer(gd, "launcher");
		Player victim = MakePlayer(gd, "victim");
		MakeCityOn(gd, target, victim, 2);   // target has a city

		Tile landNeighbour = BlockOf(target)[1];
		Assert.False(landNeighbour.HasCity());
		Assert.True(landNeighbour.IsLand());

		FireOnce(gd, MakeType(gd, "Nuke", 0, nuclear: true), launcher, map.tileAt(1, 1), target, _ => 0);

		Assert.False(target.HasPollution(), "a city tile is not polluted by the strike");
		Assert.True(landNeighbour.HasPollution(), "a city-less land tile is polluted");
	}

	// ---- Terrain (section 5.8) ----

	[Fact]
	public void Strike_TurnsGrasslandIntoPlains() {
		(GameMap map, C7GameData.GameData gd) = MakeMap(20, 3);
		Tile target = map.tileAt(5, 1);
		Player launcher = MakePlayer(gd, "launcher");
		// Target is grassland with pollutionEffect 1 (plains), and plains is
		// below 4, so it converts.
		Assert.Equal("grassland", target.overlayTerrainType.Key);

		FireOnce(gd, MakeType(gd, "Nuke", 0, nuclear: true), launcher, map.tileAt(1, 1), target, _ => 0);

		Assert.Equal("plains", target.overlayTerrainType.Key);
		Assert.Equal("grassland", target.baseTerrainType.Key);
	}

	[Fact]
	public void Strike_LeavesImmuneTerrainAlone() {
		(GameMap map, C7GameData.GameData gd) = MakeMap(20, 3);
		Player launcher = MakePlayer(gd, "launcher");

		// Desert and hills both carry pollutionEffect -1 and are never changed.
		Tile target = map.tileAt(5, 1);
		TerrainType desert = gd.terrainTypes.First(t => t.Key == "desert");
		target.overlayTerrainType = desert;
		target.baseTerrainType = desert;

		FireOnce(gd, MakeType(gd, "Nuke", 0, nuclear: true), launcher, map.tileAt(1, 1), target, _ => 0);

		Assert.Equal("desert", target.overlayTerrainType.Key);
	}

	[Fact]
	public void Strike_StripsForestToItsUnderlyingTerrainWhenThatIdIsBelowFour() {
		(GameMap map, C7GameData.GameData gd) = MakeMap(20, 3);
		Player launcher = MakePlayer(gd, "launcher");
		TerrainType grass = gd.terrainTypes.First(t => t.Key == "grassland");
		TerrainType hills = gd.terrainTypes.First(t => t.Key == "hills");
		TerrainType forest = gd.terrainTypes.First(t => t.Key == "forest");

		// Forest on grassland (underlying id 2, below 4) strips to grassland.
		Tile target = map.tileAt(5, 1);
		target.overlayTerrainType = forest;
		target.baseTerrainType = grass;
		// Forest on hills (underlying id 5, 4 or more) is left alone.
		Tile hillsForest = BlockOf(target)[1];
		hillsForest.overlayTerrainType = forest;
		hillsForest.baseTerrainType = hills;

		FireOnce(gd, MakeType(gd, "Nuke", 0, nuclear: true), launcher, map.tileAt(1, 1), target, _ => 0);

		Assert.Equal("grassland", target.overlayTerrainType.Key);
		Assert.Equal("forest", hillsForest.overlayTerrainType.Key);
	}

	// ---- The human range gate (section 4.3) ----

	[Fact]
	public void HumanRangeGate_RefusesATargetBeyondBombardRange() {
		// The range lives at target selection, not in the launch routine. The
		// metric is floor((xDist + yDist)/2); range 6 accepts a sum of 13 and
		// refuses 14.
		(GameMap map, C7GameData.GameData gd) = MakeMap(20, 3);
		Player launcher = MakePlayer(gd, "launcher");
		MapUnit tactical = PlaceUnit(gd, MakeType(gd, "Tactical Nuke", 0, nuclear: true, tactical: true, range: 6), launcher, map.tileAt(1, 1));

		Tile near = map.tileAt(13, 1);   // dx = 12 -> distance 6
		Tile far = map.tileAt(15, 1);    // dx = 14 -> distance 7
		Assert.Equal(6, tactical.location.DistanceTo(near));
		Assert.Equal(7, tactical.location.DistanceTo(far));

		Assert.True(tactical.CanLaunchNuclearWeaponAt(near));
		Assert.False(tactical.CanLaunchNuclearWeaponAt(far));
	}

	[Fact]
	public void HumanRangeGate_AnICBMIsUnlimited() {
		(GameMap map, C7GameData.GameData gd) = MakeMap(20, 3);
		Player launcher = MakePlayer(gd, "launcher");
		// The shipped ICBM carries BombardRange 0, but the ability-20 branch
		// exempts it from the distance test entirely.
		MapUnit icbm = PlaceUnit(gd, MakeType(gd, "ICBM", 0, nuclear: true, icbm: true, range: 0), launcher, map.tileAt(1, 1));
		Tile far = map.tileAt(15, 1);

		Assert.Equal(7, icbm.location.DistanceTo(far));
		Assert.True(icbm.CanLaunchNuclearWeaponAt(far));
	}

	[Fact]
	public void HumanRangeGate_CityAndOwnUnitTargetsAreRefused() {
		(GameMap map, C7GameData.GameData gd) = MakeMap(20, 3);
		Player launcher = MakePlayer(gd, "launcher");
		Player victim = MakePlayer(gd, "victim");
		launcher.EnsureRelationshipExists(victim);
		Tile target = map.tileAt(5, 1);

		// A peaceful victim's city is refused as a target (section 3.3), even
		// though it is well within range.
		MakeCityOn(gd, target, victim, 2);
		MapUnit nuke = PlaceUnit(gd, MakeType(gd, "Nuke", 0, nuclear: true, range: 6), launcher, map.tileAt(1, 1));

		Assert.False(nuke.CanBombardTile(target, out _));
	}
}
