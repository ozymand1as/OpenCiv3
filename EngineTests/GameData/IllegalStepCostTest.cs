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
/// sentinel -1, the executor tests for it (`cmpl $-0x1, %eax` at 0x5b9467) and
/// asks Unit_can_move_to_adjacent_tile at 0x5b9471: a refusal returns 1 from
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
	// direction gate rejects index 0 at 0x57f40f (`testl %eax,%eax` /
	// `jle 0x57fe05`), which is the only reason this step is illegal at all -
	// the destination it resolves to is the unit's own tile. TileDirection.INVALID
	// is the fork's index 0.
	//
	// A charge-only implementation leaves 2 of the 3 points (the tile costs 1);
	// the whole-maximum charge leaves none.
	[Fact]
	public async Task AStepWithAnIllegalCostChargesTheWholeMaximum() {
		Tile source = PrepareCleanStep(destinationMovementCost: 1);
		MapUnit unit = MakeUnit(MakePlayer(), MakeLandPrototype("Mover", movement: 3), source);
		Assert.Equal(3.0, unit.movementPoints.remaining, Tolerance);

		Assert.True(await unit.Move(TileDirection.INVALID));

		Assert.Equal(0.0, unit.movementPoints.remaining, Tolerance);
		// Spent equals the maximum, which in C7's remainder form is no remainder.
		Assert.Equal(unit.MaxMovementPoints, unit.MaxMovementPoints - unit.movementPoints.remaining, Tolerance);
		// The step is not an amphibious assault, so step 9's override is not what
		// emptied the budget.
		Assert.False(unit.IsAmphibiousAssaultStep(source, source));
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
