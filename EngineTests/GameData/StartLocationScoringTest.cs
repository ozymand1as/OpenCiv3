using System;
using System.Collections.Generic;
using System.Linq;
using C7Engine;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using Xunit;

namespace EngineTests.GameData;

// Civ3's starting-location scoring and placement, as read out of
// `FUN_005eeee0` @ `0x5eeee0` and the city-site value function
// `Match_ai_eval_city_location` @ `0x442480` it sorts its candidates by. The
// rules, the addresses they were read from and what was verified against the
// binary are written up in re/notes/civ3_map_generator_spec.md section 2.14.
//
// The headline correction this file pins down: the original score has no flat
// river bonus (the fork used to add RiverPoints = 35) and no flat coast bonus
// (CoastPoints = 30). What it has instead is a fresh-water DIVISOR: a site with
// fresh water on or beside it, or a river within 45 spiral positions or a lake
// within 77, is not divided at all or divided by 256, while a dry site far from
// any is divided by 512. So Civ3's fresh-water preference is roughly a factor of
// 512, not the factor of ~1.03 the old flat bonus produced.
public class StartLocationScoringTest : IClassFixture<SaveGameFixture> {
	private readonly SaveGameFixture saveGameFixture;

	public StartLocationScoringTest(SaveGameFixture fix) {
		saveGameFixture = fix;
	}

	// ------------------------------------------------------------------ terrain

	private static TerrainType Grassland() {
		return new() { Key = "grassland", baseFoodProduction = 2, movementCost = 1 };
	}

	private static TerrainType Plains() {
		return new() { Key = "plains", baseFoodProduction = 1, baseShieldProduction = 1, movementCost = 1 };
	}

	private static TerrainType Desert() {
		return new() { Key = "desert", baseFoodProduction = 0, baseShieldProduction = 1, movementCost = 1 };
	}

	private static TerrainType Tundra() {
		return new() { Key = "tundra", baseFoodProduction = 1, movementCost = 1 };
	}

	private static TerrainType Hills() {
		return new() { Key = "hills", baseFoodProduction = 1, baseShieldProduction = 1, movementCost = 2 };
	}

	private static TerrainType Mountains() {
		return new() { Key = "mountains", baseFoodProduction = 0, baseShieldProduction = 1, movementCost = 3, allowCities = false };
	}

	private static TerrainType Forest() {
		return new() { Key = "forest", baseFoodProduction = 1, baseShieldProduction = 2, movementCost = 1 };
	}

	private static TerrainType Coast() {
		return new() { Key = "coast", baseFoodProduction = 1, baseCommerceProduction = 2, movementCost = 1, allowCities = false };
	}

	private static TerrainType Ocean() {
		return new() { Key = "ocean", baseFoodProduction = 0, movementCost = 1, allowCities = false };
	}

	// --------------------------------------------------------------------- maps

	// Builds a map with the tile ordering the generator uses, so that
	// GameMap.tileAt resolves coordinates the same way it does on a generated
	// map.
	private static GameMap MakeMap(int width, int height, Func<int, int, TerrainType> terrainAt) {
		GameMap m = new GameMap() {
			tiles = new List<Tile>(),
			numTilesWide = width,
			numTilesTall = height,
		};

		ID.Factory factory = new();

		for (int y = 0; y < height; ++y) {
			for (int x = y % 2; x < width; x += 2) {
				Tile t = new Tile(factory.CreateID("tile"));
				t.XCoordinate = x;
				t.YCoordinate = y;
				t.baseTerrainType = terrainAt(x, y);
				t.overlayTerrainType = t.baseTerrainType;
				m.tiles.Add(t);
			}
		}

		m.computeNeighbors();
		m.recomputeContinents();
		return m;
	}

	private static Tile At(GameMap m, int x, int y) {
		Tile t = m.tileAt(x, y);
		Assert.True(t != Tile.NONE, $"no tile at ({x},{y})");
		return t;
	}

