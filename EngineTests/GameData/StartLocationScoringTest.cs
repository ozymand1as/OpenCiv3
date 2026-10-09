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

	// A city standing on a tile, which is what marks that tile and its cross as
	// "inside a city's working radius" (and is what the acceptance screens
	// look for).
	private static void PlaceCity(Tile t) {
		t.cityAtTile = new City(t, new Player(), "Test City", ID.None("city"));
	}

	// The first `count` tiles of Civ3's spiral around a tile - the big fat cross
	// for count 21.
	private static HashSet<Tile> CrossTiles(GameMap m, Tile centre, int count) {
		HashSet<Tile> tiles = new();
		for (int i = 0; i < count; ++i) {
			(int dx, int dy) = MapGenerator.Civ3SpiralOffset(i);
			tiles.Add(At(m, centre.XCoordinate + dx, centre.YCoordinate + dy));
		}
		return tiles;
	}

	private static Tile At(GameMap m, int x, int y) {
		Tile t = m.tileAt(x, y);
		Assert.True(t != Tile.NONE, $"no tile at ({x},{y})");
		return t;
	}

	// The shipped rules' irrigation, mining and road bonuses, keyed by terrain
	// key, as ImportCiv3 fills them from TERR.IrrigationBonus/MiningBonus/
	// RoadBonus and as C7/Lua/civ3/ruleset.json carries them. The score reads
	// the same values out of the terrain record (irrigation food at `+0x4c`,
	// mining shields at `+0x50`, road commerce at `+0x54`), and the
	// irrigation entry is also the "may a city stand here" gate, so a map with
	// no irrigation bonuses at all has no legal sites.
	private static List<SaveTerrainImprovement> ShippedImprovements() {
		SaveTerrainImprovement irrigation = new(Tile.TileOverlays.IRRIGATION, TerrainImprovement.Layer.ResourceDevelopment);
		SaveTerrainImprovement mine = new(Tile.TileOverlays.MINE, TerrainImprovement.Layer.ResourceDevelopment);
		SaveTerrainImprovement road = new(Tile.TileOverlays.ROAD, TerrainImprovement.Layer.Roads);

		foreach (string terrain in new string[] { "desert", "plains", "grassland", "flood plain" }) {
			irrigation.bonusYields[terrain] = new() { [Tile.YieldType.Food] = 1 };
		}
		foreach (string terrain in new string[] { "desert", "plains", "grassland", "tundra" }) {
			mine.bonusYields[terrain] = new() { [Tile.YieldType.Production] = 1 };
		}
		mine.bonusYields["hills"] = new() { [Tile.YieldType.Production] = 2 };
		mine.bonusYields["mountains"] = new() { [Tile.YieldType.Production] = 2 };
		foreach (string terrain in new string[] {
			"desert", "plains", "grassland", "tundra", "flood plain",
			"hills", "mountains", "forest", "jungle", "marsh" }) {
			road.bonusYields[terrain] = new() { [Tile.YieldType.Commerce] = 1 };
		}

		return new List<SaveTerrainImprovement>() { irrigation, mine, road };
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
			terrainImprovements = ShippedImprovements(),
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

	// The sum over the cross reads the tile's terrain, i.e. the *effective*
	// terrain - the fork's `overlayTerrainType`, which is what Civ3's
	// `plot+0xc8` holds (see `MapGenerator.ScoreTerrain`). Two maps whose cross
	// tiles differ only in the ground under the overlay must therefore score
	// identically, and changing one tile's overlay must move the score. A port
	// that read `baseTerrainType` fails both halves.
	[Fact]
	public void TheCrossSumCountsTheOverlayTerrainNotTheBase() {
		WorldCharacteristics wc = MakeWc(60, 8, 2, 0);

		// All grassland, overlay and base alike.
		GameMap grass = MakeMap(60, 8, (x, y) => Grassland());
		// One cross tile becomes forest: the overlay changes, the base stays
		// grassland.
		GameMap forestOnGrass = MakeMap(60, 8, (x, y) => Grassland());
		At(forestOnGrass, 29, 3).overlayTerrainType = Forest();
		// The same forest overlay, but on a plains base.
		GameMap forestOnPlains = MakeMap(60, 8, (x, y) => Grassland());
		At(forestOnPlains, 29, 3).baseTerrainType = Plains();
		At(forestOnPlains, 29, 3).overlayTerrainType = Forest();

		// The overlay is not the base, so the fixture really does put the two
		// fields out of step.
		Assert.NotEqual(At(forestOnGrass, 29, 3).baseTerrainType.Key,
			At(forestOnGrass, 29, 3).overlayTerrainType.Key);

		int plain = MapGenerator.StartScoreBucketSum(wc, grass, MapGenerator.BodyAreas(grass),
			MapGenerator.CityRadiusTiles(grass), At(grass, 30, 4));
		int overlayChanged = MapGenerator.StartScoreBucketSum(wc, forestOnGrass, MapGenerator.BodyAreas(forestOnGrass),
			MapGenerator.CityRadiusTiles(forestOnGrass), At(forestOnGrass, 30, 4));
		int baseChanged = MapGenerator.StartScoreBucketSum(wc, forestOnPlains, MapGenerator.BodyAreas(forestOnPlains),
			MapGenerator.CityRadiusTiles(forestOnPlains), At(forestOnPlains, 30, 4));

		Assert.NotEqual(plain, overlayChanged);
		Assert.Equal(overlayChanged, baseChanged);
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

	// The tile the city would stand on always feeds two, whatever its terrain:
	// the score overwrites it at `0x442970`. A desert site feeds one food once
	// it is irrigated, so the override is what makes the difference visible.
	[Fact]
	public void TheSiteTileAlwaysFeedsTwo() {
		WorldCharacteristics wc = MakeWc(60, 8, 2, 0);
		GameMap m = MakeMap(60, 8, (x, y) => x == 30 && y == 4 ? Desert() : Grassland());
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

		int without = MapGenerator.StartScoreBucketSum(wc, plain, MapGenerator.BodyAreas(plain),
			MapGenerator.CityRadiusTiles(plain), At(plain, 30, 4));
		int outer = MapGenerator.StartScoreBucketSum(wc, withOuterLuxury, MapGenerator.BodyAreas(withOuterLuxury),
			MapGenerator.CityRadiusTiles(withOuterLuxury), At(withOuterLuxury, 30, 4));
		int inner = MapGenerator.StartScoreBucketSum(wc, withInnerLuxury, MapGenerator.BodyAreas(withInnerLuxury),
			MapGenerator.CityRadiusTiles(withInnerLuxury), At(withInnerLuxury, 30, 4));

		Assert.Equal(2 + 1, outer - without);
		Assert.Equal(4 + 1, inner - without);
	}

	// A rejected tile scores zero. Desert sites are rejected because no
	// neighbour can feed more than one citizen, mountains do not allow cities,
	// and tundra is rejected because its terrain cannot be irrigated at all.
	[Fact]
	public void RejectedTilesScoreZero() {
		WorldCharacteristics wc = MakeWc(60, 8, 2, 0);

		GameMap desert = MakeMap(60, 8, (x, y) => Desert());
		Tile desertTile = At(desert, 30, 4);
		// Desert IS irrigable, so it passes the terrain gate; what rejects it is
		// that no neighbour of a desert tile can feed more than one citizen.
		Assert.Equal(1, MapGenerator.IrrigationBonus(wc, desertTile));
		Assert.True(MapGenerator.StartSiteRejected(wc, desert, MapGenerator.BodyAreas(desert), desertTile));
		Assert.Equal(0, MapGenerator.ScoreStartCandidate(wc, desert, MapGenerator.BodyAreas(desert),
			MapGenerator.CityRadiusTiles(desert), desertTile));

		GameMap mountains = MakeMap(60, 8, (x, y) => Mountains());
		Tile mountainTile = At(mountains, 30, 4);
		Assert.Equal(0, MapGenerator.ScoreStartCandidate(wc, mountains, MapGenerator.BodyAreas(mountains),
			MapGenerator.CityRadiusTiles(mountains), mountainTile));

		// Tundra cannot be irrigated, so it fails the irrigation gate.
		GameMap tundra = MakeMap(60, 8, (x, y) => Tundra());
		Tile tundraTile = At(tundra, 30, 4);
		Assert.Equal(1, tundraTile.baseTerrainType.baseFoodProduction);
		Assert.Equal(0, MapGenerator.IrrigationBonus(wc, tundraTile));
		Assert.True(MapGenerator.StartSiteRejected(wc, tundra, MapGenerator.BodyAreas(tundra), tundraTile));
		Assert.Equal(0, MapGenerator.ScoreStartCandidate(wc, tundra, MapGenerator.BodyAreas(tundra),
			MapGenerator.CityRadiusTiles(tundra), tundraTile));

		// The control: grassland is a valid site with a positive score.
		GameMap grass = MakeMap(60, 8, (x, y) => Grassland());
		Assert.True(MapGenerator.ScoreStartCandidate(wc, grass, MapGenerator.BodyAreas(grass),
			MapGenerator.CityRadiusTiles(grass), At(grass, 30, 4)) > 0);
	}

	// What decides whether the site itself may hold a city is the terrain's
	// irrigation bonus (`plot+0xc8`'s terrain, field `+0x4c`), not its food:
	// `0x442c7c` calls the irrigation-bonus getter and returns zero for a tile
	// whose result is zero. So a desert tile - no food of its own, but
	// irrigable - is a legal site, while forest, jungle, marsh, hills, tundra
	// and mountains, which all yield food, are not. A port that gated on the
	// terrain's food has both of these the other way round.
	[Fact]
	public void ASiteIsJudgedByItsTerrainIrrigationBonusNotItsFood() {
		WorldCharacteristics wc = MakeWc(60, 8, 2, 0);

		GameMap desertInGrass = MakeMap(60, 8, (x, y) => x == 30 && y == 4 ? Desert() : Grassland());
		Tile desertSite = At(desertInGrass, 30, 4);
		Assert.Equal(0, desertSite.baseTerrainType.baseFoodProduction);
		Assert.Equal(1, MapGenerator.IrrigationBonus(wc, desertSite));
		Assert.False(MapGenerator.StartSiteRejected(wc, desertInGrass, MapGenerator.BodyAreas(desertInGrass), desertSite));

		GameMap forestInGrass = MakeMap(60, 8, (x, y) => x == 30 && y == 4 ? Grassland() : Grassland());
		At(forestInGrass, 30, 4).overlayTerrainType = Forest();
		Tile forestSite = At(forestInGrass, 30, 4);
		Assert.Equal(1, forestSite.overlayTerrainType.baseFoodProduction);
		Assert.Equal(0, MapGenerator.IrrigationBonus(wc, forestSite));
		Assert.True(MapGenerator.StartSiteRejected(wc, forestInGrass, MapGenerator.BodyAreas(forestInGrass), forestSite));

		GameMap hillsInGrass = MakeMap(60, 8, (x, y) => x == 30 && y == 4 ? Grassland() : Grassland());
		At(hillsInGrass, 30, 4).overlayTerrainType = Hills();
		Tile hillsSite = At(hillsInGrass, 30, 4);
		Assert.Equal(1, hillsSite.overlayTerrainType.baseFoodProduction);
		Assert.Equal(0, MapGenerator.IrrigationBonus(wc, hillsSite));
		Assert.True(MapGenerator.StartSiteRejected(wc, hillsInGrass, MapGenerator.BodyAreas(hillsInGrass), hillsSite));
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

		int dryBefore = MapGenerator.ScoreStartCandidate(wc, m, areas, MapGenerator.CityRadiusTiles(m), dry);
		int riverBefore = MapGenerator.ScoreStartCandidate(wc, m, areas, MapGenerator.CityRadiusTiles(m), river);
		Assert.Equal(dryBefore, riverBefore);

		// A river on the site (an edge of the tile) makes it fresh water.
		river.riverNorth = true;
		Assert.True(river.BordersRiver());
		Assert.True(MapGenerator.HasFreshWater(river));

		int dryScore = MapGenerator.ScoreStartCandidate(wc, m, areas, MapGenerator.CityRadiusTiles(m), dry);
		int riverScore = MapGenerator.ScoreStartCandidate(wc, m, areas, MapGenerator.CityRadiusTiles(m), river);

		Assert.True(riverScore > dryScore, $"a river site ({riverScore}) should outscore an equivalent dry one ({dryScore})");
		Assert.True(riverScore > 100 * dryScore,
			$"the fresh-water preference is a divisor, so the river site ({riverScore}) should dwarf the dry one ({dryScore})");
		// A flat additive bonus would leave the difference a tiny fraction of the
		// score; the divisor makes the difference comparable to the score itself.
		Assert.True(riverScore - dryScore > riverScore / 2,
			"the river preference should move most of the score, not add a flat handful of points");
	}

	// ------------------------------------------------- the small-term mapping

	// The "food >= 3 with a shield" bucket is added TWICE: the sum at
	// `0x442bab` doubles the difference between that bucket's count and the
	// city-radius count. With only the irrigation bonus in the rules, a
	// grassland cross tile is food 3 and no shields and lands in no bucket at
	// all; the same tile carrying a strategic resource with a shield bonus is a
	// great tile, so the difference is exactly the two points the bucket is
	// worth plus the resource's own four - and a port that did not double the
	// bucket would show 4.
	[Fact]
	public void AGreatTileInTheCrossAddsExactlyTwoPoints() {
		WorldCharacteristics wc = MakeWc(60, 8, 2, 0);
		wc.terrainImprovements = ShippedImprovements()
			.Where(i => i.key == Tile.TileOverlays.IRRIGATION).ToList();

		GameMap plain = MakeMap(60, 8, (x, y) => Grassland());
		GameMap withResource = MakeMap(60, 8, (x, y) => Grassland());
		At(withResource, 29, 3).Resource = new Resource() {
			Key = "strat",
			Name = "strat",
			Category = ResourceCategory.STRATEGIC,
			ShieldsBonus = 1,
		};

		(int food, int shields, int commerce) resourceTile =
			MapGenerator.StartScoreTileYields(wc, withResource, MapGenerator.BodyAreas(withResource), At(withResource, 29, 3), 7);
		Assert.Equal(3, resourceTile.food);
		Assert.Equal(1, resourceTile.shields);

		int without = MapGenerator.StartScoreBucketSum(wc, plain, MapGenerator.BodyAreas(plain),
			MapGenerator.CityRadiusTiles(plain), At(plain, 30, 4));
		int with = MapGenerator.StartScoreBucketSum(wc, withResource, MapGenerator.BodyAreas(withResource),
			MapGenerator.CityRadiusTiles(withResource), At(withResource, 30, 4));

		// Two for the great bucket and four for the strategic resource, which
		// sits in the inner three-by-three.
		Assert.Equal(2 + 4, with - without);
	}

	// A grassland tile from the same rules is in no bucket at all, which is
	// what the test above rests on.
	[Fact]
	public void APlainGrassTileFallsIntoNoBucket() {
		WorldCharacteristics wc = MakeWc(60, 8, 2, 0);
		wc.terrainImprovements = ShippedImprovements()
			.Where(i => i.key == Tile.TileOverlays.IRRIGATION).ToList();
		GameMap m = MakeMap(60, 8, (x, y) => Grassland());

		Assert.Equal(MapGenerator.START_CANDIDATE_BASE,
			MapGenerator.StartScoreBucketSum(wc, m, MapGenerator.BodyAreas(m),
				MapGenerator.CityRadiusTiles(m), At(m, 30, 4)));
	}

	// The resource-point term splits on the spiral index: four points in the
	// inner three-by-three and two outside it (`0x442884`), and the luxury count
	// is a separate +1. A luxury therefore beats an identical strategic resource
	// by exactly one point, and both beat a bonus resource by more.
	[Fact]
	public void LuxuryResourcesScoreOneMoreThanStrategicOnes() {
		WorldCharacteristics wc = MakeWc(60, 8, 2, 0);

		GameMap luxuryMap = MakeMap(60, 8, (x, y) => Grassland());
		GameMap strategicMap = MakeMap(60, 8, (x, y) => Grassland());
		At(luxuryMap, 29, 3).Resource = new Resource() { Key = "lux", Name = "lux", Category = ResourceCategory.LUXURY };
		At(strategicMap, 29, 3).Resource = new Resource() { Key = "strat", Name = "strat", Category = ResourceCategory.STRATEGIC };

		int luxury = MapGenerator.StartScoreBucketSum(wc, luxuryMap, MapGenerator.BodyAreas(luxuryMap),
			MapGenerator.CityRadiusTiles(luxuryMap), At(luxuryMap, 30, 4));
		int strategic = MapGenerator.StartScoreBucketSum(wc, strategicMap, MapGenerator.BodyAreas(strategicMap),
			MapGenerator.CityRadiusTiles(strategicMap), At(strategicMap, 30, 4));

		Assert.NotEqual(0, luxury);
		Assert.Equal(1, luxury - strategic);
	}

	// ------------------------------------------- the tiles inside a city radius

	// A cross tile inside an existing city's working radius is skipped outright
	// and charged twice: the sum subtracts `2 *` the number of such tiles
	// (`0x442bab`). The radius set is passed in directly here so that the test
	// pins one tile at a time; a real city's cross overlaps a neighbouring
	// candidate's own cross heavily, which is what the > 9 rejection covers.
	[Fact]
	public void ACrossTileInsideACitysRadiusIsSkippedAndChargedTwice() {
		WorldCharacteristics wc = MakeWc(60, 8, 2, 0);

		Resource luxury = new() { Key = "test luxury", Name = "test luxury", Category = ResourceCategory.LUXURY };

		GameMap plain = MakeMap(60, 8, (x, y) => Grassland());
		int without = MapGenerator.StartScoreBucketSum(wc, plain, MapGenerator.BodyAreas(plain),
			MapGenerator.CityRadiusTiles(plain), At(plain, 30, 4));

		// One plain cross tile inside a city radius: the tile is skipped, so it
		// loses its shield and its bucket, and the sum then charges two for it.
		GameMap marked = MakeMap(60, 8, (x, y) => Grassland());
		HashSet<Tile> oneTile = new() { At(marked, 29, 3) };
		int oneSkipped = MapGenerator.StartScoreBucketSum(wc, marked, MapGenerator.BodyAreas(marked),
			oneTile, At(marked, 30, 4));

		// The same tile, but carrying a luxury: skipping it must throw away the
		// luxury's four points and its luxury count too, not just its bucket.
		GameMap luxurious = MakeMap(60, 8, (x, y) => Grassland());
		At(luxurious, 29, 3).Resource = luxury;
		HashSet<Tile> oneLuxuryTile = new() { At(luxurious, 29, 3) };
		int luxurySkipped = MapGenerator.StartScoreBucketSum(wc, luxurious, MapGenerator.BodyAreas(luxurious),
			oneLuxuryTile, At(luxurious, 30, 4));

		// A grassland tile with the mining bonus is food 3 and one shield, i.e.
		// a great tile worth two; the skip also charges two.
		Assert.Equal(without - 2 - 2, oneSkipped);
		// The luxury on the skipped tile changes nothing: the tile is skipped
		// before its resource is ever looked at, so the luxury's four points and
		// its luxury count never enter the sum. A port that counted them and only
		// skipped the buckets would end up five points higher.
		Assert.Equal(oneSkipped, luxurySkipped);
	}

	// More than nine tiles of the cross inside a city's working radius rejects
	// the site outright (`0x442640` for the cross, `0x44259c` for the
	// twenty-five-position screen). A city standing ON the candidate covers the
	// candidate's own cross, which is twenty-one tiles.
	[Fact]
	public void ASiteWithMoreThanNineTilesInsideACitysRadiusScoresZero() {
		WorldCharacteristics wc = MakeWc(60, 8, 2, 0);

		GameMap plain = MakeMap(60, 8, (x, y) => Grassland());
		Assert.True(MapGenerator.ScoreStartCandidate(wc, plain, MapGenerator.BodyAreas(plain),
			MapGenerator.CityRadiusTiles(plain), At(plain, 30, 4)) > 0);

		GameMap withCity = MakeMap(60, 12, (x, y) => Grassland());
		PlaceCity(At(withCity, 30, 6));
		Assert.Equal(21, MapGenerator.CityRadiusTiles(withCity).Count);
		Assert.True(MapGenerator.StartPrePassRejected(withCity, MapGenerator.CityRadiusTiles(withCity), At(withCity, 30, 6)));
		Assert.Equal(0, MapGenerator.ScoreStartCandidate(wc, withCity, MapGenerator.BodyAreas(withCity),
			MapGenerator.CityRadiusTiles(withCity), At(withCity, 30, 6)));

		// Ten tiles is over the limit and nine is not, so the bound is exact.
		GameMap boundary = MakeMap(60, 12, (x, y) => Grassland());
		Tile candidate = At(boundary, 30, 6);
		HashSet<Tile> nine = CrossTiles(boundary, candidate, 9);
		Assert.False(MapGenerator.StartPrePassRejected(boundary, nine, candidate));
		HashSet<Tile> ten = CrossTiles(boundary, candidate, 10);
		Assert.True(MapGenerator.StartPrePassRejected(boundary, ten, candidate));
	}

	// No pass of the generator sets the city-radius mark, so on a freshly
	// generated map the set is empty and the term is genuinely zero there.
	[Fact]
	public void AGeneratedMapHasNoTilesInsideACityRadius() {
		if (Civ3TestData.ShouldSkipCiv3DependentTests()) {
			Skip.If(true, "Civ3 assets are not available");
		}

		GameMap m = MapGenerator.GenerateMap(GeneratedWorldCharacteristics(seed: 4242));
		Assert.Empty(MapGenerator.CityRadiusTiles(m));
	}

	// --------------------------------------------------- the acceptance screens

	// The seven screens `FUN_005eeee0` runs on a candidate (`0x5ef2ee`-
	// `0x5ef3b1`): not already a start, land, no goody hut or barbarian camp,
	// no barbarian tribe, no city, no unit. Each has to be able to fire on its
	// own, or the screen is not modelled at all.
	[Fact]
	public void TheAcceptanceScreensRejectStartsOnFeaturesAndOccupants() {
		GameMap m = MakeMap(60, 8, (x, y) => Grassland());
		Tile t = At(m, 30, 4);
		List<Tile> starts = new();

		Assert.False(MapGenerator.StartScreenRejects(t, starts));

		Assert.True(MapGenerator.StartScreenRejects(t, new List<Tile>() { t }));

		Tile goodyHut = At(m, 28, 4);
		goodyHut.hasGoodyHut = true;
		Assert.True(MapGenerator.StartScreenRejects(goodyHut, starts));

		Tile camp = At(m, 26, 4);
		camp.hasBarbarianCamp = true;
		Assert.True(MapGenerator.StartScreenRejects(camp, starts));

		Tile tribe = At(m, 24, 4);
		tribe.barbarianTribeId = 3;
		Assert.True(MapGenerator.StartScreenRejects(tribe, starts));

		Tile water = At(m, 22, 4);
		water.overlayTerrainType = Coast();
		Assert.True(MapGenerator.StartScreenRejects(water, starts));
	}

	// A tile with a unit on it is screened out by the plot's unit id
	// (`plot+0x0c`).
	[Fact]
	public void AStartIsNeverPlacedOnATileWithAUnit() {
		GameMap m = MakeMap(60, 8, (x, y) => Grassland());
		Tile t = At(m, 30, 4);
		Assert.False(MapGenerator.StartScreenRejects(t, new List<Tile>()));

		t.unitsOnTile.Add(new MapUnit());
		Assert.True(MapGenerator.StartScreenRejects(t, new List<Tile>()));
	}

	// A city on the tile is screened out by the plot's city id (`plot+0x1c`).
	[Fact]
	public void AStartIsNeverPlacedOnATileWithACity() {
		GameMap m = MakeMap(60, 8, (x, y) => Grassland());
		Tile t = At(m, 30, 4);
		Assert.False(t.HasCity());
		PlaceCity(t);
		Assert.True(MapGenerator.StartScreenRejects(t, new List<Tile>()));
	}

	// ----------------------------------------------------- the final reshuffle

	// The original's final reshuffle (`0x5ef655`-`0x5ef6de`) walks positions 1
	// to n-2, so the LAST start never moves, and it forces the first drawn step
	// to swap position 0 with the fixed slot `(2n - 2) / 3` rather than drawing
	// a random one. Both are visible without knowing anything about the random
	// numbers, so a plain Fisher-Yates over the whole list fails this test.
	[Fact]
	public void TheFinalReshufflePinsTheFirstStartAndLeavesTheLastOneAlone() {
		const int n = 8;
		List<Tile> starts = new();
		ID.Factory factory = new();
		for (int i = 0; i < n; ++i) {
			Tile t = new(factory.CreateID("tile"));
			t.XCoordinate = i;
			starts.Add(t);
		}
		Tile firstOfAll = starts[0];
		Tile atTheFixedSlot = starts[(2 * n - 2) / 3];
		Tile last = starts[n - 1];

		MapGenerator.ReshuffleStartOrder(new Civ3StartRandom(1234), starts);

		Assert.Equal(n, starts.Distinct().Count());
		Assert.Equal(atTheFixedSlot, starts[0]);
		Assert.Equal(last, starts[n - 1]);
		Assert.NotEqual(firstOfAll, starts[0]);
	}

	// The reshuffle is a pure function of the seed, and two seeds part.
	[Fact]
	public void TheFinalReshuffleIsSeeded() {
		List<string> Order(int seed) {
			List<Tile> starts = new();
			ID.Factory factory = new();
			for (int i = 0; i < 8; ++i) {
				Tile t = new(factory.CreateID("tile"));
				t.XCoordinate = i;
				starts.Add(t);
			}
			MapGenerator.ReshuffleStartOrder(new Civ3StartRandom(seed), starts);
			return starts.Select(t => t.XCoordinate.ToString()).ToList();
		}

		Assert.Equal(Order(7), Order(7));
		Assert.Equal(8, Order(7).Distinct().Count());
		Assert.NotEqual(string.Join(",", Order(7)), string.Join(",", Order(8)));
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
		Assert.True(MapGenerator.ScoreStartCandidate(wc, m, MapGenerator.BodyAreas(m),
			MapGenerator.CityRadiusTiles(m), At(m, 30, 4)) == 0);

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

	// A fingerprint of every tile's terrain, so a test can tell two generated
	// maps apart without touching the image ids.
	private static string TerrainSignature(GameMap m) {
		return string.Join(";", m.tiles.Select(t =>
			$"{t.XCoordinate},{t.YCoordinate},{t.baseTerrainType.Key}/{t.overlayTerrainType.Key}"));
	}

	// The whole generated map is a pure function of the seed, not just the
	// starts: two runs of the same seed must agree tile for tile.
	[Fact]
	public void TheSameSeedGivesTheSameMap() {
		if (Civ3TestData.ShouldSkipCiv3DependentTests()) {
			Skip.If(true, "Civ3 assets are not available");
		}

		GameMap first = MapGenerator.GenerateMap(GeneratedWorldCharacteristics(seed: 4242));
		GameMap again = MapGenerator.GenerateMap(GeneratedWorldCharacteristics(seed: 4242));
		Assert.Equal(TerrainSignature(first), TerrainSignature(again));

		GameMap other = MapGenerator.GenerateMap(GeneratedWorldCharacteristics(seed: 987654));
		Assert.NotEqual(TerrainSignature(first), TerrainSignature(other));
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
