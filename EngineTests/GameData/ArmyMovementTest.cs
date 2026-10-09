using System;
using System.Linq;
using C7Engine;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using Xunit;

namespace EngineTests.GameData;

// Civ3's Unit_get_max_move_points (0x5be470) has a branch for a unit carrying
// the Army ability: the army's maximum movement is the maximum of the maxima of
// the units whose container is the army, plus RULE.MovementAlongRoads internal
// units; the branch is skipped when no member yields a value, so an empty army
// keeps its own type's rate (11_movement.md §2.2, gap G11 in §8.1).
//
// OpenCiv3 used to reset every unit, armies included, to its own prototype's
// movement field, so an army of cavalry moved one point per turn and the Blitz
// ability Civ3 gives armies was unusable. These tests pin the aggregation and
// the empty-army fallback, and pin ordinary units to their unchanged rate.
public sealed class ArmyMovementTest : MapBase {
	private const double Tolerance = 0.0001;

	public ArmyMovementTest() {
		InitializeTestGameData();
	}

	private static ExperienceLevel Regular => new("Regular", "Regular", 3, 0.0, 0.0);

	private static Tile MakeTile() {
		return new(ID.None("tile")) {
			overlayTerrainType = new TerrainType { Key = "plains", movementCost = 1 },
		};
	}

	// An army prototype, whose own movement field is the rate an empty army
	// keeps. The shipped Army type's field is 1.
	private static UnitPrototype MakeArmyType(int movement = 1) {
		UnitPrototype proto = new() {
			name = $"TestArmy{movement}",
			movement = movement,
			capacity = 3,
		};
		proto.flags.Add(SaveUnitPrototype.Flag.Army);
		return proto;
	}

	private static UnitPrototype MakeLandType(int movement) {
		UnitPrototype proto = new() {
			name = $"TestLand{movement}",
			movement = movement,
		};
		proto.categories.Add("Land");
		return proto;
	}

	private static MapUnit MakeUnit(Player owner, UnitPrototype proto, Tile tile) {
		MapUnit unit = proto.GetInstance(ID.None(proto.name), proto, owner, location: tile);
		unit.experienceLevel = Regular;
		unit.experienceLevelKey = "Regular";
		tile.unitsOnTile.Add(unit);
		return unit;
	}

	// A fresh turn for the unit: its movement is spent, then the start-of-turn
	// reset runs.
	private static void BeginNextTurn(MapUnit unit) {
		unit.movementPoints.onConsumeAll();
		unit.OnBeginTurn();
	}

	[Fact]
	public void AnArmyMovesAsFastAsItsFastestMemberPlusOnePoint() {
		Player player = MakePlayer();
		Tile tile = MakeTile();
		MapUnit army = MakeUnit(player, MakeArmyType(), tile);
		MapUnit slow = MakeUnit(player, MakeLandType(1), tile);
		MapUnit fast = MakeUnit(player, MakeLandType(3), tile);

		Assert.True(slow.LoadIntoArmy(army));
		Assert.True(fast.LoadIntoArmy(army));

		BeginNextTurn(army);

		// The fastest member's three points plus the army's one-point bonus.
		Assert.Equal(4.0, army.MaxMovementPoints, Tolerance);
		Assert.Equal(4.0, army.movementPoints.remaining, Tolerance);
	}

	[Fact]
	public void AnArmyOfOneMoveMembersMovesTwoPoints() {
		Player player = MakePlayer();
		Tile tile = MakeTile();
		MapUnit army = MakeUnit(player, MakeArmyType(), tile);
		MapUnit member = MakeUnit(player, MakeLandType(1), tile);

		Assert.True(member.LoadIntoArmy(army));

		BeginNextTurn(army);

		// One point from the member plus the army bonus.
		Assert.Equal(2.0, army.movementPoints.remaining, Tolerance);
	}

	[Fact]
	public void AnEmptyArmyKeepsItsOwnTypesRate() {
		Player player = MakePlayer();

		MapUnit slowArmy = MakeUnit(player, MakeArmyType(movement: 1), MakeTile());
		BeginNextTurn(slowArmy);
		Assert.Equal(1.0, slowArmy.MaxMovementPoints, Tolerance);
		Assert.Equal(1.0, slowArmy.movementPoints.remaining, Tolerance);

		MapUnit fastArmy = MakeUnit(player, MakeArmyType(movement: 2), MakeTile());
		BeginNextTurn(fastArmy);
		Assert.Equal(2.0, fastArmy.MaxMovementPoints, Tolerance);
		Assert.Equal(2.0, fastArmy.movementPoints.remaining, Tolerance);
	}

	[Fact]
	public void UnitsCarriedByAnotherContainerDoNotCountAsMembers() {
		Player player = MakePlayer();
		Tile tile = MakeTile();
		MapUnit army = MakeUnit(player, MakeArmyType(), tile);
		MapUnit member = MakeUnit(player, MakeLandType(1), tile);
		Assert.True(member.LoadIntoArmy(army));

		// A fast unit standing on the same tile but not loaded into the army is
		// not a member, so it must not raise the army's rate.
		MakeUnit(player, MakeLandType(3), tile);

		BeginNextTurn(army);

		Assert.Equal(2.0, army.movementPoints.remaining, Tolerance);
	}

