using System;
using System.Collections.Generic;
using System.Numerics;

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
				// Subtract the cost of the next move.
				float cost = GetMovementCost(player, from, from.DirectionTo(tile), tile);
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

		// A valid path of length 0
		public static TilePath EmptyPath(Tile destination) {
			return new TilePath(destination, new Queue<Tile>());
		}

		public static float GetMovementCost(Player player, Tile from, TileDirection dir, Tile newLocation, MapUnit unit = null) {
			// If the player hasn't yet explored a tile, assume it's a generic tile
			// and give back a generic cost, since we don't know yet what it is.
			if (player.isHuman && !player.HasExploredTile(newLocation))
				return 1f;

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
					// and road-to-road cost the road improvement's movement cost.
					return Math.Max(fromRoadCost.Value, toRoadCost.Value);
				}
			}

			// Some unit types ignore the movement cost of particular terrains: the
			// step costs one movement point instead of the terrain's MoveCost, but
			// only when the road/railroad branch above did not already discount it
			// (11_movement.md §3.6). This is what makes the Keshik's and the
			// Chasqui Scout's hill and mountain steps cheap.
			if (unit != null && unit.unitType.IgnoresMovementCostOf(newLocation.overlayTerrainType)) {
				return 1f;
			}

			// A barricade in territory the mover has no right of passage through
			// consumes all of the unit's remaining movement, whatever the step
			// would otherwise have cost (11_movement.md §3.8). The original reads
			// the spent-movement field and charges the remainder times the movement
			// scale, an overshoot that leaves the remainder at zero; clamping the
			// remainder at zero makes "charge everything left" the same result.
			if (unit != null && BarricadeConsumesRemainingMovement(player, newLocation)) {
				return unit.movementPoints.remaining;
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
	}
}
