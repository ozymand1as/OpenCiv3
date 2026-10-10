using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using C7Engine;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using Xunit;

namespace EngineTests.GameData;

/// <summary>
/// Guards the illegal-cost charge (11_movement.md §3 and §5 step 6): when the
/// cost function reports a step illegal but the step is nonetheless legal to
/// take, the unit is charged its WHOLE MAXIMUM movement.
///
/// The original's cost function reports an illegal step with the all-bits-set
/// sentinel -1, the executor compares the cost against that sentinel at 0x5b9467
/// and asks Unit_can_move_to_adjacent_tile at 0x5b9471: a refusal returns 1 from
/// 0x5b9478 and the step never happens, otherwise the charge becomes
/// Unit_get_max_move_points at 0x5b9480. The charge is added to the spent field
/// at 0x5b94b2 (step 8), and step 9's amphibious override overwrites it with the
/// maximum at 0x5b94e9.
///
/// Where the sentinel comes from for an ordinary step is narrow: with the mask
/// 0x80000080 the occupancy/territory/flag block is skipped, so only an off-map
/// destination, a direction index outside 1-8 and a non-adjacent pair can
/// produce it. The end-to-end test below uses the direction route, which is the
/// only one reachable through MapUnit.Move.
///
/// The direction route's reachable case is a same-tile step, and its legality is
/// settled rather than assumed: the executor hands Unit_can_move_to_adjacent_tile
/// the step's own direction index, index 0 included, and for index 0 that
/// predicate examines the unit's own tile - where the occupier is the mover's
/// own civ, so both occupancy blocks are skipped, the own tile passes the
/// between-tiles test, and the tail's "occupied by a civ the mover is not at war
/// with" check sees the mover's own civ and answers 0 (allowed). The original
/// therefore takes the step and charges the whole maximum, which is what the
/// fork does. The route itself is defensive in the fork: no engine caller can
/// build a TileDirection.INVALID step, because every derived direction comes
/// from Tile.DirectionTo, which answers a compass direction for the own tile
/// too.
/// </summary>
public sealed class IllegalStepCostTest : IClassFixture<SaveGameFixture> {
	private const double Tolerance = 0.0001;
	private const int SourceX = 50;
	private const int SourceY = 50;
	private const int DestinationX = 52;
	private const int DestinationY = 50;
	// Two steps east: adjacent to nothing the step touches.
	private const int RemoteX = 54;

	private readonly C7GameData.GameData gd;