	private static WorldCharacteristics MakeWc(int width, int height, int numberOfCivs, int distanceBetweenCivs,
			int seed = 1234) {
		return new WorldCharacteristics() {
			landform = WorldCharacteristics.Landform.Pangaea,
			oceanCoverage = WorldCharacteristics.OceanCoverage.Percent_70,
			age = WorldCharacteristics.Age.Billion_4,
			climate = WorldCharacteristics.Climate.Normal,
			temperature = WorldCharacteristics.Temperature.Temperate,
			worldSize = new WorldSize() {
				width = width,
				height = height,
				numberOfCivs = numberOfCivs,
				distanceBetweenCivs = distanceBetweenCivs,
			},
			terrainTypes = new List<TerrainType>() { Grassland(), Plains(), Desert(), Tundra(), Hills(), Mountains(), Forest(), Coast(), Ocean() },
			mapSeed = seed,
		};
	}

	// --------------------------------------------------------------- the spiral

	// The first twenty-one positions of Civ3's neighbour spiral are exactly the
	// big fat cross: the tile, its eight neighbours and the twelve non-axis
	// tiles of the second ring. The four axis tiles of the second ring follow
	// them, which is what makes a single "index < 21" bound the cross.
	[Fact]
	public void TheSpiralIsTheCiv3BigFatCross() {
		HashSet<(int, int)> cross = new();
		for (int i = 0; i < 21; ++i) {
			cross.Add(MapGenerator.Civ3SpiralOffset(i));
		}
		Assert.Equal(21, cross.Count);

		Assert.Contains((0, 0), cross);
		foreach ((int, int) d in new (int, int)[] {
			(1, -1), (2, 0), (1, 1), (0, 2), (-1, 1), (-2, 0), (-1, -1), (0, -2) }) {
			Assert.Contains(d, cross);
		}
		foreach ((int, int) d in new (int, int)[] {
			(1, -3), (2, -2), (3, -1), (3, 1), (2, 2), (1, 3),
			(-1, 3), (-2, 2), (-3, 1), (-3, -1), (-2, -2), (-1, -3) }) {
			Assert.Contains(d, cross);
		}

		// The four axis tiles of ring 2 are not in the cross, and they are the
		// next four positions.
		HashSet<(int, int)> afterTheCross = new();
		for (int i = 21; i < 25; ++i) {
			afterTheCross.Add(MapGenerator.Civ3SpiralOffset(i));
		}
		Assert.Equal(new HashSet<(int, int)>() { (0, -4), (4, 0), (0, 4), (-4, 0) }, afterTheCross);
	}

	// --------------------------------------------------------------- the score

	// The sum over the cross reads the BASE terrain of each tile, not the
	// overlay: putting a forest on a grassland tile in the cross does not change
	// the score at all. If the code read overlayTerrainType the two maps would
	// differ.
	[Fact]
	public void TheCrossSumCountsBaseTerrainNotTheOverlay() {
		WorldCharacteristics wc = MakeWc(60, 8, 2, 0);

		GameMap plain = MakeMap(60, 8, (x, y) => Grassland());
		GameMap forested = MakeMap(60, 8, (x, y) => Grassland());
		At(forested, 29, 3).overlayTerrainType = Forest();

		Tile centre = At(plain, 30, 4);
		Tile centreForested = At(forested, 30, 4);

		Assert.Equal(
			MapGenerator.StartScoreBucketSum(wc, plain, MapGenerator.BodyAreas(plain), centre),
			MapGenerator.StartScoreBucketSum(wc, forested, MapGenerator.BodyAreas(forested), centreForested));
	}

