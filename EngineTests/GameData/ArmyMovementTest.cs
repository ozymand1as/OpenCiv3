using System;
using System.Linq;
using C7Engine;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using Xunit;

namespace EngineTests.GameData;

// Civ3's Unit_get_max_move_points (0x5be470) has a branch for a unit carrying
// the Army ability: the army's maximum movement is the SMALLEST maximum among
// the units whose container is the army, plus RULE.MovementAlongRoads internal
// units, i.e. an army moves at its slowest member's rate plus one point. The
// comparison at 0x5be528 keeps the running value whenever it is the smaller of
// the two, so one slow member slows the whole army down
// (11_movement.md section 2.2, gap G11 in section 8.1).
//
// The running value starts at -1 and the "+ MovementAlongRoads" add happens
// before the empty test (0x5be569-0x5be573), so an army with no members returns
// MovementAlongRoads - 1 internal units - two thirds of a point at the shipped
// scale - and does NOT fall back to its own type's rate.
//
// A homogeneous army cannot tell the minimum from the maximum, so every test
// below that pins the aggregation uses at least two different member speeds.
// Against the previous "fastest member plus one" expression the mixed-speed
// tests read 4.0 (or 3.0) where they now read 2.0, and they fail.
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

	// An army prototype, whose own movement field is the rate a pre-fix empty
	// army wrongly kept. The shipped Army type's field is 1.
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

	// The mixed-speed case the old expression got wrong: one 1-move member and
	// one 3-move member. The slowest member wins, so the maximum is 2, not the
	// 4 the old Max expression produced.
	[Fact]
	public void AnArmyMovesAsSlowAsItsSlowestMemberPlusOnePoint() {
		Player player = MakePlayer();
		Tile tile = MakeTile();
		MapUnit army = MakeUnit(player, MakeArmyType(), tile);
		MapUnit slow = MakeUnit(player, MakeLandType(1), tile);
		MapUnit fast = MakeUnit(player, MakeLandType(3), tile);

		Assert.True(slow.LoadIntoArmy(army));
		Assert.True(fast.LoadIntoArmy(army));

		Assert.Equal(2.0, army.MaxMovementPoints, Tolerance);

		BeginNextTurn(army);

		// The slowest member's one point plus the army's one-point bonus.
		Assert.Equal(2.0, army.movementPoints.remaining, Tolerance);
	}

	// One slow member is enough to slow a whole army of fast ones, whichever
	// order the members are loaded in.
	[Fact]
	public void OneSlowMemberSlowsAnArmyOfFastMembers() {
		Player player = MakePlayer();
		Tile tile = MakeTile();
		MapUnit army = MakeUnit(player, MakeArmyType(), tile);
		Assert.True(MakeUnit(player, MakeLandType(3), tile).LoadIntoArmy(army));
		Assert.True(MakeUnit(player, MakeLandType(3), tile).LoadIntoArmy(army));
		Assert.True(MakeUnit(player, MakeLandType(1), tile).LoadIntoArmy(army));

		BeginNextTurn(army);

		Assert.Equal(2.0, army.MaxMovementPoints, Tolerance);
		Assert.Equal(2.0, army.movementPoints.remaining, Tolerance);
	}

	// A third pair of speeds, so the aggregation cannot pass by coincidence: the
	// minimum of 2 and 3 is 2, and 2 + 1 = 3, where the old maximum read 4.
	[Fact]
	public void TheSlowestOfTwoMiddleSpeedsWins() {
		Player player = MakePlayer();
		Tile tile = MakeTile();
		MapUnit army = MakeUnit(player, MakeArmyType(), tile);
		Assert.True(MakeUnit(player, MakeLandType(2), tile).LoadIntoArmy(army));
		Assert.True(MakeUnit(player, MakeLandType(3), tile).LoadIntoArmy(army));

		BeginNextTurn(army);

		Assert.Equal(3.0, army.MaxMovementPoints, Tolerance);
		Assert.Equal(3.0, army.movementPoints.remaining, Tolerance);
	}

	// The no-member consequence: the -1 running value is added to
	// MovementAlongRoads before it is tested, so an empty army returns one
	// internal unit less than the road bonus. At the shipped scale of three that
	// is 2/3 of a movement point - two road tiles - whatever the Army type's own
	// movement field says. The old fallback returned the type's rate.
	[Fact]
	public void AnEmptyArmyReturnsOneInternalUnitLessThanTheRoadBonus() {
		Player player = MakePlayer();

		MapUnit slowArmy = MakeUnit(player, MakeArmyType(movement: 1), MakeTile());
		BeginNextTurn(slowArmy);
		Assert.Equal(2.0 / 3.0, slowArmy.MaxMovementPoints, Tolerance);
		Assert.Equal(2.0 / 3.0, slowArmy.movementPoints.remaining, Tolerance);

		// The type's own field does not matter: even a two-move Army type gets
		// the shortfall, not two points.
		MapUnit fastArmy = MakeUnit(player, MakeArmyType(movement: 2), MakeTile());
		BeginNextTurn(fastArmy);
		Assert.Equal(2.0 / 3.0, fastArmy.MaxMovementPoints, Tolerance);
		Assert.Equal(2.0 / 3.0, fastArmy.movementPoints.remaining, Tolerance);
	}

	// Only units whose container is the army count. The test uses a slower
	// bystander, because under the minimum a faster bystander could not change
	// the result and the assertion would not falsify an "all units on the tile"
	// implementation.
	[Fact]
	public void UnitsCarriedByAnotherContainerDoNotCountAsMembers() {
		Player player = MakePlayer();
		Tile tile = MakeTile();
		MapUnit army = MakeUnit(player, MakeArmyType(), tile);
		Assert.True(MakeUnit(player, MakeLandType(3), tile).LoadIntoArmy(army));

		// A one-move unit standing on the same tile but not loaded into the army
		// is not a member, so it must not slow the army down to 2.
		MakeUnit(player, MakeLandType(1), tile);

		BeginNextTurn(army);

		Assert.Equal(4.0, army.movementPoints.remaining, Tolerance);
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
	// is 1. The empty army does not: its shortfall is below one point
	// (12_combat.md section 6.1, 11_movement.md section 2.2).
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
	// whose internal cost is fixed (11_movement.md section 2.1, 2.2). The
	// empty-army shortfall of one internal unit is scale-dependent by
	// construction: 2/3 of a point at scale 3, 4/5 at scale 5.
	[Fact]
	public void TheArmyBonusIsOnePointAndTheEmptyShortfallIsOneInternalUnitWhateverTheScale() {
		Player player = MakePlayer();
		Tile tile = MakeTile();
		MapUnit army = MakeUnit(player, MakeArmyType(), tile);
		Assert.True(MakeUnit(player, MakeLandType(1), tile).LoadIntoArmy(army));
		Assert.True(MakeUnit(player, MakeLandType(3), tile).LoadIntoArmy(army));

		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		startTile.overlays.Add(road);
		Tile destination = AddNeighborsAndUpdateMap(startTile, MakePlainsTile(), TileDirection.NORTH);
		destination.overlays.Add(road);

		EngineStorage.gameData.rules = new Rules { MovementAlongRoads = 3 };
		BeginNextTurn(army);
		Assert.Equal(2.0, army.movementPoints.remaining, Tolerance);
		Assert.Equal(1.0 / 3.0, TilePath.GetMovementCost(player, startTile, TileDirection.NORTH, destination, army), Tolerance);
		Assert.Equal(2.0 / 3.0, MakeUnit(player, MakeArmyType(), MakeTile()).MaxMovementPoints, Tolerance);

		EngineStorage.gameData.rules = new Rules { MovementAlongRoads = 5 };
		BeginNextTurn(army);
		Assert.Equal(2.0, army.movementPoints.remaining, Tolerance);
		Assert.Equal(1.0 / 5.0, TilePath.GetMovementCost(player, startTile, TileDirection.NORTH, destination, army), Tolerance);
		Assert.Equal(4.0 / 5.0, MakeUnit(player, MakeArmyType(), MakeTile()).MaxMovementPoints, Tolerance);
	}

	// Loading is where the corrected maximum reaches the movement points that
	// gate a second attack. Civ3's Unit_load_into_army (0x5bcc90) folds the
	// member's SPENT movement into the army's spent movement, so a load never
	// hands the army more movement than the aggregation allows: a slow member
	// slows the army at once, and a fast one cannot raise it (11_movement.md
	// section 2.2). The old "army moves as fast as its fastest member" load gave
	// the army the fast member's rate instead.
	[Fact]
	public void LoadingASlowMemberLowersTheArmyToTheAggregatedRate() {
		Player player = MakePlayer();
		Tile tile = MakeTile();
		MapUnit army = MakeUnit(player, MakeArmyType(), tile);

		// A fresh member has spent nothing, so the army gets its aggregated
		// maximum at once: min(3) + 1 = 4.
		Assert.True(MakeUnit(player, MakeLandType(3), tile).LoadIntoArmy(army));
		Assert.Equal(4.0, army.movementPoints.remaining, Tolerance);

		// Loading the slow member drops the maximum to min(3, 1) + 1 = 2, and the
		// army has spent nothing yet, so it has two points left.
		Assert.True(MakeUnit(player, MakeLandType(1), tile).LoadIntoArmy(army));
		Assert.Equal(2.0, army.MaxMovementPoints, Tolerance);
		Assert.Equal(2.0, army.movementPoints.remaining, Tolerance);

		// One point buys one attack; the second point is what lets the army use
		// the Blitz rule, and the third attack is refused. An army with a slow
		// member is slow.
		army.movementPoints.onUnitMove(1f);
		Assert.True(army.movementPoints.canMove);
		army.movementPoints.onUnitMove(1f);
		Assert.False(army.movementPoints.canMove);

		// A further fast member cannot raise the rate back up, because the army
		// has already spent its two points.
		Assert.True(MakeUnit(player, MakeLandType(3), tile).LoadIntoArmy(army));
		Assert.Equal(2.0, army.MaxMovementPoints, Tolerance);
		Assert.Equal(0.0, army.movementPoints.remaining, Tolerance);
	}
}