	public IllegalStepCostTest(SaveGameFixture fixture) {
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

	private Tile CleanTile(int x, int y, string key = "test", int movementCost = 0) {
		Tile tile = gd.map.tileAt(x, y);
		if (tile.cityAtTile != null) {
			tile.cityAtTile = null;   // clears the overlays as a side effect
		}
		tile.unitsOnTile.Clear();
		tile.overlays.Clear();
		tile.hasGoodyHut = false;
		tile.hasBarbarianCamp = false;
		tile.overlayTerrainType = new TerrainType { Key = key, movementCost = movementCost };
		tile.baseTerrainType = tile.overlayTerrainType;
		return tile;
	}

	// The step's source, destination and every neighbour of both, so nothing the
	// generator left behind can add a zone-of-control candidate to a step that
	// stays on one tile.
	private Tile PrepareCleanStep(int destinationMovementCost) {
		CleanTile(SourceX, SourceY, movementCost: destinationMovementCost);
		CleanTile(DestinationX, DestinationY, movementCost: destinationMovementCost);
		CleanTile(RemoteX, DestinationY, movementCost: destinationMovementCost);
		foreach (TileDirection dir in TileDirectionExtensions.All) {
			TileLocation source = Tile.NeighborCoordinate(new TileLocation(SourceX, SourceY), dir);
			CleanTile(source.X, source.Y, movementCost: destinationMovementCost);
			TileLocation destination = Tile.NeighborCoordinate(new TileLocation(DestinationX, DestinationY), dir);
			CleanTile(destination.X, destination.Y, movementCost: destinationMovementCost);
		}
		return gd.map.tileAt(SourceX, SourceY);
	}

	private MapUnit MakeUnit(Player owner, UnitPrototype proto, Tile tile) {
		MapUnit unit = proto.GetInstance(gd.GenerateID(proto.name), proto, owner, location: tile);
		unit.experienceLevel = gd.experienceLevels.First(e => e.key == "Regular");
		unit.experienceLevelKey = "Regular";
		unit.hitPointsRemaining = unit.maxHitPoints;
		tile.unitsOnTile.Add(unit);
		gd.mapUnits.Add(unit);
		owner.AddUnit(unit);
		return unit;
	}

	private static UnitPrototype MakeLandPrototype(string name, int movement = 3) {
		UnitPrototype proto = new UnitPrototype { name = name, movement = movement };
		proto.categories.Add("Land");
		return proto;
	}

	// ---------- the sentinel ----------

	[Fact]
	public void AnOffMapDestinationIsAnIllegalStep() {
		Tile source = PrepareCleanStep(destinationMovementCost: 1);
		MapUnit unit = MakeUnit(MakePlayer(), MakeLandPrototype("Mover"), source);

		Assert.Equal(TilePath.IllegalStepCost,
			TilePath.GetMovementCost(unit.owner, source, TileDirection.EAST, Tile.NONE, unit));
	}

	[Fact]
	public void ANonAdjacentDestinationIsAnIllegalStep() {
		Tile source = PrepareCleanStep(destinationMovementCost: 1);
		Tile remote = gd.map.tileAt(RemoteX, DestinationY);
		MapUnit unit = MakeUnit(MakePlayer(), MakeLandPrototype("Mover"), source);

		// The original refuses a pair whose (xDist + yDist) / 2 is not exactly 1
		// (0x57f44a), which is Tile.DistanceTo's own formula.
		Assert.Equal(2, source.DistanceTo(remote));
		Assert.Equal(TilePath.IllegalStepCost,
			TilePath.GetMovementCost(unit.owner, source, TileDirection.EAST, remote, unit));
	}

	[Fact]
	public void ANeighbouringDestinationIsNotAnIllegalStep() {
		Tile source = PrepareCleanStep(destinationMovementCost: 2);
		Tile destination = gd.map.tileAt(DestinationX, DestinationY);
		MapUnit unit = MakeUnit(MakePlayer(), MakeLandPrototype("Mover"), source);

		Assert.Equal(2f, TilePath.GetMovementCost(unit.owner, source, TileDirection.EAST, destination, unit));
	}

	// ---------- the charge ----------

	// The rule itself: an illegal cost that the legality predicate allows is
	// charged the unit's whole maximum, not the terrain cost.
	[Fact]
	public void AnIllegalCostThatIsLegalToTakeChargesTheWholeMaximum() {
		Tile source = PrepareCleanStep(destinationMovementCost: 1);
		Tile remote = gd.map.tileAt(RemoteX, DestinationY);
		MapUnit unit = MakeUnit(MakePlayer(), MakeLandPrototype("Mover", movement: 3), source);

		Assert.True(unit.CanEnter(remote, source));
		Assert.Equal(3.0f, unit.StepCostForCharge(source, TileDirection.EAST, remote));
	}

	// The other half of the branch: an illegal cost on a step the predicate also
	// refuses is a refusal, and the charge never happens (0x5b9478).
	[Fact]
	public void AnIllegalCostThatIsIllegalToTakeIsRefused() {
		Tile source = PrepareCleanStep(destinationMovementCost: 1);
		MapUnit unit = MakeUnit(MakePlayer(), MakeLandPrototype("Mover", movement: 3), source);

		Assert.False(unit.CanEnter(Tile.NONE, source));
		Assert.Null(unit.StepCostForCharge(source, TileDirection.EAST, Tile.NONE));
	}

	// The rule does not fire on a legal-cost step: the charge is the cost, not
	// the maximum.
	[Fact]
	public void ALegalCostStepIsChargedItsOwnCost() {
		Tile source = PrepareCleanStep(destinationMovementCost: 2);
		Tile destination = gd.map.tileAt(DestinationX, DestinationY);
		MapUnit unit = MakeUnit(MakePlayer(), MakeLandPrototype("Mover", movement: 3), source);

		Assert.Equal(2.0f, unit.StepCostForCharge(source, TileDirection.EAST, destination));
	}

	// ---------- the end-to-end step ----------

	// The one route to the sentinel a step issued through MapUnit.Move can take:
	// a direction outside the eight the cost function accepts. The original's
	// direction gate at 0x57f40f refuses any index below 1 (and any index above 8
	// at 0x57f407), and the destination a zero index resolves to is the unit's own
	// tile: the direction-to-delta table at 0x5e6e50 maps index 0 to a zero
	// offset. TileDirection.INVALID is the fork's way of asking for that same-tile
	// step - its direction-to-delta conversion is the zero offset too - and the
	// step's own tile is not adjacent to itself, which is how the fork's cost
	// function refuses the step.
	//
	// The original takes this step, which is what makes the whole-maximum charge
	// correct rather than merely defensive. In the cost-illegal branch the
	// executor pushes the step's direction index as Unit_can_move_to_adjacent_tile's
	// neighbour index (0x5b946c-0x5b9471, and the index is the same one the cost
	// function was given), so index 0 sends that predicate to the unit's own tile.
	// The tile's occupier there is the mover's own civ, so the routine skips both
	// occupancy blocks, the own tile passes the between-tiles test, and the tail's
	// "occupied by a civ the mover is not at war with" check sees the mover's own
	// civ and answers 0 (allowed). The executor then charges the whole maximum.
	//
	// A charge-only implementation leaves 2 of the 3 points (the tile costs 1);
	// the whole-maximum charge leaves none.
	[Fact]
	public async Task AStepWithAnIllegalCostChargesTheWholeMaximum() {
		Tile source = PrepareCleanStep(destinationMovementCost: 1);
		MapUnit unit = MakeUnit(MakePlayer(), MakeLandPrototype("Mover", movement: 3), source);
		Assert.Equal(3.0, unit.movementPoints.remaining, Tolerance);

		// The fork's shape of the original's index-0 answer: the legality predicate
		// allows the step onto the tile the unit already stands on, which is what
		// makes the charge below the whole maximum rather than a refusal.
		Assert.True(unit.CanEnter(source, source));

		Assert.True(await unit.Move(TileDirection.INVALID));

		Assert.Equal(0.0, unit.movementPoints.remaining, Tolerance);
		// Spent equals the maximum, which in C7's remainder form is no remainder.
		Assert.Equal(unit.MaxMovementPoints, unit.MaxMovementPoints - unit.movementPoints.remaining, Tolerance);
		// The step is not an amphibious assault, so step 9's override is not what
		// emptied the budget.
		Assert.False(unit.IsAmphibiousAssaultStep(source, source));
	}

	// The same-tile step is a defensive path in the fork, not one the game can
	// reach: every direction an engine caller derives comes from Tile.DirectionTo,
	// and that answers a compass direction for the unit's own tile as well (the
	// zero angle lands on EAST), so no caller ever hands MapUnit.Move the INVALID
	// direction. Only an explicit TileDirection.INVALID argument reaches the route
	// the test above pins - which is why the route's faithfulness rests on the
	// original's index-0 answer, established in the comment above, rather than on
	// gameplay evidence.
	[Fact]
	public void DirectionToAlwaysAnswersACompassDirection() {
		Tile source = PrepareCleanStep(destinationMovementCost: 1);

		// The unit's own tile: a zero delta, which is not INVALID.
		Assert.Equal(TileDirection.EAST, source.DirectionTo(source));

		// And every real neighbour resolves to one of the eight compass
		// directions, so the sentinel direction is never derived from geometry.
		foreach (TileDirection dir in TileDirectionExtensions.All) {
			Assert.Contains(source.DirectionTo(source.neighbors[dir]), TileDirectionExtensions.All);
		}
	}

	// Step 6's whole-maximum charge and step 9's amphibious override both leave
	// the unit with nothing, so they cannot disagree when both apply. Step 9 is a
	// SET of the spent field (0x5b94e9) and therefore wins over step 8's add.
	[Fact]
	public void TheIllegalCostMaximumAndTheAmphibiousOverrideBothLeaveNoRemainder() {
		Tile water = CleanTile(SourceX, SourceY, key: "coast", movementCost: 1);
		Tile remote = CleanTile(RemoteX, DestinationY, movementCost: 1);
		MapUnit unit = MakeUnit(MakePlayer(), MakeLandPrototype("Mover", movement: 3), water);

		// A water source and a land destination two tiles away: the pair is not
		// adjacent, so the cost is illegal, and the step is an amphibious assault.
		Assert.True(unit.IsAmphibiousAssaultStep(water, remote));
		float? cost = unit.StepCostForCharge(water, TileDirection.EAST, remote);
		Assert.Equal(3.0f, cost);

		unit.movementPoints.onUnitMove(cost.Value);
		Assert.Equal(0.0, unit.movementPoints.remaining, Tolerance);

		// The override is a set of the spent field, so it lands on the same value.
		unit.movementPoints.reset(3f);
		unit.movementPoints.onConsumeAll();
		Assert.Equal(0.0, unit.movementPoints.remaining, Tolerance);
	}
}
