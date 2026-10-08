using System.Collections.Generic;
using System.Linq;
using C7Engine;

namespace C7GameData {
	/// <summary>
	/// The pollution half of 18_terrain_improvement.md sections 6 and 7.
	///
	/// Cities generate pollution from their population and from their
	/// buildings; each turn every city rolls against that total and may put a
	/// polluted tile somewhere in its radius. Pollution zeroes the yields of the
	/// tile it lands on, and the "clear damage" worker job removes it. The
	/// accumulated pollution of every city also drives global warming, which
	/// converts tiles according to their terrain's TERR.PollutionEffect column
	/// and destroys mines and irrigation as it does so.
	/// </summary>
	public partial class City {
		/// <summary>
		/// The pollution this city generates from its population, exactly as
		/// Civ3's City_get_pollution_from_pop does (section 6.1): no pollution
		/// at or below RULE.MaximumLevel2CitySize, then one point per citizen
		/// above it, but never more than 1 while the city has a non-obsolete
		/// building that removes population pollution (Mass Transit System's
		/// ITF_Removes_Population_Pollution flag).
		/// </summary>
		public int PollutionFromPopulation() {
			int threshold = owner.rules.MaximumLevel2CitySize;
			if (residents.Count <= threshold) {
				return 0;
			}

			if (HasNonObsoleteBuilding(b => b.removesPopulationPollution)) {
				return 1;
			}

			return residents.Count - threshold;
		}

		/// <summary>
		/// The pollution this city generates from its buildings, exactly as
		/// Civ3's City_get_pollution_from_buildings does (section 6.2): the sum
		/// of the buildings' BLDG.Pollution values, capped at 1 while the city
		/// has a non-obsolete building that reduces building pollution
		/// (Recycling Center's ITF_Reduces_Buildings_Pollution flag).
		/// </summary>
		public int PollutionFromBuildings() {
			int raw = GetBuildings().Sum(cb => cb.building.pollution);
			if (raw <= 0) {
				return 0;
			}

			if (HasNonObsoleteBuilding(b => b.reducesBuildingPollution)) {
				return 1;
			}

			return raw;
		}

		/// <summary>
		/// The value Civ3's City_get_total_pollution returns: the sum of the
		/// population and building terms (section 6.3). It is also the
		/// percentage chance that this city pollutes a tile on a given turn.
		/// </summary>
		public int TotalPollution() {
			return PollutionFromPopulation() + PollutionFromBuildings();
		}

		private bool HasNonObsoleteBuilding(System.Func<Building, bool> predicate) {
			return GetBuildings().Any(cb => predicate(cb.building) && !IsBuildingObsolete(cb.building));
		}

		// Civ3 checks the building's RenderedObsoleteBy field (BLDG + 0xE0)
		// against the owner's known technologies.
		private bool IsBuildingObsolete(Building building) {
			return building.renderedObsoleteBy != null
				&& owner.knownTechs.Contains(building.renderedObsoleteBy.id);
		}
	}

	public static class Pollution {
		// Civ3 rolls rand() % 100 and pollutes when the roll is below the
		// city's total pollution.
		private const int PollutionRollRange = 100;

		// Civ3 makes floor(accumulated / 10) + 1 global-warming attempts per
		// turn, so at least one is always made even when the accumulated
		// pollution is zero (in which case no attempt can hit).
		private const int GlobalWarmingAttemptDivisor = 10;

		// Civ3 rejects every global-warming target terrain id of 11 or greater
		// (clause: the water terrains and anything past them).
		private const int LargestAllowedWarmingTargetId = 11;

		/// <summary>
		/// Rolls for this city's pollution and, on success, marks one of the
		/// twenty non-centre tiles of the city's radius as polluted. Returns
		/// true when a tile was polluted.
		///
		/// Follows Civ3's City_update_spawn_pollution (section 6.3): the target
		/// must be on the same landmass as the city, must not be water, and must
		/// not already be polluted; at most twenty candidates are examined, so a
		/// crowded neighbourhood can fail to find a spot.
		/// </summary>
		public static bool SpawnPollution(City city) {
			int total = city.TotalPollution();
			if (total <= 0) {
				return false;
			}

			if (GameData.rng.Next(PollutionRollRange) >= total) {
				return false;
			}

			Tile target = ChoosePollutionTile(city);
			if (target == null) {
				return false;
			}

			Tile.TryAddPollution(target);
			if (city.owner.isHuman) {
				new MsgShowTemporaryPopup("Pollution!", target).send();
			}
			return true;
		}