	// A water tile in the cross feeds one extra food, and one extra shield when
	// its body is bigger than twenty tiles - a one-tile or small lake gives the
	// food but not the shield.
	[Fact]
	public void WaterTilesGetOneFoodAndOneShieldOnABigBody() {
		WorldCharacteristics wc = MakeWc(60, 8, 2, 0);

		// 60 x 8 storage rows hold 240 tiles, so this is one big water body.
		GameMap big = MakeMap(60, 8, (x, y) => Coast());
		Tile bigWater = At(big, 30, 4);
		(int food, int shields, int commerce) bigYields =
			MapGenerator.StartScoreTileYields(wc, big, MapGenerator.BodyAreas(big), bigWater, 9);
		Assert.Equal(1 + 1, bigYields.food);
		Assert.Equal(0 + 1, bigYields.shields);
		Assert.Equal(2, bigYields.commerce);

		// 10 x 4 holds exactly twenty tiles, which is not "bigger than twenty".
		GameMap small = MakeMap(10, 4, (x, y) => Coast());
		Tile smallWater = At(small, 4, 2);
		(int food, int shields, int commerce) smallYields =
			MapGenerator.StartScoreTileYields(wc, small, MapGenerator.BodyAreas(small), smallWater, 9);
		Assert.Equal(1 + 1, smallYields.food);
		Assert.Equal(0, smallYields.shields);
		Assert.Equal(2, smallYields.commerce);
	}

	// The tile the city would stand on always feeds two, whatever its terrain.
	[Fact]
	public void TheSiteTileAlwaysFeedsTwo() {
		WorldCharacteristics wc = MakeWc(60, 8, 2, 0);
		GameMap m = MakeMap(60, 8, (x, y) => x == 30 && y == 4 ? Plains() : Grassland());
		Tile centre = At(m, 30, 4);

		(int food, int shields, int commerce) asSite =
			MapGenerator.StartScoreTileYields(wc, m, MapGenerator.BodyAreas(m), centre, 0);
		Assert.Equal(2, asSite.food);

		(int food, int shields, int commerce) asNeighbour =
			MapGenerator.StartScoreTileYields(wc, m, MapGenerator.BodyAreas(m), centre, 5);
		Assert.Equal(1, asNeighbour.food);
	}

	// A luxury resource adds its own value to the cross sum: four resource points
	// for a tile in the inner three-by-three and two for one in the outer ring,
	// plus one to the luxury count. This resource carries no yields, so nothing
	// else in the sum moves.
	[Fact]
	public void LuxuryResourcesAddTheirValueToTheCrossSum() {
		WorldCharacteristics wc = MakeWc(60, 8, 2, 0);

		GameMap plain = MakeMap(60, 8, (x, y) => Grassland());
		GameMap withOuterLuxury = MakeMap(60, 8, (x, y) => Grassland());
		GameMap withInnerLuxury = MakeMap(60, 8, (x, y) => Grassland());
		Resource luxury = new() {
			Key = "test luxury",
			Name = "test luxury",
			Category = ResourceCategory.LUXURY,
		};
		// (32,2) is two tiles north-east of the site, which is spiral index 10 and
		// so in the outer ring; (29,3) is adjacent, index 7 and in the inner one.
		At(withOuterLuxury, 32, 2).Resource = luxury;
		At(withInnerLuxury, 29, 3).Resource = luxury;

		int without = MapGenerator.StartScoreBucketSum(wc, plain, MapGenerator.BodyAreas(plain), At(plain, 30, 4));
		int outer = MapGenerator.StartScoreBucketSum(wc, withOuterLuxury, MapGenerator.BodyAreas(withOuterLuxury), At(withOuterLuxury, 30, 4));
		int inner = MapGenerator.StartScoreBucketSum(wc, withInnerLuxury, MapGenerator.BodyAreas(withInnerLuxury), At(withInnerLuxury, 30, 4));

		Assert.Equal(2 + 1, outer - without);
		Assert.Equal(4 + 1, inner - without);
	}

