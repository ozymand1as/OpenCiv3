using System;
using System.Collections.Generic;
using System.Numerics;
using C7Engine;

namespace C7GameData {
	public class TilePath {
		public Tile destination { get; private set; } //stored in case we need to re-calculate
		public Queue<Tile> path { get; private set; }

		private TilePath() {
			destination = Tile.NONE;
			path = new Queue<Tile>();
		}

		public TilePath(Tile destination, Queue<Tile> path) {
			this.destination = destination;
			this.path = path;
		}

		// The next tile in the path, or Tile.NONE if there
		// are no remaining tiles, or the path is invalid
		public Tile Next() {
			return PathLength() > 0 ? path.Dequeue() : Tile.NONE;
		}

		public Tile PeekNext() {
			return PathLength() > 0 ? path.Peek() : Tile.NONE;
		}

		//TODO: Once we have roads, we should return the calculated cost, not just the length.
		//This will require Dijkstra or another fancier pathing algorithm
		public int PathLength() {
			return path != null ? path.Count : -1;
		}

		public int PathCost(Player player, Tile from, float perTurnMovePoints, float remainingMovementPoints) {
			// If we have no path (such as if we are a land unit trying to move to the water)
			// return -1 so we don't display a goto cursor.
			if (path == null || path.Count == 0) { return -1; }

			int turns = 0;

			// Start out with however many movement points the unit has left.
			MovementPoints movementPoints = new();
			movementPoints.reset(remainingMovementPoints);

			foreach (Tile tile in path) {
				// Subtract the cost of the next move. An illegal step means the path
				// cannot be walked at all, the same answer an empty path gives.
				float cost = GetMovementCost(player, from, from.DirectionTo(tile), tile);
				if (cost == IllegalStepCost) { return -1; }
				movementPoints.onUnitMove(cost);

				// If we can't do any more moves, bump up the turn cost and reset
				// our MP to the base value.
				if (!movementPoints.canMove) {
					++turns;
					movementPoints.reset(perTurnMovePoints);
				}

				from = tile;
			}

			// Special case: if we consumed part of our movement points (such as by
			// walking along a road, consuming 1/3 of a point), round up the cost.
			// This prevents showing 0 turns when moving one tile along a road, or
			// 1 turn when moving 4 tiles along a road.
			if (movementPoints.remaining < (turns == 0 ? remainingMovementPoints : perTurnMovePoints)) {
				++turns;
			}

			return turns;
		}

		public HashSet<Vector2> GetPathCoords() {
			HashSet<Vector2> result = new();

			foreach (Tile tile in path) {
				result.Add(new Vector2(tile.XCoordinate, tile.YCoordinate));
			}

			return result;
		}

		// Indicates no path was found to the requested destination.
		public static TilePath NONE = new TilePath();

		// The value Civ3's Trade_Net_get_movement_cost @ 0x57f360 returns for a
		// step it considers illegal: every bit set, i.e. -1. The caller
		// (Unit_move_to_adjacent_tile) tests for it at 0x5b9467, and when it sees
		// it asks Unit_can_move_to_adjacent_tile and either refuses the step or
		// charges the unit's whole maximum movement (11_movement.md §3, §5 step 6).
		//
		// The ordinary step reaches this value only from the function's first
		// block: a destination outside the map, a direction index outside 1-8, or a
		// pair of tiles that is not adjacent (0x57fe05, reached from 0x57f3ac and
		// 0x57f3b8, from 0x57f407 and 0x57f40f, and from the adjacency test at
		// 0x57f44a). The occupancy/territory/flag block that produces the
		// function's other -1 returns is skipped whenever bit 31 of the flag
		// bitmask is set (0x57f363 extracts it, 0x57f484 and 0x57faa2 test it), and
		// the ordinary step's mask 0x80000080 (pushed at 0x5b9447) has it set, as
		// does the path buffer pop's 0x80000000 (pushed at 0x5b335c).
		public const float IllegalStepCost = -1f;

		// A valid path of length 0
		public static TilePath EmptyPath(Tile destination) {
			return new TilePath(destination, new Queue<Tile>());
		}