	[Fact]
	public void NonArmyUnitsKeepTheirOwnRate() {
		Player player = MakePlayer();

		MapUnit slow = MakeUnit(player, MakeLandType(1), MakeTile());
		BeginNextTurn(slow);
		Assert.Equal(1.0, slow.MaxMovementPoints, Tolerance);
		Assert.Equal(1.0, slow.movementPoints.remaining, Tolerance);

		MapUnit fast = MakeUnit(player, MakeLandType(3), MakeTile());
		BeginNextTurn(fast);
		Assert.Equal(3.0, fast.MaxMovementPoints, Tolerance);
		Assert.Equal(3.0, fast.movementPoints.remaining, Tolerance);
	}

	// A unit may retreat only when its maximum movement exceeds one movement
	// point, and the binary asks Unit_get_max_move_points for it, so a loaded
	// army qualifies through its members even though the Army type's own field
	// is 1 (12_combat.md §6.1).
	[Fact]
	public void AnArmyRetreatsWhenItsMembersMove() {
		Player player = MakePlayer();
		Tile tile = MakeTile();
		MapUnit army = MakeUnit(player, MakeArmyType(), tile);
		Assert.False(army.CanRetreat());

		MapUnit member = MakeUnit(player, MakeLandType(1), tile);
		Assert.True(member.LoadIntoArmy(army));

		Assert.True(army.CanRetreat());
	}

	// The bonus is one movement point, which is RULE.MovementAlongRoads internal
	// units at any scale; C7 counts movement points, so changing the scale must
	// not change the army's rate in points, while it does change the road step
	// whose internal cost is fixed (11_movement.md §2.1, §2.2).
	[Fact]
	public void TheArmyBonusIsOneMovementPointWhateverTheScale() {
		Player player = MakePlayer();
		Tile tile = MakeTile();
		MapUnit army = MakeUnit(player, MakeArmyType(), tile);
		MapUnit fast = MakeUnit(player, MakeLandType(3), tile);
		Assert.True(fast.LoadIntoArmy(army));

		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		startTile.overlays.Add(road);
		Tile destination = AddNeighborsAndUpdateMap(startTile, MakePlainsTile(), TileDirection.NORTH);
		destination.overlays.Add(road);

		EngineStorage.gameData.rules = new Rules { MovementAlongRoads = 3 };
		BeginNextTurn(army);
		Assert.Equal(4.0, army.movementPoints.remaining, Tolerance);
		Assert.Equal(1.0 / 3.0, TilePath.GetMovementCost(player, startTile, TileDirection.NORTH, destination, army), Tolerance);

		EngineStorage.gameData.rules = new Rules { MovementAlongRoads = 5 };
		BeginNextTurn(army);
		Assert.Equal(4.0, army.movementPoints.remaining, Tolerance);
		Assert.Equal(1.0 / 5.0, TilePath.GetMovementCost(player, startTile, TileDirection.NORTH, destination, army), Tolerance);
	}
}

// The numbers the shipped rules expose, through the real import path: the Army
// type's own movement field is 1, which is what an army used to move with, while
// its fastest shipped members move 3, so a loaded army must move 4
// (11_movement.md §2.2).
public class ArmyMovementFromTheShippedRulesetTest : IClassFixture<SaveGameFixture> {
	private const double Tolerance = 0.0001;
	private readonly C7GameData.GameData gd;

	public ArmyMovementFromTheShippedRulesetTest(SaveGameFixture fixture) {
		gd = fixture.saveGame.ToGameData(fixture.behaviors);
		EngineStorage.InitializeGameDataForTests(gd);
	}

	[Fact]
	public void AShippedArmyOfFastUnitsMovesFourPoints() {
		UnitPrototype armyType = gd.unitPrototypes.First(p => p.isArmy);
		UnitPrototype fastMember = gd.unitPrototypes.First(p => p.name == "Modern Armor");
		Assert.Equal(1, armyType.movement);
		Assert.Equal(3, fastMember.movement);

		Player player = new() {
			id = gd.GenerateID("player"),
			civilization = gd.civilizations.First(c => !c.isBarbarian),
			rules = gd.rules,
			government = new Government(),
		};
		Tile tile = new(gd.GenerateID("tile")) {
			overlayTerrainType = new TerrainType { Key = "plains", movementCost = 1 },
		};
		MapUnit army = armyType.GetInstance(gd.GenerateID("army"), armyType, player, location: tile);
		tile.unitsOnTile.Add(army);

		MapUnit member = fastMember.GetInstance(gd.GenerateID("member"), fastMember, player, location: tile);
		member.experienceLevel = gd.defaultExperienceLevel;
		member.experienceLevelKey = gd.defaultExperienceLevelKey;
		tile.unitsOnTile.Add(member);
		Assert.True(member.LoadIntoArmy(army));

		army.movementPoints.onConsumeAll();
		army.OnBeginTurn();

		Assert.Equal(4.0, army.MaxMovementPoints, Tolerance);
		Assert.Equal(4.0, army.movementPoints.remaining, Tolerance);
	}
}
