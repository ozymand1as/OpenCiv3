using System.Collections.Generic;
using C7GameData;

namespace C7Engine.Pathing {
	public abstract class EdgeWalker<TNode> {
		public abstract IEnumerable<Edge<TNode>> getEdges(TNode node);
	}

	public class UnitWalker : EdgeWalker<Tile> {
		private MapUnit unit;

		public UnitWalker(MapUnit unit) {
			this.unit = unit;
		}

		public override IEnumerable<Edge<Tile>> getEdges(Tile node) {
			List<Edge<Tile>> result = new List<Edge<Tile>>();
			foreach (KeyValuePair<TileDirection, Tile> pair in node.neighbors) {
				TileDirection direction = pair.Key;
				Tile neighbor = pair.Value;
				bool neighborHasCityWithSameOwner = neighbor.cityAtTile != null && neighbor.cityAtTile.owner == unit.owner;

				bool isPassable = false;

				if (unit.owner.isHuman && !unit.owner.HasExploredTile(neighbor)) {
					isPassable = true;
				} else {
					if (unit.IsLandUnit())
						isPassable = neighbor.IsLand();
					else if (unit.IsWaterUnit())
						isPassable = neighbor.IsWater() || neighborHasCityWithSameOwner;
				}

				if (!isPassable) {
					continue;
				}

				float tileMovementCost = TilePath.GetMovementCost(unit.owner, node, direction, neighbor, unit);
				// An illegal step has no edge at all: the original's pathfinder
				// rejects the cost function's -1 sentinel at 0x580b34-0x580b36,
				// where a sign test on the returned cost leaves the neighbour's
				// update before it is applied, and treating the sentinel as a
				// number here would give the edge a negative weight.
				if (tileMovementCost == TilePath.IllegalStepCost) {
					continue;
				}
				// An army's per-turn budget is its slowest member's rate plus one
				// point, not its own prototype's rate (11_movement.md §2.2).
				float unitMovementPoints = unit.MaxMovementPoints;

				// If this tile would consume all of the movement points of this
				// unit, it has a cost of 1 turn. Otherwise we use the fraction
				// of a turn it would use as the cost.
				//
				// Examples:
				//  - Warrior (1mp) moving onto Grassland (cost 1) => 1 turn
				//  - Warrior (1mp) moving onto Hills (cost 2) => 1 turn
				//  - Warrior (1mp) moving onto Jungle (cost 3) => 1 turn
				//
				//  - Cavalry (3mp) moving onto Grassland (cost 1) => 1/3
				//  - Cavalry (3mp) moving onto Hills (cost 2) => 2/3
				//  - Cavalry (3mp) moving onto Jungle (cost 3) => 3/3
				//
				if (tileMovementCost >= unitMovementPoints) {
					result.Add(new Edge<Tile>(node, neighbor, 1));
				} else {
					result.Add(new Edge<Tile>(node, neighbor, tileMovementCost / unitMovementPoints));
				}
			}
			return result;
		}
	}
}