		public static float GetMovementCost(Player player, Tile from, TileDirection dir, Tile newLocation, MapUnit unit = null) {
			// A destination outside the map, or one that is not a neighbour of the
			// source, is an illegal step: the original's cost function refuses it
			// before looking at any terrain (11_movement.md §1.1 and §3). Its
			// adjacency test is exactly `(xDist + yDist) / 2 == 1`, which is also
			// what Tile.DistanceTo computes. The pathfinder only ever evaluates
			// neighbouring tiles, so this cannot change a path's shape; it is the
			// step executor that has to decide what an illegal cost means.
			if (from == null || newLocation == null || from == Tile.NONE || newLocation == Tile.NONE)
				return IllegalStepCost;
			if (from.DistanceTo(newLocation) != 1)
				return IllegalStepCost;

			// If the player hasn't yet explored a tile, assume it's a generic tile
			// and give back a generic cost, since we don't know yet what it is.
			if (player.isHuman && !player.HasExploredTile(newLocation))
				return 1f;

			// The barricade override comes first among the real rules because the
			// spec applies it after all of them: section 3.8 says "After all of the
			// above, one further override applies" and "the cost is replaced". So a
			// barricade consumes the remaining movement even when a road, a
			// railroad, the per-terrain ignore rule or the sea-into-city special
			// case would otherwise return a smaller cost - the barricade supersedes
			// every other cost branch (11_movement.md §3.8). The override is
			// conditional on its own three conditions (barricade present, foreign
			// owner, no right of passage) and on a non-null unit; when those do not
			// hold it does not fire at all, and the branches below decide the cost.
			//
			// The original computes the charge as (max move points - spent) times
			// the movement scale, an overshoot that leaves the remainder at zero;
			// returning the remainder that is left has the same observable result.
			if (unit != null && BarricadeConsumesRemainingMovement(player, newLocation)) {
				return unit.movementPoints.remaining;
			}

			// River crossings disrupt roads, so check that first. Foreign territory
			// does the same without a right of passage (11_movement.md §3.4 and
			// §3.10). Both conditions deny only the road/railroad discount: the
			// per-terrain ignore rule below still applies when they fail.
			bool roadDiscountAllowed = (!from.HasRiverCrossing(dir) || player.CanBridgeRoads())
				&& Player.CanMoveFreely(player, from, newLocation);

			if (roadDiscountAllowed) {
				// Special case: if we are a water unit, traveling from the water into
				// a city, it doesn't matter if the city is on hills or on grassland,
				// the cost should always be 1.
				if (from.IsWater() && newLocation.HasCity()) return 1;

				// Movement costs of terrain improvements (roads and railroads). A road
				// or railroad only discounts a step when both the source and the
				// destination carry the improvement; when only one end does, the
				// destination's terrain cost applies. A tile with no improvement at
				// all - including a city tile with no road on it - yields null rather
				// than a cost, so it cannot be mistaken for a cheap end.
				float? fromRoadCost = from.overlays.RoadMovementCost();
				float? toRoadCost = newLocation.overlays.RoadMovementCost();

				if (fromRoadCost.HasValue && toRoadCost.HasValue) {
					// Railroads are free and also count as roads, so the slower end
					// determines the step: rail-to-rail is free, while rail-to-road
					// and road-to-road cost the road cost. That cost is the original's
					// constant in Civ3's internal movement units, taken from the rules
					// (not from the improvement's save-carried movementCost), and the
					// movement scale converts it into movement points: a road step
					// costs 1 / MovementAlongRoads, which is 1/3 of a point with the
					// shipped scale of 3, and a scenario that changes the scale changes
					// the road step with it (11_movement.md §2.1, §3.2).
					return Math.Max(fromRoadCost.Value, toRoadCost.Value) / MovementPointsScale;
				}
			}

			// Some unit types ignore the movement cost of particular terrains: the
			// step costs ONE WHOLE MOVEMENT POINT instead of the terrain's MoveCost,
			// but only when the road/railroad branch above did not already discount
			// it (11_movement.md §3.6). This is what makes the Keshik's and the
			// Chasqui Scout's hill and mountain steps cheap - one point instead of
			// the two or three the terrain itself charges.
			//
			// The original loads RULE.MovementAlongRoads into the cost register for
			// this branch (0x58036f), the same register value the air branch loads at
			// that address and the sea-into-a-city branch loads at 0x580336. It is
			// NOT the raw 1 internal unit the road branch (0x580282) and the
			// all-terrain-as-roads branch (0x5802a9, 0x5802e7) charge; an earlier
			// reading of this rule as "1 internal unit = a third of a point" was
			// wrong. Written in internal units over the scale so the branch reads the
			// movement scale at runtime like its neighbours and a scenario with a
			// different MovementAlongRoads still charges exactly one point.
			if (unit != null && unit.IgnoresMovementCostOf(newLocation.overlayTerrainType)) {
				int scale = MovementPointsScale;
				return Rules.OneMovementPointInternalUnits(scale) / (float)scale;
			}

			return newLocation.MovementCost(); // terrain movement cost
		}

		// Whether stepping onto this tile triggers the barricade movement rule:
		// a barricade on a tile owned by a civ that is neither the mover nor
		// unowned, with no active right of passage for the mover (11_movement.md
		// §3.8). A fortress on the same tile has no movement effect.
		private static bool BarricadeConsumesRemainingMovement(Player player, Tile destination) {
			if (!destination.HasBarricade()) {
				return false;
			}

			Player owner = destination.OwningPlayer();
			if (owner == null || owner == player) {
				return false;
			}

			return !PlayerRelationship.HaveActiveRightOfPassage(player, owner);
		}

		// Civ3's movement scale: RULE.MovementAlongRoads, the number of internal
		// movement units in one movement point, which the original reads at
		// runtime. The terrain costs above are already in movement points, but a
		// terrain improvement's cost is in internal units, so this converts it.
		//
		// A game with no rules loaded (a unit test that never set up game data)
		// falls back to the shipped default rather than dividing by zero.
		private static int MovementPointsScale {
			get {
				int scale = EngineStorage.gameData?.rules?.MovementAlongRoads ?? Rules.DefaultMovementAlongRoads;
				return Math.Max(1, scale);
			}
		}
	}
}
