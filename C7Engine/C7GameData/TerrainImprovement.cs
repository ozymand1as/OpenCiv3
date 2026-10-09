using System.Collections.Generic;
using C7Engine.Lua;
using C7GameData.Save;
using System;
using System.Linq;
using C7Engine;

namespace C7GameData {
	public class TerrainImprovement {
		public enum Layer {
			Roads,
			ResourceDevelopment, // mine, irrigation
			Holdings, // outpost, radar tower, fortress, barricade
			Ruins,
			Pollution,
			Craters,
		}

		public readonly string key;

		// Each tile can have only one type of improvement per layer.
		// This is enforced by the TileOverlays class.
		public readonly Layer layer;
		public readonly StrengthBonus? defenseBonus;

		// On the game map, overlapping terrain improvement with a higher zIndex cover those with a smaller one
		public readonly int zIndex = 0;

		// A terrain improvement's movement cost, in Civ3's internal movement
		// units: one whole movement point is RULE.MovementAlongRoads of them, so
		// a road is 1 and the movement code divides by the rule's scale. A
		// negative cost means the improvement does not affect movement at all.
		//
		// This is the value carried by the save. The step cost does NOT read it:
		// StepCostInInternalUnits derives the road and railroad costs from the
		// rules, so a save written before the scale existed still moves at the
		// shipped rate.
		public readonly float movementCost = -1;

		// This improvement's contribution to a step's internal movement cost
		// (11_movement.md §3.2 and §3.3). Civ3's cost function charges a raw
		// constant for a road (1 internal unit) and for a railroad (0), so the
		// value comes from the improvement's identity and the rules, never from
		// the save-carried movementCost field - an old save stores the fraction
		// 0.33333334 there, and dividing that by the scale would make the step
		// nine times too cheap. TilePath.GetMovementCost then divides by
		// RULE.MovementAlongRoads, which it reads at runtime.
		public int StepCostInInternalUnits =>
			layer == Layer.Roads && key == Tile.TileOverlays.RAILROAD
				? Rules.RailroadStepInternalUnits
				: Rules.RoadStepInternalUnits;

		// In the default ruleset, Road upgrades to Railroad and Fortress upgrades to Barricade.
		// The upgrade relationship affects:
		// 1) confirmation dialog when replacing improvements (if an improvement is replaced with its upgrade, there is no need for a confirmation)
		// 2) availability of an improvement (a player can't build an upgraded improvement without building a base improvement)
		// 3) result of pillaging (after pillaging, an upgraded improvement will downgrade)
		public readonly TerrainImprovement upgradesFrom;

		public readonly Action<Tile.Yield> tileModifier;

		private Dictionary<(TerrainType, Tile.YieldType), int> bonusYields = [];
		private readonly SaveTerrainImprovement dataSource;

		public TerrainImprovement(
			string key,
			Layer layer,
			float movementCost = -1
		) {
			this.key = key;
			this.layer = layer;
			this.movementCost = movementCost;
		}

		public TerrainImprovement(
			SaveTerrainImprovement save,
			BehaviorEngine rulesEngine,
			Func<string, TerrainType> resolveTerrainType,
			TerrainImprovement upgradesFrom = null
		) {
			key = save.key;
			layer = save.layer;
			movementCost = save.movementCost;
			zIndex = save.zIndex;
			defenseBonus = save.defenseBonus;
			dataSource = save;
			this.upgradesFrom = upgradesFrom;

			if (save.tileModifier != null) {
				tileModifier = rulesEngine.ImportFunc<Action<Tile.Yield>>(save.tileModifier);
			}

			bonusYields = [];
			foreach (var (terrainKey, yieldDict) in save.bonusYields) {
				var terrain = resolveTerrainType(terrainKey);
				foreach (var (yieldType, bonus) in yieldDict) {
					bonusYields[(terrain, yieldType)] = bonus;
				}
			}
		}

		public SaveTerrainImprovement ToSaveTerrainImprovement() {
			return dataSource;
		}

		public int GetYieldBonus(TerrainType terrain, Tile.YieldType yieldType) {
			if (bonusYields.TryGetValue((terrain, yieldType), out int res)) {
				return res;
			}

			return 0;
		}

		public bool CanBeReplacedBy(TerrainImprovement replacement) {
			if (this.key == replacement.key) return false;       // mine-mine
			if (this.upgradesFrom == replacement) return false;  // railroad upgrades from road so road cannot replace railroad
			if (replacement.upgradesFrom == this) return true;   // railroad upgrades from road so railroad can replace road
			return this.layer == replacement.layer;              // irrigation can replace mine and vice versa, an outpost a radar tower, etc
		}

		public static Terraform? ToTerraform(string improvement) {
			return EngineStorage.gameData.Terraforms.FirstOrDefault(tf => tf.Improvement.key.ToLower() == improvement.ToLower());
		}
	}
}