// The numbers the shipped rules expose, through the real import path: the Army
// type's own movement field is 1, which is what an army used to move with, and
// its members range from 1 (Warrior) to 3 (Modern Armor), so a mixed army must
// move at min(3, 1) + 1 = 2 - not the 4 the old "fastest member" rule read.
public class ArmyMovementFromTheShippedRulesetTest : IClassFixture<SaveGameFixture> {
	private const double Tolerance = 0.0001;
	private readonly C7GameData.GameData gd;

	public ArmyMovementFromTheShippedRulesetTest(SaveGameFixture fixture) {
		gd = fixture.saveGame.ToGameData(fixture.behaviors);
		EngineStorage.InitializeGameDataForTests(gd);
	}

	private (Player player, UnitPrototype armyType, Tile tile) MakeSetup() {
		UnitPrototype armyType = gd.unitPrototypes.First(p => p.isArmy);
		Player player = new() {
			id = gd.GenerateID("player"),
			civilization = gd.civilizations.First(c => !c.isBarbarian),
			rules = gd.rules,
			government = new Government(),
		};
		Tile tile = new(gd.GenerateID("tile")) {
			overlayTerrainType = new TerrainType { Key = "plains", movementCost = 1 },
		};
		return (player, armyType, tile);
	}

	private MapUnit AddMember(Player player, Tile tile, UnitPrototype proto, MapUnit army) {
		MapUnit member = proto.GetInstance(gd.GenerateID("member"), proto, player, location: tile);
		member.experienceLevel = gd.defaultExperienceLevel;
		member.experienceLevelKey = gd.defaultExperienceLevelKey;
		tile.unitsOnTile.Add(member);
		Assert.True(member.LoadIntoArmy(army));
		return member;
	}