	// A rejected tile scores zero. Desert produces no food at all, mountains do
	// not allow cities, and a site whose neighbours cannot feed more than one
	// citizen each is rejected even though it is legal ground.
	[Fact]
	public void RejectedTilesScoreZero() {
		WorldCharacteristics wc = MakeWc(60, 8, 2, 0);

		GameMap desert = MakeMap(60, 8, (x, y) => Desert());
		Tile desertTile = At(desert, 30, 4);
		Assert.True(MapGenerator.StartSiteRejected(wc, desert, MapGenerator.BodyAreas(desert), desertTile));
		Assert.Equal(0, MapGenerator.ScoreStartCandidate(wc, desert, MapGenerator.BodyAreas(desert), desertTile));

		GameMap mountains = MakeMap(60, 8, (x, y) => Mountains());
		Tile mountainTile = At(mountains, 30, 4);
		Assert.Equal(0, MapGenerator.ScoreStartCandidate(wc, mountains, MapGenerator.BodyAreas(mountains), mountainTile));

		// Tundra produces one food, so it passes the food gate, but it cannot
		// feed a second citizen and so no neighbour qualifies.
		GameMap tundra = MakeMap(60, 8, (x, y) => Tundra());
		Tile tundraTile = At(tundra, 30, 4);
		Assert.Equal(1, tundraTile.baseTerrainType.baseFoodProduction);
		Assert.True(MapGenerator.StartSiteRejected(wc, tundra, MapGenerator.BodyAreas(tundra), tundraTile));
		Assert.Equal(0, MapGenerator.ScoreStartCandidate(wc, tundra, MapGenerator.BodyAreas(tundra), tundraTile));

		// The control: grassland is a valid site with a positive score.
		GameMap grass = MakeMap(60, 8, (x, y) => Grassland());
		Assert.True(MapGenerator.ScoreStartCandidate(wc, grass, MapGenerator.BodyAreas(grass), At(grass, 30, 4)) > 0);
	}

	// THE HEADLINE. Two sites with identical surroundings score identically;
	// giving one of them a river edge makes it a fresh-water site, which the
	// score does not divide down at all, while the dry one is divided by 512.
	// The old fork added a flat 35 points instead, which is why this test is
	// written to fail against a flat bonus: a flat bonus would leave the two
	// scores within a few percent of each other.
	[Fact]
	public void ARiverSiteIsNotGivenAFlatBonusItIsAWholeDivisor() {
		WorldCharacteristics wc = MakeWc(200, 8, 2, 0);
		GameMap m = MakeMap(200, 8, (x, y) => Grassland());
		Dictionary<int, int> areas = MapGenerator.BodyAreas(m);

		Tile dry = At(m, 150, 4);
		Tile river = At(m, 10, 4);

		int dryBefore = MapGenerator.ScoreStartCandidate(wc, m, areas, dry);
		int riverBefore = MapGenerator.ScoreStartCandidate(wc, m, areas, river);
		Assert.Equal(dryBefore, riverBefore);

		// A river on the site (an edge of the tile) makes it fresh water.
		river.riverNorth = true;
		Assert.True(river.BordersRiver());
		Assert.True(MapGenerator.HasFreshWater(river));

		int dryScore = MapGenerator.ScoreStartCandidate(wc, m, areas, dry);
		int riverScore = MapGenerator.ScoreStartCandidate(wc, m, areas, river);

		Assert.True(riverScore > dryScore, $"a river site ({riverScore}) should outscore an equivalent dry one ({dryScore})");
		Assert.True(riverScore > 100 * dryScore,
			$"the fresh-water preference is a divisor, so the river site ({riverScore}) should dwarf the dry one ({dryScore})");
		// A flat additive bonus would leave the difference a tiny fraction of the
		// score; the divisor makes the difference comparable to the score itself.
		Assert.True(riverScore - dryScore > riverScore / 2,
			"the river preference should move most of the score, not add a flat handful of points");
	}

	// ------------------------------------------------------- the pass gates

	[Fact]
	public void TheScoreGateIsDroppedFromPassSeven() {
		Assert.True(MapGenerator.StartScoreGateApplies(0));
		Assert.True(MapGenerator.StartScoreGateApplies(6));
		Assert.False(MapGenerator.StartScoreGateApplies(7));
	}