		private static Tile ChoosePollutionTile(City city) {
			List<Tile> radius = city.location.GetTilesWithinRankDistance(city.owner.rules.MaxRankOfWorkableTiles);

			// Index 0 is the city's own tile, which cannot be polluted. Walk the
			// remaining candidates cyclically from a random start, like Civ3's
			// "random index, then scan in order" loop.
			int count = radius.Count - 1;
			if (count <= 0) {
				return null;
			}

			int start = GameData.rng.Next(count);
			for (int i = 0; i < count; ++i) {
				Tile candidate = radius[1 + (start + i) % count];
				if (candidate == null || candidate == Tile.NONE) {
					continue;
				}
				if (candidate.continent != city.location.continent) {
					continue;
				}
				if (candidate.IsWater() || candidate.HasPollution()) {
					continue;
				}
				return candidate;
			}

			return null;
		}

		/// <summary>
		/// Runs Civ3's once-per-turn global-warming pass (section 7): sum the
		/// pollution of every city plus the square of the number of nuclear
		/// weapons used, make floor(sum / 10) + 1 attempts, and on each attempt
		/// that beats the sum, draw a tile and convert it to the terrain its
		/// current terrain's TERR.PollutionEffect names, clearing mine and
		/// irrigation as it does so.
		/// </summary>
		public static void DoPerTurnGlobalWarming(GameData gameData) {
			int tileCount = gameData.map.tiles.Count;
			if (tileCount <= 0) {
				return;
			}

			int accumulated = gameData.cities.Sum(c => c.TotalPollution())
				+ (gameData.nukesUsed * gameData.nukesUsed);

			int attempts = accumulated / GlobalWarmingAttemptDivisor + 1;
			for (int i = 0; i < attempts; ++i) {
				if (GameData.rng.Next(tileCount) >= accumulated) {
					continue;
				}

				Tile tile = gameData.map.tiles[GameData.rng.Next(tileCount)];
				if (tile == null || tile == Tile.NONE || !tile.IsLand()) {
					continue;
				}

				WarmTile(tile, gameData);
			}
		}

		private static void WarmTile(Tile tile, GameData gameData) {
			TerrainType target = ResolveWarmingTarget(tile, gameData, out int targetId);
			if (target == null || target == tile.overlayTerrainType) {
				return;
			}

			// Civ3 changes the tile's terrain and then clears the mine and
			// irrigation bits (0xC). In OpenCiv3's layered model mine and
			// irrigation share the ResourceDevelopment layer, so at most one of
			// them is present. When the target is the tile's own underlying
			// terrain (how forests and jungles are stripped) the underlying
			// assignment is simply a no-op.
			tile.overlayTerrainType = target;
			tile.baseTerrainType = target;
			tile.RemoveImprovementAtLayer(TerrainImprovement.Layer.ResourceDevelopment);

			Player human = gameData.players.FirstOrDefault(p => p.id == EngineStorage.uiControllerID)
				?? gameData.GetFirstHumanPlayer();
			if (human == null || !human.tileKnowledge.isTileKnown(tile)) {
				return;
			}

			// Civ3 picks the message by the resulting terrain id: the four
			// basic terrains get the plain global-warming message, anything
			// else (a stripped forest or jungle in practice) its own.
			string message = targetId < 4
				? "Global warming has damaged the land!"
				: "Global warming has destroyed the forest!";
			new MsgShowTemporaryPopup(message, tile).send();
		}

		/// <summary>
		/// Resolves the terrain a tile becomes under global warming from its
		/// terrain's TERR.PollutionEffect column, returning null when the tile
		/// is immune. The resulting Civ3 terrain id is reported through
		/// targetId so callers can tell the two warming messages apart.
		/// </summary>
		private static TerrainType ResolveWarmingTarget(Tile tile, GameData gameData, out int targetId) {
			targetId = -1;

			int effect = tile.overlayTerrainType.pollutionEffect;
			if (effect < 0) {
				return null;
			}

			// 0xE means "fall back to the tile's underlying terrain", which is
			// how forests and jungles are stripped.
			if (effect == TerrainType.UnderlyingTerrainPollutionEffect) {
				targetId = TerrainType.Civ3TerrainIdForKey(tile.baseTerrainType.Key);
				return tile.baseTerrainType;
			}

			if (effect >= LargestAllowedWarmingTargetId) {
				return null;
			}

			string key = TerrainType.KeyForCiv3TerrainId(effect);
			TerrainType target = key == null ? null : gameData.terrainTypes.Find(t => t.Key == key);
			if (target == null) {
				return null;
			}

			targetId = effect;
			return target;
		}
	}
}
