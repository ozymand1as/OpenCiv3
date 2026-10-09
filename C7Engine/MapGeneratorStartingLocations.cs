namespace C7Engine {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using Serilog;
	using C7GameData;
	using C7GameData.Save;

	// Civ3's starting-location scoring and placement, read out of the shipped
	// map generator.
	//
	// The algorithm is `FUN_005eeee0` @ `0x5eeee0`: score every tile with the
	// world object's vtable slot `+0x8` - the wrapper `Map_eval_city_location` @
	// `0x5d3830`, which calls the city-site value function
	// `Match_ai_eval_city_location` @ `0x442480` with its fourth argument (the
	// AI-evaluation flag) set to 1, which the wrapper pushes at `0x5d383a` -
	// then sort the tiles by descending score and walk that list in up to eight
	// passes of progressively relaxed gates. The prose write-up, the address each
	// rule was read from and its confidence tag are in
	// re/notes/civ3_map_generator_spec.md section 2.14.
	public partial class MapGenerator {
		// The score is 1000000 plus small bucket terms (`0x442bc4` loads the
		// constant into the accumulator); the constant dominates, so the
		// "score > 0" gate in practice only reflects the rejection predicates.
		internal const int START_CANDIDATE_BASE = 1000000;

		// Up to eight passes: `0x5ef648` compares the incremented pass counter
		// against 8.
		internal const int START_PASSES = 8;

		// The body-size gate. A body of at least 75 tiles passes on any pass
		// (`0x5ef40b` compares the body's tile count against 0x4b); a body of at
		// least 37 passes from pass 2 (`0x5ef415` tests the pass counter against
		// 2 and `0x5ef425` compares the area against 0x25); the gate is dropped
		// from pass 5 (`0x5ef42b` tests the pass counter against 5).
		internal const int START_BODY_MIN_AREA = 74;
		internal const int START_BODY_RELAXED_AREA = 36;

		// Until pass 4 a candidate must sit on a body of more than 20 tiles
		// (`0x5ef26f` tests the pass counter against 4 and `0x5ef2ae` compares
		// the area against 0x14).
		internal const int START_BODY_MIN_AREA_EARLY = 20;

		// A water body of 20 tiles or fewer counts as fresh water (`0x5f39a4`).
		internal const int START_FRESH_WATER_MAX_AREA = 20;

		// The score gate is dropped from pass 7 (`0x5ef434` tests the pass
		// counter against 7).
		internal const int START_SCORE_GATE_LAST_PASS = 6;

		// The spacing requirement is halved from pass 3 (`0x5ef4ba` tests the
		// pass counter against 3) and waived from pass 6 (`0x5ef5ad` tests it
		// against 6).
		internal const int START_SPACING_HALVED_FROM_PASS = 3;
		internal const int START_SPACING_WAIVED_FROM_PASS = 6;

		// The two "near" radii the score's fresh-water test uses, in Civ3 spiral
		// positions: 0x2d = 45 for a river (`0x443194`) and 0x4d = 77 for a lake
		// (`0x4431a6`).
		internal const int START_NEAR_RIVER_RADIUS = 45;
		internal const int START_NEAR_LAKE_RADIUS = 77;

		// The divisor the score applies to a tile with no fresh water and no
		// river or lake nearby (`0x4431c3`), and the one it applies when there is
		// fresh water or a river/lake within reach (`0x4431d3`).
		internal const int START_DRY_DIVISOR = 512;
		internal const int START_FRESH_WATER_DIVISOR = 256;

		// `FUN_005eeee0` seeds its two shuffles from the world seed. The original
		// uses the generator's own stream; this offset is ours, chosen so the
		// start order is a pure function of the seed and independent of the
		// passes before it.
		private const int START_SEED_OFFSET = 0x1337;

		// ------------------------------------------------------------- the spiral

		// Civ3's neighbour spiral, read out of `neighbor_index_to_diff` @
		// `0x5e6e50`. Index 0 is the tile itself; index 1 starts at the
		// north-east and runs clockwise; from ring 2 on, each ring is walked as
		// four runs of (2r - 1) tiles - north-east, south-east, south-west,
		// north-west - followed by the four axis tiles (north, east, south,
		// west). That ordering is what makes the big fat cross exactly the first
		// twenty-one indices: ring 0, ring 1 and the twelve non-axis tiles of
		// ring 2.
		internal static (int dx, int dy) Civ3SpiralOffset(int index) {
			if (index <= 0) {
				return (0, 0);
			}

			int ring = 1;
			while ((2 * ring + 1) * (2 * ring + 1) <= index) {
				++ring;
			}

			// 1-based position within the ring.
			int k = index - (2 * ring - 1) * (2 * ring - 1) + 1;

			// The four axis tiles close the ring.
			if (k > 8 * ring - 4) {
				if (k == 8 * ring - 3) return (0, -2 * ring);
				if (k == 8 * ring - 2) return (2 * ring, 0);
				if (k == 8 * ring - 1) return (0, 2 * ring);
				return (-2 * ring, 0);
			}

			if (k < 2 * ring) return (k, k - 2 * ring);
			if (k < 4 * ring - 1) return (4 * ring - k - 1, k + 1 - 2 * ring);
			if (k < 6 * ring - 2) return (4 * ring - k - 2, 6 * ring - k - 2);
			return (k + 3 - 8 * ring, 6 * ring - k - 3);
		}

		private static Tile TileAtSpiral(GameMap m, Tile center, int index) {
			(int dx, int dy) = Civ3SpiralOffset(index);
			return m.tileAt(center.XCoordinate + dx, center.YCoordinate + dy);
		}

		private static bool InBounds(Tile t) {
			return t != null && t != Tile.NONE;
		}

		// ------------------------------------------------------- score helpers

		// The area of the body a tile sits on. `FUN_005eeee0` reads it through
		// the world's body accessor at `+0x84` (record field `+0x24`).
		internal static Dictionary<int, int> BodyAreas(GameMap m) {
			Dictionary<int, int> areas = new();
			foreach (HashSet<Tile> body in m.continents) {
				if (body.Count == 0) {
					continue;
				}
				areas[body.First().continent] = body.Count;
			}
			return areas;
		}

		private static int BodyAreaOf(Dictionary<int, int> bodyAreas, Tile t) {
			return bodyAreas.TryGetValue(t.continent, out int area) ? area : 0;
		}

		// ---- the pass gates, split out so each relaxation is testable ----

		// The score gate applies through pass 6 and is dropped from pass 7
		// (`0x5ef434`).
		internal static bool StartScoreGateApplies(int pass) {
			return pass <= START_SCORE_GATE_LAST_PASS;
		}

		// The body-size gate: over 74 tiles on any pass, over 36 from pass 2, and
		// dropped from pass 5 (`0x5ef40b`, `0x5ef415`, `0x5ef425`, `0x5ef42b`).
		internal static bool StartSizeGatePasses(int pass, int bodyArea) {
			return bodyArea > START_BODY_MIN_AREA
				|| (pass >= 2 && bodyArea > START_BODY_RELAXED_AREA)
				|| pass >= 5;
		}

		// The spacing test applies through pass 5 and is waived from pass 6
		// (`0x5ef5ad`).
		internal static bool StartSpacingApplies(int pass) {
			return pass < START_SPACING_WAIVED_FROM_PASS;
		}

		// The minimum spacing the test enforces: the world size's distance, halved
		// from pass 3 (`0x5ef4ba`).
		internal static int StartSpacingRequirement(int minDistance, int pass) {
			return pass >= START_SPACING_HALVED_FROM_PASS ? minDistance / 2 : minDistance;
		}

		// The terrain-rules improvement bonus a terrain carries, read from the
		// rules' terrain improvements. The score reads the same values straight
		// out of the terrain-rules table (terrain record `+0x4c` for irrigation
		// food, `+0x50` for mining shields and `+0x54` for road commerce, at
		// `0x44299c`-`0x442a0c`), so the site is valued as if its tiles were
		// improved. The BIQ import turns TERR.IrrigationBonus/MiningBonus/
		// RoadBonus into exactly these improvement bonuses (ImportCiv3.cs), and
		// C7/Lua/civ3/ruleset.json carries the same values, so the rules are the
		// same on both paths.
		private static int ImprovementBonus(WorldCharacteristics wc, string improvementKey,
				string terrainKey, Tile.YieldType type) {
			if (wc.terrainImprovements == null) {
				return 0;
			}
			foreach (SaveTerrainImprovement improvement in wc.terrainImprovements) {
				if (improvement.key != improvementKey) {
					continue;
				}
				if (improvement.bonusYields != null
					&& improvement.bonusYields.TryGetValue(terrainKey, out Dictionary<Tile.YieldType, int> yields)
					&& yields.TryGetValue(type, out int bonus)) {
					return bonus;
				}
				return 0;
			}
			return 0;
		}

		// The exact terrain a tile carries for the score. Civ3's plot keeps two
		// four-bit terrain fields in the kind-1 word at `plot+0x2c`: bits 12..15
		// (`plot+0xc8`) are the tile's terrain id and bits 8..11 (`plot+0xc4`)
		// are the ground underneath it. `Plot_Set_Square_Type` @ `0x5e9940` is
		// what writes the pair from a single terrain id: it stores the id in bits
		// 12..15 and derives the substrate from it - grassland (2) for a forest,
		// jungle, marsh, hill or mountain, desert (0) for a flood plain, and the
		// id itself for desert, plains, grassland and tundra. Every reader that
		// means "the tile's terrain" reads bits 12..15: is-water @ `0x5eaa30`,
		// the resource candidacy test @ `0x5f33b2` and the game's own
		// food/shield/commerce getters @ `0x5dbd80`, `0x5dbdd0` and `0x5dbe20`.
		// In C7 that field is `overlayTerrainType` - grassland under a forest is
		// `baseTerrainType`, and the fork's own rules (allowed resources, water,
		// hills, and the yields in `Tile_Yield.cs`) all read the overlay. So the
		// score's terrain is the overlay, not the base.
		internal static TerrainType ScoreTerrain(Tile n) {
			return n.overlayTerrainType;
		}

		// `plot+0x8c` @ `0x5eaa30` reads that same terrain field and reports true
		// for the three water terrains 11..13, so "is water" is a test on the
		// effective terrain as well.
		internal static bool IsWaterTerrain(Tile n) {
			return ScoreTerrain(n).isWater();
		}

		// The terrain's food, shields and commerce, plus its irrigation, mining
		// and road bonuses and any resource's bonuses. Civ3 reads the tile's
		// terrain (`plot+0xc8`), then that terrain record's first
		// food/shield/commerce triple (`+0x64`/`+0x68`/`+0x6c`), the resource
		// record's bonuses (`+0x50`/`+0x54`/`+0x58`) and the terrain's second
		// triple (`+0x4c`/`+0x50`/`+0x54`, the irrigation, mining and road
		// bonuses) - in that order, at `0x4429b1`-`0x442b46`.
		internal static (int food, int shields, int commerce) TerrainYields(WorldCharacteristics wc, Tile n) {
			TerrainType terrain = ScoreTerrain(n);
			int food = terrain.baseFoodProduction
				+ ImprovementBonus(wc, Tile.TileOverlays.IRRIGATION, terrain.Key, Tile.YieldType.Food);
			int shields = terrain.baseShieldProduction
				+ ImprovementBonus(wc, Tile.TileOverlays.MINE, terrain.Key, Tile.YieldType.Production);
			int commerce = terrain.baseCommerceProduction
				+ ImprovementBonus(wc, Tile.TileOverlays.ROAD, terrain.Key, Tile.YieldType.Commerce);

			if (HasResource(n)) {
				food += n.Resource.FoodBonus;
				shields += n.Resource.ShieldsBonus;
				commerce += n.Resource.CommerceBonus;
			}

			return (food, shields, commerce);
		}

		internal static bool HasResource(Tile n) {
			return n.Resource != null && n.Resource != Resource.NONE;
		}

		// The terrain record's irrigation bonus - the `+0x4c` field of the tile's
		// terrain. `FUN_00442480` uses this, not the terrain's food, as its
		// "may a city stand here" gate (`0x442c7c` calls the irrigation-bonus
		// getter @ `0x5dbe70` and rejects the tile when the result is zero), and
		// the fresh-water lake pass @ `0x5ed5d0` uses the same getter as its
		// "irrigable ground" test. It is a rules value, so it exists on both the
		// BIQ import path (TERR.IrrigationBonus, ImportCiv3.cs) and in
		// C7/Lua/civ3/ruleset.json.
		internal static int IrrigationBonus(WorldCharacteristics wc, Tile t) {
			return ImprovementBonus(wc, Tile.TileOverlays.IRRIGATION, ScoreTerrain(t).Key, Tile.YieldType.Food);
		}

		// The food, shields and commerce one big-fat-cross tile contributes to the
		// score: its terrain's yields, plus the terrain's improvement bonuses and
		// the tile's resource bonuses, plus the two bonuses the score adds per
		// tile - one food for water (`0x442a0c` adds 1 when `plot+0x8c` is set)
		// and one shield for water on a body of more than twenty tiles
		// (`0x442a44`) - and, for the tile itself, the "a city tile always feeds
		// two" override at `0x442970`. `index` is the tile's position in the Civ3
		// spiral; 0 is the site itself.
		internal static (int food, int shields, int commerce) StartScoreTileYields(
				WorldCharacteristics wc, GameMap m, Dictionary<int, int> bodyAreas, Tile n, int index) {
			(int food, int shields, int commerce) = TerrainYields(wc, n);

			if (IsWaterTerrain(n)) {
				food += 1;
				if (BodyAreaOf(bodyAreas, n) > START_FRESH_WATER_MAX_AREA) {
					shields += 1;
				}
			}

			if (index == 0) {
				food = 2;
			}

			// A bonus grassland shield, for a tile whose terrain is grassland and
			// whose bonus-shield flag is set (`0x442aa8`-`0x442b12`: the terrain id
			// is compared against 2, which is grassland).
			if (ScoreTerrain(n).Key == "grassland" && n.isBonusShield) {
				shields += 1;
			}

			return (food, shields, commerce);
		}

		// `Map_has_fresh_water` @ `0x5f39e0`: a small water body within the
		// three-by-three around the tile, or a river on the tile itself.
		internal static bool HasFreshWater(Tile t) {
			return t.BordersRiver() || t.NeighborsFreshWater();
		}

		// `Map_is_near_river` @ `0x5f37f0`: any tile within the first `radius`
		// spiral positions carries a river.
		internal static bool NearRiver(GameMap m, Tile t, int radius) {
			for (int i = 0; i < radius; ++i) {
				Tile n = TileAtSpiral(m, t, i);
				if (InBounds(n) && n.BordersRiver()) {
					return true;
				}
			}
			return false;
		}

		// `Map_is_near_lake` @ `0x5f38c0`: any tile within the first `radius`
		// spiral positions is water belonging to a body of at most twenty tiles.
		internal static bool NearLake(GameMap m, Dictionary<int, int> bodyAreas, Tile t, int radius) {
			for (int i = 0; i < radius; ++i) {
				Tile n = TileAtSpiral(m, t, i);
				if (InBounds(n) && n.IsWater()
					&& BodyAreaOf(bodyAreas, n) <= START_FRESH_WATER_MAX_AREA) {
					return true;
				}
			}
			return false;
		}

		// ------------------------------------------------- the candidate score

		// The tiles that lie inside an existing city's working radius. Civ3 marks
		// these with kind-2 bit 17 (`0x20000`): the city constructor
		// `FUN_004ae2a0` @ `0x4ae2a0` sets it on the city's own plot and on the
		// twenty tiles of the big fat cross around it (`0x4ae513` sets it inside
		// that loop), and `City_raze` @ `0x4aecc0` clears it again on the tiles
		// around the razed plot that no longer have a city within their own cross
		// (`0x4af06f`) and re-marks the crosses of the cities that survive
		// (`0x4af3cd`). No pass of the generator sets it, so on a generated map
		// this set is empty; it is modelled because the same score function also
		// serves the AI's own city siting.
		internal static HashSet<Tile> CityRadiusTiles(GameMap m) {
			HashSet<Tile> tiles = new();
			foreach (Tile cityTile in m.tiles) {
				if (!cityTile.HasCity()) {
					continue;
				}
				for (int i = 0; i < 21; ++i) {
					Tile n = TileAtSpiral(m, cityTile, i);
					if (InBounds(n)) {
						tiles.Add(n);
					}
				}
			}
			return tiles;
		}

		// The part of `Match_ai_eval_city_location` @ `0x442480` that does not
		// depend on the AI-evaluation flag: the terrain yields summed over the
		// twenty-one big-fat-cross tiles, classified into food/shield/commerce
		// buckets, plus the resource, river and fresh-water terms, over the
		// constant 1000000 (`0x442bc4`). The flag-selected branch then divides this
		// down; see `ScoreStartCandidate`.
		//
		// The term assignment, read off the sum at `0x442ba2`-`0x442c16`, is
		//   food1/3 + 2 * (great - insideACityRadius) + 1000000 + food2/2
		//   - dead + food3NoShield + luxuryCount + resourcePoints - ownedByAnotherCiv
		//   + freshWater + riverTiles + (the AI-evaluation terms)
		// where `insideACityRadius` counts the cross tiles carrying kind-2 bit 17
		// and `food2/2` is a signed division. A tile carrying that bit is skipped
		// outright (`0x4427c6` jumps to the next spiral position), so it also
		// contributes no resource points, no river and no bucket.
		//
		// Three of those terms need their live conditions stated, because the
		// function's frame keeps one slot each and the record previously
		// conflated them (`re/notes/civ3_map_generator_spec.md` section 2.14 has
		// the slot table):
		//  * the `+4` fresh-water term is the one `Map_has_fresh_water` @
		//    `0x5f39e0` sets at `0x4426fc` with no flag test at all, and it is the
		//    only fresh-water term the sum accumulates during generation (the
		//    divisor chain is a separate, later stage);
		//  * `ownedByAnotherCiv` - the cross tiles owned by a civ other than the
		//    evaluating player (`0x442792` counts, `0x442be2` subtracts) - is
		//    counted only when the third argument, the evaluating player, is not
		//    -1; the wrapper passes -1 (`0x5d383c`), so it is zero at generation
		//    time;
		//  * the `+8` and `+4` terms the AI-evaluation path sets (`0x4426e4`,
		//    `0x4426c0`) are live only when the fourth argument, the "AI
		//    evaluation" flag, is 0; the wrapper passes 1 (`0x5d383a`), so both
		//    are zero at generation time.
		internal static int StartScoreBucketSum(WorldCharacteristics wc, GameMap m,
				Dictionary<int, int> bodyAreas, HashSet<Tile> cityRadiusTiles, Tile t) {
			int food1 = 0;            // food == 1, with a shield or a commerce
			int food2 = 0;            // food == 2, with a shield or a commerce
			int food3NoShield = 0;    // food >= 3, no shield, some commerce
			int great = 0;            // food >= 3 and a shield
			int dead = 0;             // food == 0
			int resourcePoints = 0;
			int luxuryCount = 0;
			int riverTiles = 0;
			int insideACityRadius = 0;

			for (int i = 0; i < 21; ++i) {
				Tile n = TileAtSpiral(m, t, i);
				if (!InBounds(n)) {
					continue;
				}
				if (cityRadiusTiles.Contains(n)) {
					insideACityRadius++;
					continue;
				}

				(int food, int shields, int commerce) = StartScoreTileYields(wc, m, bodyAreas, n, i);

				if (n.BordersRiver()) {
					riverTiles++;
				}

				if (HasResource(n)) {
					if (n.Resource.Category == ResourceCategory.BONUS) {
						resourcePoints += 1;
					} else {
						// Four points for a luxury or strategic resource in the
						// inner three-by-three, two in the outer ring
						// (`0x442884`: the spiral index is compared against 9).
						resourcePoints += i < 9 ? 4 : 2;
						if (n.Resource.Category == ResourceCategory.LUXURY) {
							luxuryCount++;
						}
					}
				}

				if (food >= 3) {
					if (shields >= 1) {
						great++;
						continue;
					}
					if (commerce >= 1) {
						food3NoShield++;
						continue;
					}
				}
				if (food >= 2 && (shields >= 1 || commerce >= 1)) {
					food2++;
				} else if (food >= 1 && (shields >= 1 || commerce >= 1)) {
					food1++;
				} else if (food == 0) {
					dead++;
				}
			}

			return START_CANDIDATE_BASE
				+ food1 / 3
				+ 2 * (great - insideACityRadius)
				+ food2 / 2
				- dead
				+ food3NoShield
				+ luxuryCount
				+ resourcePoints
				+ riverTiles
				+ (HasFreshWater(t) ? 4 : 0);
		}

		// The pre-pass `Match_ai_eval_city_location` runs over the first
		// forty-nine spiral positions before it sums anything (`0x4424f7`-
		// `0x442653`). It rejects:
		//  * a tile that already carries a CITY within the first twenty-five
		//    spiral positions (`0x44258c` calls `Tile_has_city` @ `0x5ea6c0`,
		//    which forwards to the plot's city-id getter `plot+0xb4` @ `0x5eaae0`,
		//    the short at `+0x1a`; `0x442599` compares the spiral index against
		//    twenty-five). It is NOT the owner byte: the owner byte is the
		//    separate field at `+0x05` that the outer-ring rule below reads;
		//  * more than three tiles owned by a civ among positions nine to twenty,
		//    the outer ring of the cross less its axis tiles (`0x4425a7` skips
		//    `i < 9`, `0x4425cc` reads the owner byte through `plot+0x98` @
		//    `0x5eaa80`, `0x4425df` counts and `0x44262d` compares against four);
		//  * more than nine tiles inside a city's working radius anywhere in the
		//    cross (`0x442603`-`0x442610` counts, `0x442640` compares against
		//    ten).
		// The first of the three is what makes the second reachable at all: while
		// the `i < 25` screen was keyed on ownership, every owned tile tripped it
		// first and the outer-ring count could never be the reason a candidate was
		// rejected.
		//
		// The function also rejects, before the loop, a candidate whose own tile is
		// owned by a different civ (`0x4424b2`-`0x4424f4`); that test is skipped
		// whenever the third argument, the evaluating player, is -1, which is what
		// the map generator passes (`0x5d383c`), so it is not modelled here.
		//
		// During generation no tile carries a city, an owner or the city-radius
		// mark, so no candidate trips any of the three; they are modelled because
		// the same function serves the AI's own site evaluation.
		internal static bool StartPrePassRejected(GameMap m, HashSet<Tile> cityRadiusTiles, Tile t) {
			int ownedInTheOuterRing = 0;
			int insideACityRadius = 0;
			for (int i = 0; i < 49; ++i) {
				Tile n = TileAtSpiral(m, t, i);
				if (!InBounds(n)) {
					continue;
				}
				if (n.HasCity() && i < 25) {
					return true;
				}
				if (i >= 9 && i < 21 && IsOwned(n)) {
					ownedInTheOuterRing++;
				}
				if (i < 21 && cityRadiusTiles.Contains(n)) {
					insideACityRadius++;
				}
			}
			return ownedInTheOuterRing > 3 || insideACityRadius > 9;
		}

		// A tile that belongs to a civ. Civ3 stores the owning civ's id in the
		// plot's first flag byte (`plot+0x05`, read by `plot+0x98` @ `0x5eaa80`),
		// zero meaning unclaimed; C7's equivalent is the city whose border
		// contains the tile.
		private static bool IsOwned(Tile n) {
			return n.owningCity != null;
		}

		// `Map_check_city_location` @ `0x5f3160` plus the two extra rejection
		// gates the score applies to a candidate before it divides anything: the
		// tile must allow cities, its terrain's irrigation bonus must be non-zero
		// (`0x442c7c` calls the irrigation-bonus getter @ `0x5dbe70` and rejects
		// the tile when it returns zero), and at least one of its eight
		// neighbours must be able to feed more than one citizen (`0x443146`).
		// A neighbour's food is its terrain's food, plus one for water on a body
		// of twenty tiles or fewer, plus the food bonus of a resource with no
		// prerequisite.
		internal static bool StartSiteRejected(WorldCharacteristics wc, GameMap m,
				Dictionary<int, int> bodyAreas, Tile t) {
			if (!InBounds(t) || !t.IsAllowCities()) {
				return true;
			}
			if (IrrigationBonus(wc, t) == 0) {
				return true;
			}

			for (int i = 1; i <= 8; ++i) {
				Tile n = TileAtSpiral(m, t, i);
				if (!InBounds(n)) {
					continue;
				}
				int neighbourFood = ScoreTerrain(n).baseFoodProduction;
				if (IsWaterTerrain(n) && BodyAreaOf(bodyAreas, n) <= START_FRESH_WATER_MAX_AREA) {
					neighbourFood += 1;
				}
				if (HasResource(n) && n.Resource.Prerequisite == null) {
					neighbourFood += n.Resource.FoodBonus;
				}
				if (neighbourFood > 1) {
					return false;
				}
			}
			return true;
		}

		// The full candidate score: `Match_ai_eval_city_location` @ `0x442480`
		// with the AI-evaluation flag set, i.e. the value `FUN_005eeee0` sorts its
		// candidates by. Returns 0 for a rejected tile, otherwise at least 1.
		//
		// There is no flat "river bonus" and no flat "coast bonus"; what the
		// function does have is a fresh-water divisor (a tile with fresh water, or
		// a river or lake within reach, keeps twice as much of its score as a dry
		// one), a river-tile term in the sum, and a food/terrain penalty chain.
		internal static int ScoreStartCandidate(WorldCharacteristics wc, GameMap m,
				Dictionary<int, int> bodyAreas, HashSet<Tile> cityRadiusTiles, Tile t) {
			if (!InBounds(t) || StartPrePassRejected(m, cityRadiusTiles, t)) {
				return 0;
			}
			if (StartSiteRejected(wc, m, bodyAreas, t)) {
				return 0;
			}

			int score = StartScoreBucketSum(wc, m, bodyAreas, cityRadiusTiles, t);

			// Divide by one plus the number of water tiles among the eight
			// neighbours (`0x442d51` divides by that count).
			int waterNeighbours = 0;
			for (int i = 1; i <= 8; ++i) {
				Tile n = TileAtSpiral(m, t, i);
				if (InBounds(n) && IsWaterTerrain(n)) {
					waterNeighbours++;
				}
			}
			score /= 1 + waterNeighbours;

			// A luxury within the first forty-five spiral positions, or the score
			// is divided by sixteen (`0x442e21`).
			bool luxuryNearby = false;
			for (int i = 0; i < 45 && !luxuryNearby; ++i) {
				Tile n = TileAtSpiral(m, t, i);
				luxuryNearby = InBounds(n) && HasResource(n)
					&& n.Resource.Category == ResourceCategory.LUXURY;
			}
			if (!luxuryNearby) {
				score /= 16;
			}

			// Jungle around the site is a penalty: divide by
			// (jungle neighbours * 32 / 20 + 1) (`0x442ef2`).
			int jungleNeighbours = 0;
			for (int i = 1; i <= 20; ++i) {
				Tile n = TileAtSpiral(m, t, i);
				if (InBounds(n) && ScoreTerrain(n).Key == "jungle") {
					jungleNeighbours++;
				}
			}
			score /= jungleNeighbours * 32 / 20 + 1;

			// The eight neighbours: how many can feed more than one citizen, and
			// how many count as "improved".
			int foodRichNeighbours = 0;
			int improvedNeighbours = 0;
			for (int i = 1; i <= 8; ++i) {
				Tile n = TileAtSpiral(m, t, i);
				if (!InBounds(n)) {
					continue;
				}

				int neighbourFood = ScoreTerrain(n).baseFoodProduction;
				if (IsWaterTerrain(n) && BodyAreaOf(bodyAreas, n) <= START_FRESH_WATER_MAX_AREA) {
					neighbourFood += 1;
				}
				if (HasResource(n) && n.Resource.Prerequisite == null) {
					neighbourFood += n.Resource.FoodBonus;
				}
				if (neighbourFood <= 1) {
					continue;
				}
				foodRichNeighbours++;

				bool improved = ScoreTerrain(n).baseShieldProduction >= 1;
				if (!improved && ScoreTerrain(n).Key == "grassland" && n.isBonusShield) {
					improved = true;
				}
				if (!improved && HasResource(n) && n.Resource.Prerequisite == null
					&& n.Resource.ShieldsBonus >= 1) {
					improved = true;
				}
				if (improved) {
					improvedNeighbours++;
				}
			}

			if (foodRichNeighbours == 1) {
				score /= 64;
			}
			if (improvedNeighbours < 2) {
				score /= 128;
			}

			// The fresh-water divisor. A tile with fresh water on or beside it
			// keeps the score it has; a dry tile that has a river within 45
			// spiral positions or a lake within 77 keeps twice as much as a dry
			// tile with neither.
			if (!HasFreshWater(t)) {
				if (NearRiver(m, t, START_NEAR_RIVER_RADIUS)
					|| NearLake(m, bodyAreas, t, START_NEAR_LAKE_RADIUS)) {
					score /= START_FRESH_WATER_DIVISOR;
				} else {
					score /= START_DRY_DIVISOR;
				}
			}

			// Terrain penalties for the site itself.
			if (ScoreTerrain(t).Key == "jungle") {
				score /= 1024;
			}
			if (ScoreTerrain(t).Key == "desert") {
				score /= 2048;
			}
			if (ScoreTerrain(t).Key == "tundra") {
				score /= 4096;
			}

			// The function returns 1 rather than 0 for a valid site whose score
			// has been divided away, so that the "score > 0" gate only rejects
			// tiles the predicates rejected (`0x44328c` onward).
			return score > 0 ? score : 1;
		}

		// ------------------------------------------------------- the search

		private static void DetermineStartingLocations(WorldCharacteristics wc, GameMap m) {
			m.startingLocations = PlaceStartingLocations(wc, m, out _);
		}

		// `FUN_005eeee0` @ `0x5eeee0`: the start placement itself. The original
		// clears the start array first (it can run over a partly-populated
		// scenario map), scores and sorts every tile, then runs up to eight
		// passes with progressively relaxed gates. `passOfStart[i]` records the
		// pass that produced start i.
		internal static List<Tile> PlaceStartingLocations(WorldCharacteristics wc, GameMap m,
				out int[] passOfStart) {
			List<Tile> startingLocations = new();
			passOfStart = Array.Empty<int>();

			int wanted = wc.worldSize.numberOfCivs;
			if (wanted <= 0) {
				return startingLocations;
			}
			int[] passes = new int[wanted];

			// Score every tile, then sort by descending score. The tiles inside an
			// existing city's working radius, and the order the candidates are tried
			// in: the original fills an index array with `0..n-1` and then, for each
			// position from the front, swaps it with `i + rand_int(n - i)`
			// (`0x5ef0ff`-`0x5ef14b`) - a forward Fisher-Yates, which is what
			// `Civ3StartRandom` reproduces. Before that it draws thirty-two times
			// while it clears the thirty-two start slots (`0x5ef06d`-`0x5ef082`), so
			// the port draws the same number. The score array is indexed by the
			// shuffled order, so ties in the (stable) sort keep that order.
			Dictionary<int, int> bodyAreas = BodyAreas(m);
			HashSet<Tile> cityRadiusTiles = CityRadiusTiles(m);

			Civ3StartRandom rand = new(wc.mapSeed + START_SEED_OFFSET);
			for (int i = 0; i < 32; ++i) {
				rand.NextFloat();
			}

			List<Tile> shuffled = new(m.tiles);
			for (int i = 0; i < shuffled.Count; ++i) {
				int j = i + rand.Next(shuffled.Count - i);
				(shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
			}

			Dictionary<Tile, int> scores = new(shuffled.Count);
			foreach (Tile t in shuffled) {
				scores[t] = ScoreStartCandidate(wc, m, bodyAreas, cityRadiusTiles, t);
			}
			// The original sorts the shuffled (tile, score) pairs by descending
			// score (`0x5cdcd0`); a stable sort keeps the shuffled order for equal
			// scores, which is what the shuffle is for.
			List<Tile> ordered = shuffled.OrderByDescending(t => scores[t]).ToList();

			// The per-body counters: `FUN_005eeee0` counts the luxury resources
			// on each body and, as starts are accepted, the starts on each body.
			Dictionary<int, int> luxuriesPerBody = new();
			foreach (Tile t in m.tiles) {
				if (HasResource(t) && t.Resource.Category == ResourceCategory.LUXURY) {
					luxuriesPerBody[t.continent] = luxuriesPerBody.GetValueOrDefault(t.continent) + 1;
				}
			}
			int totalLuxuries = luxuriesPerBody.Values.Sum();
			Dictionary<int, int> startsPerBody = new();

			int minDistance = wc.worldSize.distanceBetweenCivs;

			for (int pass = 0; pass < START_PASSES && startingLocations.Count < wanted; ++pass) {
				foreach (Tile t in ordered) {
					if (startingLocations.Count >= wanted) {
						break;
					}

					int body = t.continent;
					int bodyArea = BodyAreaOf(bodyAreas, t);

					// The outer gate: the tile must not be in the top or bottom
					// pole row, and until pass 4 it must sit on a body of more
					// than twenty tiles.
					bool poleRowOk = t.YCoordinate != 0 && t.YCoordinate < m.numTilesTall - 1;
					bool bodyEarlyOk = pass >= 4 || bodyArea > START_BODY_MIN_AREA_EARLY;
					if (!poleRowOk || !bodyEarlyOk) {
						continue;
					}

					// The seven acceptance screens (`0x5ef2ee`-`0x5ef3b1`).
					if (StartScreenRejects(t, startingLocations)) {
						continue;
					}

					// The per-body counter condition (`0x5ef3d1`-`0x5ef3fa`):
					// from pass 1 on it is satisfied, and on pass 0 it is
					// satisfied when there are more starts than luxuries in the
					// world or when this body has more luxuries than starts.
					bool countersOk = pass >= 1
						|| startingLocations.Count > totalLuxuries
						|| luxuriesPerBody.GetValueOrDefault(body) > startsPerBody.GetValueOrDefault(body);
					if (!countersOk) {
						continue;
					}

					// The region/size test on the body.
					if (!StartSizeGatePasses(pass, bodyArea)) {
						continue;
					}

					// The score gate, dropped from pass 7.
					if (StartScoreGateApplies(pass) && scores[t] <= 0) {
						continue;
					}

					// The minimum spacing test: against every already-placed
					// start on the same body, the wrapped distance must exceed
					// the world size's minimum. The requirement is halved from
					// pass 3 and waived from pass 6.
					if (StartSpacingApplies(pass)
						&& TooCloseToAnotherStart(t, startingLocations,
							StartSpacingRequirement(minDistance, pass))) {
						continue;
					}

					// Accept: write the tile, and bump the per-body counters.
					passes[startingLocations.Count] = pass;
					startingLocations.Add(t);
					startsPerBody[body] = startsPerBody.GetValueOrDefault(body) + 1;
					log.Information($"Placed start number {startingLocations.Count} on pass {pass}");
				}
			}

			if (wanted > startingLocations.Count) {
				log.Error("More civs than available starting locations.");
			}

			// The original's final reshuffle (`0x5ef655`-`0x5ef6de`), which the
			// driver always asks for (it passes 1 for the flag the block tests).
			ReshuffleStartOrder(rand, startingLocations);

			passOfStart = passes[..startingLocations.Count];
			return startingLocations;
		}

		// The seven acceptance screens `FUN_005eeee0` runs on a candidate before
		// its body-size, per-body counter, score and spacing gates
		// (`0x5ef2ee`-`0x5ef3b1`), in the binary's order. Each tests something on
		// the plot that has to come back empty:
		//  * `plot+0x80` (`0x5ea9c0`), kind-2 bit 19: this tile is already a
		//    starting location;
		//  * `plot+0x8c` (`0x5eaa30`): the tile's terrain is water, so the site
		//    must be land;
		//  * `plot+0x3c(0)` (`0x5ea7a0`), kind-0 bit 5: the GOODY HUT. The sparse
		//    pass `FUN_005f21b0` @ `0x5f21b0` sets that bit, and Civ3 runs the pass
		//    *before* the starts (driver `0x5eb773` against `0x5eb7b0`), so a Civ3
		//    start is never on a hut. The fork's generator places its starts
		//    before that pass, so the screen cannot fire on a generated map here -
		//    a pipeline difference the spec records;
		//  * `plot+0xb4` (`0x5ea6c0`, `Tile_has_city`): the plot's CITY id - the
		//    short at `+0x1a` - which must be -1;
		//  * `plot+0xbc` (`0x5ea6e0`): the plot's COLONY id - the short at `+0x1c` -
		//    which must be -1 unless one of kind-0 bits 29/30/31 is set. C7 has no
		//    colony model, so this screen is modelled as always clear and that is
		//    recorded as an open item;
		//  * `plot+0x0c` (`0x5ea9f0`): the unit id, which must be -1;
		//  * `plot+0x1c(0)` (`0x5ea630`), kind-0 bit 7: the BARBARIAN CAMP. The
		//    camp placement `FUN_0055f9f0` sets that bit (`0x55fbe4`, kind 0, bit
		//    `0x80`) and the sparse pass sets it too; C7 models it as
		//    `hasBarbarianCamp`. Like the hut screen it cannot fire on a generated
		//    map here, because the fork places its camps after the starts.
		internal static bool StartScreenRejects(Tile t, List<Tile> startingLocations) {
			if (startingLocations.Contains(t)) {
				return true;
			}
			if (IsWaterTerrain(t)) {
				return true;
			}
			if (t.hasGoodyHut) {
				return true;
			}
			if (t.HasCity()) {
				return true;
			}
			if (t.unitsOnTile.Count > 0) {
				return true;
			}
			if (t.hasBarbarianCamp) {
				return true;
			}
			return false;
		}

		// The original's final reshuffle (`0x5ef655`-`0x5ef6de`). Its start array
		// is one-based: the generator never writes slot 0 (its next-free-slot scan
		// starts at slot 1), so the count `n` is initialised to 1 (`0x5ef05c`) and
		// bumped once per accepted start (`0x5ef607`), and the first accepted tile
		// is written to slot 1 (`0x5ef5c4`, into the slot the scan at
		// `0x5ef1f4`-`0x5ef217` seeds to 1). The loop therefore walks slots 1 to
		// `n-1` - i.e. every start - swapping each with itself or with a later
		// slot, and only the unused slot 0 is fixed. At the very first step it
		// does not draw at all but swaps slot 1 with the fixed slot
		// `(2n - 2) / 3 + 1`, which is what keeps the first start off the single
		// best site; the drawn steps continue the same stream the candidate
		// shuffle used.
		//
		// `startingLocations` is zero-based, so slot `s` is `startingLocations[s - 1]`
		// and the loop variable `i` is the binary's slot number. The old port used
		// `n = Count` and stopped at `i < n`, which dropped the last start from the
		// walk and used the off-by-one fixed slot `(2 * Count - 2) / 3`.
		internal static void ReshuffleStartOrder(Civ3StartRandom rand, List<Tile> startingLocations) {
			int n = startingLocations.Count + 1;
			for (int i = 1; i < n; ++i) {
				int j = i == 1 ? (2 * n - 2) / 3 + 1 : i + rand.Next(n - i);
				if (j != i) {
					(startingLocations[i - 1], startingLocations[j - 1]) =
						(startingLocations[j - 1], startingLocations[i - 1]);
				}
			}
		}

		// The spacing test. The original compares the wrapped distance with its
		// offset term (`0x5ef4dc`-`0x5ef57a`) against the minimum; that distance
		// is exactly the rank distance the game uses elsewhere, which the fork
		// already has as `Tile.RankDistanceTo`.
		internal static bool TooCloseToAnotherStart(Tile t, List<Tile> startingLocations, int minDistance) {
			foreach (Tile start in startingLocations) {
				if (start.continent == t.continent && start.RankDistanceTo(t) <= minDistance) {
					return true;
				}
			}
			return false;
		}
	}
}