	[Fact]
	public void TheSizeGateRelaxesOnTheSpecsPasses() {
		// Over 74 tiles passes any time.
		Assert.True(MapGenerator.StartSizeGatePasses(0, 75));
		Assert.False(MapGenerator.StartSizeGatePasses(0, 74));
		// Over 36 from pass 2.
		Assert.False(MapGenerator.StartSizeGatePasses(1, 37));
		Assert.True(MapGenerator.StartSizeGatePasses(2, 37));
		Assert.False(MapGenerator.StartSizeGatePasses(2, 36));
		// Dropped from pass 5.
		Assert.False(MapGenerator.StartSizeGatePasses(4, 1));
		Assert.True(MapGenerator.StartSizeGatePasses(5, 1));
	}

	[Fact]
	public void TheSpacingRequirementHalvesFromPassThreeAndIsWaivedFromPassSix() {
		Assert.Equal(12, MapGenerator.StartSpacingRequirement(12, 0));
		Assert.Equal(12, MapGenerator.StartSpacingRequirement(12, 2));
		Assert.Equal(6, MapGenerator.StartSpacingRequirement(12, 3));
		Assert.Equal(6, MapGenerator.StartSpacingRequirement(12, 5));

		Assert.True(MapGenerator.StartSpacingApplies(5));
		Assert.False(MapGenerator.StartSpacingApplies(6));
	}

	[Fact]
	public void TheSpacingTestIsTheWrappedRankDistance() {
		GameMap m = MakeMap(60, 8, (x, y) => Grassland());
		Tile origin = At(m, 20, 4);

		// The distance the original computes with its offset term is the rank
		// distance: two tiles apart on the same row is 3, not 2.
		Assert.Equal(3, origin.RankDistanceTo(At(m, 24, 4)));
		Assert.Equal(6, origin.RankDistanceTo(At(m, 28, 4)));

		List<Tile> starts = new() { origin };
		Assert.True(MapGenerator.TooCloseToAnotherStart(At(m, 24, 4), starts, 3));
		Assert.False(MapGenerator.TooCloseToAnotherStart(At(m, 24, 4), starts, 2));
		Assert.False(MapGenerator.TooCloseToAnotherStart(At(m, 28, 4), starts, 3));
	}

	// ---------------------------------------------------- the search, end to end

	// A map on which every tile is rejected by the score, so the only way a start
	// can be placed at all is the pass-7 relaxation of the score gate.
	[Fact]
	public void AStartCanOnlyBePlacedOnPassSevenWhenEveryScoreIsZero() {
		WorldCharacteristics wc = MakeWc(60, 8, numberOfCivs: 1, distanceBetweenCivs: 0);
		GameMap m = MakeMap(60, 8, (x, y) => Desert());
		Assert.True(MapGenerator.ScoreStartCandidate(wc, m, MapGenerator.BodyAreas(m), At(m, 30, 4)) == 0);

		List<Tile> starts = MapGenerator.PlaceStartingLocations(wc, m, out int[] passOfStart);

		Assert.Single(starts);
		Assert.Equal(7, passOfStart[0]);
	}

	// A map whose only positive-scoring tiles are two pairs of grassland tiles in
	// a desert sea, one pair at each end. The distance between the pairs fixes
	// the pass at which the second start can be accepted, so the two relaxations
	// are observable end to end.
	private static GameMap TwoGrasslandPatchesInADesert(out WorldCharacteristics wc, int distanceBetweenCivs) {
		wc = MakeWc(60, 8, numberOfCivs: 2, distanceBetweenCivs: distanceBetweenCivs);
		return MakeMap(60, 8, (x, y) => {
			bool patchA = y == 4 && (x == 2 || x == 4);
			bool patchB = y == 4 && (x == 30 || x == 32);
			return patchA || patchB ? Grassland() : Desert();
		});
	}

	// The two patches are 22 ranks apart. With a requirement of 30 no second
	// start fits until pass 3 halves it to 15; with a requirement of 100 none
	// fits until pass 6 waives the test entirely.
	[Fact]
	public void TheSecondStartWaitsForTheHalvedSpacingFromPassThree() {
		GameMap m = TwoGrasslandPatchesInADesert(out WorldCharacteristics wc, distanceBetweenCivs: 30);

		List<Tile> starts = MapGenerator.PlaceStartingLocations(wc, m, out int[] passOfStart);

		Assert.Equal(2, starts.Count);
		Assert.Equal(1, passOfStart[0]);
		Assert.Equal(3, passOfStart[1]);
	}