	[Fact]
	public void AShippedArmyOfMixedSpeedsMovesAtItsSlowestMemberPlusOne() {
		UnitPrototype fastMember = gd.unitPrototypes.First(p => p.name == "Modern Armor");
		UnitPrototype slowMember = gd.unitPrototypes.First(p => p.name == "Warrior");
		Assert.Equal(3, fastMember.movement);
		Assert.Equal(1, slowMember.movement);

		var (player, armyType, tile) = MakeSetup();
		Assert.Equal(1, armyType.movement);
		MapUnit army = armyType.GetInstance(gd.GenerateID("army"), armyType, player, location: tile);
		tile.unitsOnTile.Add(army);

		AddMember(player, tile, fastMember, army);
		AddMember(player, tile, slowMember, army);

		army.movementPoints.onConsumeAll();
		army.OnBeginTurn();

		Assert.Equal(2.0, army.MaxMovementPoints, Tolerance);
		Assert.Equal(2.0, army.movementPoints.remaining, Tolerance);
	}

	// Through the same real import path: an empty shipped army does not keep the
	// Army type's own movement field of 1. It gets MovementAlongRoads - 1
	// internal units, two thirds of a point at the shipped scale of three.
	[Fact]
	public void AShippedEmptyArmyReturnsOneInternalUnitLessThanTheRoadBonus() {
		Assert.Equal(3, gd.rules.MovementAlongRoads);

		var (player, armyType, tile) = MakeSetup();
		MapUnit army = armyType.GetInstance(gd.GenerateID("army"), armyType, player, location: tile);
		tile.unitsOnTile.Add(army);

		army.movementPoints.onConsumeAll();
		army.OnBeginTurn();

		Assert.Equal(2.0 / 3.0, army.MaxMovementPoints, Tolerance);
		Assert.Equal(2.0 / 3.0, army.movementPoints.remaining, Tolerance);
	}
}
