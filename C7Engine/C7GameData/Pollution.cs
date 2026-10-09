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
		/// Civ3's City_get_pollution_from_pop @ 0x4b1a50 does (section 6.1): no
		/// pollution at or below RULE.MaximumLevel2CitySize, then one point per
		/// citizen above it, but never more than 1 while the city has a
		/// non-obsolete building that removes population pollution (Mass Transit
		/// System's ITF_Removes_Population_Pollution flag, bit 5 of BLDG.Flags[0]
		/// at BLDG + 0xEC). **[C]**
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
		/// Civ3's City_get_pollution_from_buildings @ 0x4b1b40 does (section
		/// 6.2): the sum of the buildings' BLDG.Pollution values (BLDG + 0xCC),
		/// capped at 1 while the city has a non-obsolete building that reduces
		/// building pollution (Recycling Center's ITF_Reduces_Buildings_Pollution
		/// flag, bit 6 of BLDG.Flags[0]). **[C]**
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
		/// The value Civ3's City_get_total_pollution @ 0x4b1c10 returns: the sum
		/// of the population and building terms, each clamped at zero (section
		/// 6.3). It is also the percentage chance that this city pollutes a tile
		/// on a given turn. **[C]**
		/// </summary>
		public int TotalPollution() {
			return PollutionFromPopulation() + PollutionFromBuildings();
		}

		private bool HasNonObsoleteBuilding(System.Func<Building, bool> predicate) {
			return GetBuildings().Any(cb => predicate(cb.building) && !IsBuildingObsolete(cb.building));
		}

		// Civ3 checks the building's RenderedObsoleteBy field (BLDG + 0xE0)
		// against the owner's known technologies, and an obsolete building is
		// skipped before its pollution flag is ever examined (0x4b1a50 /
		// 0x4b1b40). **[C]**
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
		/// Follows Civ3's City_update_spawn_pollution @ 0x4b2f80 (section 6.3):
		/// nothing happens when the city's total pollution is zero, otherwise a
		/// roll of rand() % 100 must come in below that total, and then the
		/// first candidate that is inside the map, shares the city's area id and
		/// is not water is marked. At most twenty candidates are examined, so a
		/// crowded neighbourhood can fail to find a spot. **[C]**
		///
		/// NOTE (measured, and a deviation from the spec's section 6.3 step 3):
		/// the spec lists "is not already polluted" as a fourth candidate test,
		/// but the binary's candidate loop at 0x4b2f80 tests only the area id
		/// (the tile's word at +0x6C against the city's dword at +0x20) and the
		/// water bit (m35_Check_Is_Water @ 0x5eaa30), then sets overlay bit 0x40
		/// unconditionally. An already-polluted candidate is therefore re-marked
		/// rather than skipped, and this implementation does the same.
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

		/// <summary>
		/// The tile this city's pollution lands on, or null when none is
		/// eligible.
		///
		/// Civ3's loop at 0x4b2f80 draws rand() % 20 and then walks radius
		/// indices (draw + k) % 20 + 1 for k = 1..20, i.e. the twenty non-centre
		/// tiles of the twenty-one tile city radius, in order from a random
		/// start. This method walks OpenCiv3's equivalent set the same way. **[C]
		/// for the shape; [L] for the set, since OpenCiv3's rank-distance spiral
		/// orders the twenty tiles differently from Civ3's neighbour-index
		/// spiral (Tile.GetTileAtNeighborIndex's own comment records that
		/// difference), so the specific tile chosen is not Civ3's.
		///
		/// OPEN ITEM: the candidate set's identity as Civ3's is therefore
		/// unproven. What matches is the count (twenty), the random cyclic start
		/// and the eligibility rules (same area id, not water).
		/// </summary>
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
				if (candidate.IsWater()) {
					continue;
				}
				return candidate;
			}

			return null;
		}

		/// <summary>
		/// The accumulated pollution P that drives global warming (section 7 step
		/// 1): the total pollution of every city in the game plus the square of
		/// the number of nuclear weapons used (the global at 0xa52698). Civ3
		/// keeps this in a global and resets it on scenario load; here it is
		/// recomputed from the game state each turn, which is equivalent while
		/// the nuke counter is never incremented (no code launches a nuclear
		/// weapon yet, so that term is always zero - see GameData.nukesUsed).
		/// **[C]** for the sum and the nuke term (0x4f4380).
		/// </summary>
		public static int AccumulatedPollution(GameData gameData) {
			return gameData.cities.Sum(c => c.TotalPollution())
				+ (gameData.nukesUsed * gameData.nukesUsed);
		}

		/// <summary>
		/// Civ3's global-warming severity indicator (section 7 step 2), the value
		/// the engine publishes at 0xa5269c for the UI: 3 once the accumulated
		/// pollution exceeds half the map's tiles, 2 past a quarter, 1 for any
		/// pollution at all, and 0 for a clean world. It has no gameplay effect;
		/// the per-turn warming pass records it in
		/// GameData.globalWarmingSeverity so the figure the engine computed is
		/// available to the UI. **[C]** (0x4f4380 writes 0xa5269c).
		///
		/// The comparisons are against integer halves and quarters of the tile
		/// count. Integer and real division agree here because P is an integer:
		/// P > floor(n/2) and P > n/2 select the same P.
		/// </summary>
		public static int GlobalWarmingSeverity(GameData gameData) {
			int tileCount = gameData.map?.tiles?.Count ?? 0;
			int accumulated = AccumulatedPollution(gameData);

			if (accumulated > tileCount / 2) {
				return 3;
			}
			if (accumulated > tileCount / 4) {
				return 2;
			}
			if (accumulated > 0) {
				return 1;
			}
			return 0;
		}

		/// <summary>
		/// Runs Civ3's once-per-turn global-warming pass (section 7): sum the
		/// pollution of every city plus the square of the number of nuclear
		/// weapons used, make floor(sum / 10) + 1 attempts, and on each attempt
		/// that beats the sum, draw a tile and convert it to the terrain its
		/// current terrain's TERR.PollutionEffect names, clearing mine and
		/// irrigation as it does so. **[C]** (FUN_004f4380 @ 0x4f4380, called
		/// from perform_interturn @ 0x4f5ef0).
		/// </summary>
		public static void DoPerTurnGlobalWarming(GameData gameData) {
			int accumulated = AccumulatedPollution(gameData);
			gameData.globalWarmingSeverity = GlobalWarmingSeverity(gameData);

			int tileCount = gameData.map.tiles.Count;
			if (tileCount <= 0) {
				return;
			}

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

			// Civ3's Map_change_tile_terrain only changes the tile's rule/visible
			// terrain (Tile + 0xC8, OpenCiv3's overlayTerrainType, the field the
			// TERR record is selected by and the yields are computed from). The
			// tile's underlying terrain (Tile + 0xC4, baseTerrainType) is
			// preserved: that is exactly why the 0xE PollutionEffect sentinel
			// still finds the original underlying terrain, which is how clearing
			// a forest or jungle works. So warming a grassland to plains leaves
			// grassland underneath, as Civ3 does - baseTerrainType must not be
			// overwritten here.
			//
			// Civ3 then clears the mine and irrigation bits (0xC). In OpenCiv3's
			// layered model mine and irrigation share the ResourceDevelopment
			// layer, so at most one of them is present.
			tile.overlayTerrainType = target;
			tile.RemoveImprovementAtLayer(TerrainImprovement.Layer.ResourceDevelopment);

			Player human = gameData.players.FirstOrDefault(p => p.id == EngineStorage.uiControllerID)
				?? gameData.GetFirstHumanPlayer();
			if (human == null || !human.tileKnowledge.isTileKnown(tile)) {
				return;
			}

			// Civ3 picks the message by the value m72_Get_Pollution_Effect
			// returned at 0x4f4380, not by the terrain the tile ends up with: the
			// four basic terrains get the plain global-warming message, anything
			// else (a forest or jungle stripped to a terrain past them) its own.
			// **[C]**
			string message = targetId < 4
				? "Global warming has damaged the land!"
				: "Global warming has destroyed the forest!";
			new MsgShowTemporaryPopup(message, tile).send();
		}

		/// <summary>
		/// Resolves the terrain a tile becomes under global warming from its
		/// terrain's TERR.PollutionEffect column, returning null when the tile
		/// is immune. The value the engine's m72_Get_Pollution_Effect returned is
		/// reported through targetId so callers can tell the two warming messages
		/// apart: it is the stored column value, or the tile's underlying terrain
		/// id when that value is the 0xE sentinel. **[C]** (0x4f4380 +
		/// Tile_Get_Pollution_Effect @ 0x5e9a60).
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