	[Fact]
	public void TheSecondStartWaitsForTheWaivedSpacingFromPassSix() {
		GameMap m = TwoGrasslandPatchesInADesert(out WorldCharacteristics wc, distanceBetweenCivs: 100);

		List<Tile> starts = MapGenerator.PlaceStartingLocations(wc, m, out int[] passOfStart);

		Assert.Equal(2, starts.Count);
		Assert.Equal(1, passOfStart[0]);
		Assert.Equal(6, passOfStart[1]);
	}

	// All the land is in the top and bottom pole rows, which the search never
	// accepts, so no start is placed at all - not even on pass 7, when every
	// other gate has been relaxed.
	[Fact]
	public void StartsAreNeverPlacedInThePoleRows() {
		WorldCharacteristics wc = MakeWc(20, 8, numberOfCivs: 1, distanceBetweenCivs: 0);
		GameMap m = MakeMap(20, 8, (x, y) => y == 0 || y == 7 ? Grassland() : Ocean());

		List<Tile> starts = MapGenerator.PlaceStartingLocations(wc, m, out int[] passOfStart);

		Assert.Empty(starts);
	}

	// ------------------------------------------------------------------ whole maps

	private WorldCharacteristics GeneratedWorldCharacteristics(int seed, int numberOfCivs = 4) {
		return new(saveGameFixture.saveGame) {
			landform = WorldCharacteristics.Landform.Pangaea,
			oceanCoverage = WorldCharacteristics.OceanCoverage.Percent_70,
			age = WorldCharacteristics.Age.Billion_4,
			climate = WorldCharacteristics.Climate.Normal,
			temperature = WorldCharacteristics.Temperature.Temperate,
			worldSize = new WorldSize() {
				width = 60,
				height = 40,
				numberOfCivs = numberOfCivs,
				distanceBetweenCivs = 12,
			},
			mapSeed = seed,
		};
	}

	private static string StartCoordinates(GameMap m) {
		return string.Join(";", m.startingLocations.Select(t => $"{t.XCoordinate},{t.YCoordinate}"));
	}

	// The same seed must give the same starts, and a different seed must move
	// them - the candidate order and the final reshuffle are both seeded.
	[Fact]
	public void TheSameSeedGivesTheSameStarts() {
		if (Civ3TestData.ShouldSkipCiv3DependentTests()) {
			Skip.If(true, "Civ3 assets are not available");
		}

		GameMap first = MapGenerator.GenerateMap(GeneratedWorldCharacteristics(seed: 4242));
		GameMap again = MapGenerator.GenerateMap(GeneratedWorldCharacteristics(seed: 4242));
		Assert.Equal(StartCoordinates(first), StartCoordinates(again));

		GameMap other = MapGenerator.GenerateMap(GeneratedWorldCharacteristics(seed: 987654));
		Assert.NotEqual(StartCoordinates(first), StartCoordinates(other));
	}

	// The starts a generated map hands out: one per civ, distinct, on land, and
	// never in a pole row.
	[Fact]
	public void GeneratedStartsAreOnePerCivOnLandAndOffThePoles() {
		if (Civ3TestData.ShouldSkipCiv3DependentTests()) {
			Skip.If(true, "Civ3 assets are not available");
		}

		WorldCharacteristics wc = GeneratedWorldCharacteristics(seed: 4242);
		GameMap m = MapGenerator.GenerateMap(wc);

		Assert.Equal(wc.worldSize.numberOfCivs, m.startingLocations.Count);
		Assert.Equal(m.startingLocations.Count, m.startingLocations.Distinct().Count());
		foreach (Tile t in m.startingLocations) {
			Assert.True(t.IsLand(), $"the start at ({t.XCoordinate},{t.YCoordinate}) is not on land");
			Assert.NotEqual(0, t.YCoordinate);
			Assert.NotEqual(m.numTilesTall - 1, t.YCoordinate);
		}
	}
}
