using System;
using System.Collections.Generic;
using System.Linq;
using C7Engine;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using Xunit;

namespace EngineTests.GameData;

// Civ3 guarantees fresh water on every big landmass: the generator walks each
// body of more than 74 tiles that has no fresh water of its own, and turns one
// land tile whose nine-tile neighbourhood is irrigable land into coast. A
// one-tile water body is fresh water, so a city on the lake's shore grows
// without an aqueduct. The rules, the addresses they were read from and what
// was verified against the binary are in
// re/notes/civ3_map_generator_spec.md section 2.8.
public class FreshWaterLakesTest : IClassFixture<SaveGameFixture> {
	private readonly SaveGameFixture saveGameFixture;

	public FreshWaterLakesTest(SaveGameFixture fix) {
		saveGameFixture = fix;
	}

	// ------------------------------------------------------------------ terrain

	private static TerrainType Grassland() {
		return new() { Key = "grassland", baseFoodProduction = 2, movementCost = 1 };
	}

	private static TerrainType Hills() {
		return new() { Key = "hills", baseFoodProduction = 1, baseShieldProduction = 1, movementCost = 2 };
	}

	private static TerrainType Coast() {
		return new() { Key = "coast", baseFoodProduction = 1, movementCost = 1 };
	}

	private static TerrainType Ocean() {
		return new() { Key = "ocean", baseFoodProduction = 1, movementCost = 1 };
	}

	private static List<TerrainType> RuleTerrains() {
		return new List<TerrainType>() { Grassland(), Hills(), Coast(), Ocean() };
	}

	// The rules' irrigation entry carries the terrains the rules can irrigate.
	// The shipped ruleset lists desert, plains, grassland and flood plain
	// (C7/Lua/civ3/ruleset.json), and the BIQ import derives the same set from
	// each terrain record's IrrigationBonus, so this is the value the pass
	// reads whichever path the game came in through.
	private static SaveTerrainImprovement Irrigation(params string[] terrainKeys) {
		SaveTerrainImprovement irrigation = new(Tile.TileOverlays.IRRIGATION,
												TerrainImprovement.Layer.ResourceDevelopment);
		foreach (string key in terrainKeys) {
			irrigation.bonusYields[key] = new Dictionary<Tile.YieldType, int>() { [Tile.YieldType.Food] = 1 };
		}
		return irrigation;
	}

	private static WorldCharacteristics MakeWorldCharacteristics(List<TerrainType> terrains, int seed = 1234,
																params string[] irrigableTerrainKeys) {
		return new WorldCharacteristics() {
			landform = WorldCharacteristics.Landform.Pangaea,
			oceanCoverage = WorldCharacteristics.OceanCoverage.Percent_70,
			age = WorldCharacteristics.Age.Billion_4,
			climate = WorldCharacteristics.Climate.Normal,
			temperature = WorldCharacteristics.Temperature.Temperate,
			worldSize = new WorldSize() { width = 20, height = 8 },
			terrainTypes = terrains,
			terrainImprovements = new List<SaveTerrainImprovement>() { Irrigation(irrigableTerrainKeys) },
			mapSeed = seed,
		};
	}

	// --------------------------------------------------------------------- maps

	// Builds a map with the tile ordering the generator uses, so that
	// GameMap.tileAt resolves coordinates the same way it does on a generated
	// map.
	private static GameMap MakeSyntheticMap(int width, int height, Func<int, int, TerrainType> terrainAt) {
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

	private static GameMap MakeLandMap(int tiles, TerrainType terrain) {
		// A body of `tiles` tiles: the storage grid holds width * height / 2
		// tiles, and one row of them is connected east to west.
		return MakeSyntheticMap(tiles * 2, 1, (x, y) => terrain);
	}

	private static GameMap MakeGrasslandMap(string lakeKey = null, int lakeX = 0, int lakeY = 0) {
		GameMap m = MakeSyntheticMap(20, 8, (x, y) =>
			lakeKey != null && x == lakeX && y == lakeY ? (lakeKey == "ocean" ? Ocean() : Coast()) : Grassland());

		if (lakeKey != null) {
			// The pre-existing water changes the bodies and the fresh-water
			// flag, so work them out before the pass sees the map.
			m.recomputeContinents();
		}

		return m;
	}

	private static int CountTerrain(GameMap m, string key) {
		return m.tiles.Count(t => t.baseTerrainType.Key == key);
	}

	private static Tile TheLake(GameMap m) {
		return m.tiles.First(t => !t.IsLand());
	}

	// A landmass of 80 tiles, all of it dry grassland, must end up with exactly
	// one new lake, and that lake must read as fresh water and be a body of its
	// own - which is what makes it fresh water rather than a sea.
	[Fact]
	public void ALandmassBiggerThan74GetsExactlyOneFreshWaterLake() {
		WorldCharacteristics wc = MakeWorldCharacteristics(RuleTerrains(), irrigableTerrainKeys: "grassland");
		GameMap m = MakeGrasslandMap();

		Assert.Equal(1, MapGenerator.AddFreshWaterLakes(wc, m));

		Assert.Equal(1, CountTerrain(m, "coast"));
		Tile lake = TheLake(m);
		Assert.Equal("coast", lake.baseTerrainType.Key);
		Assert.Equal("coast", lake.overlayTerrainType.Key);

		HashSet<Tile> lakeBody = m.continents.First(c => c.Contains(lake));
		Assert.Equal(1, lakeBody.Count);
		Assert.True(lake.isFreshWater, "the seeded lake is not marked as fresh water");

		// Every tile of the body carries the same body id, and the lake is a
		// body of its own.
		Assert.NotEqual(lake.continent, lake.neighbors.Values.First(n => n.IsLand()).continent);
	}

	// Bodies of 74 tiles or fewer are left alone. The original compares the
	// body's area against 75, so 74 is the largest body without a guaranteed
	// lake.
	[Fact]
	public void ALandmassOf74TilesIsLeftAlone() {
		WorldCharacteristics wc = MakeWorldCharacteristics(RuleTerrains(), irrigableTerrainKeys: "grassland");
		GameMap m = MakeLandMap(74, Grassland());

		Assert.Equal(74, m.tiles.Count);
		Assert.Equal(0, MapGenerator.AddFreshWaterLakes(wc, m));
		Assert.Equal(0, CountTerrain(m, "coast"));
	}

	[Fact]
	public void ALandmassOf75TilesIsNot() {
		WorldCharacteristics wc = MakeWorldCharacteristics(RuleTerrains(), irrigableTerrainKeys: "grassland");
		GameMap m = MakeLandMap(75, Grassland());

		Assert.Equal(1, MapGenerator.AddFreshWaterLakes(wc, m));
		Assert.Equal(1, CountTerrain(m, "coast"));
	}

	// A landmass that already has fresh water - here a one-tile lake on
	// irrigable ground - is skipped, so it does not collect a second lake.
	[Fact]
	public void ALandmassThatAlreadyHasFreshWaterIsLeftAlone() {
		WorldCharacteristics wc = MakeWorldCharacteristics(RuleTerrains(), irrigableTerrainKeys: "grassland");
		GameMap m = MakeGrasslandMap(lakeKey: "ocean", lakeX: 10, lakeY: 4);

		Assert.True(TheLake(m).isFreshWater, "the pre-existing lake should read as fresh water");

		Assert.Equal(0, MapGenerator.AddFreshWaterLakes(wc, m));
		Assert.Equal(0, CountTerrain(m, "coast"));
	}

	// The "does this landmass already have fresh water" scan wants a lake (or
	// river) on terrain the rules can irrigate. A one-tile lake ringed by hills
	// is not a usable fresh-water source, so the landmass still gets its lake.
	[Fact]
	public void ALakeInTheHillsDoesNotCountAsFreshWater() {
		WorldCharacteristics wc = MakeWorldCharacteristics(RuleTerrains(), irrigableTerrainKeys: "grassland");
		GameMap m = MakeSyntheticMap(20, 8, (x, y) =>
			x == 10 && y == 4 ? Ocean()
			: (Math.Abs(x - 10) <= 2 && Math.Abs(y - 4) <= 2 ? Hills() : Grassland()));
		m.recomputeContinents();

		Assert.Equal(1, MapGenerator.AddFreshWaterLakes(wc, m));

		// The hill-ringed water is still there, and one coast tile was added.
		Assert.Equal(1, CountTerrain(m, "ocean"));
		Assert.Equal(1, CountTerrain(m, "coast"));
	}

	// The strict search only accepts a tile whose nine-tile neighbourhood is
	// irrigable land, so a landmass with exactly one such spot gets its lake
	// there rather than anywhere on the map.
	[Fact]
	public void TheStrictSearchOnlyPicksIrrigableGround() {
		WorldCharacteristics wc = MakeWorldCharacteristics(RuleTerrains(), irrigableTerrainKeys: "grassland");

		// Everything is hills but the three-by-three neighbourhood of (10, 4).
		GameMap m = MakeSyntheticMap(20, 8, (x, y) =>
			Math.Abs(x - 10) <= 2 && Math.Abs(y - 4) <= 2 ? Grassland() : Hills());

		Assert.Equal(1, MapGenerator.AddFreshWaterLakes(wc, m));

		Tile lake = TheLake(m);
		Assert.Contains((lake.XCoordinate, lake.YCoordinate), new (int, int)[] {
			(10, 4), (10, 2), (10, 6), (8, 4), (12, 4), (9, 3), (11, 3), (9, 5), (11, 5),
		});
	}

	// If the landmass has no irrigable ground at all, the relaxed second walk
	// still guarantees it a lake.
	[Fact]
	public void ALandmassWithNoIrrigableGroundStillGetsItsLake() {
		WorldCharacteristics wc = MakeWorldCharacteristics(RuleTerrains(), irrigableTerrainKeys: "grassland");
		GameMap m = MakeSyntheticMap(20, 8, (x, y) => Hills());

		Assert.Equal(1, MapGenerator.AddFreshWaterLakes(wc, m));
		Assert.Equal(1, CountTerrain(m, "coast"));
		Assert.False(TheLake(m).BordersRiver());
	}

	// The pass draws on its own seeded stream, so the same seed puts the lake in
	// the same place and a different seed moves it.
	[Fact]
	public void TheSameSeedSeedsTheSameLake() {
		List<TerrainType> terrains = RuleTerrains();

		GameMap first = MakeGrasslandMap();
		GameMap again = MakeGrasslandMap();
		MapGenerator.AddFreshWaterLakes(MakeWorldCharacteristics(terrains, seed: 99), first);
		MapGenerator.AddFreshWaterLakes(MakeWorldCharacteristics(terrains, seed: 99), again);
		Assert.Equal((TheLake(first).XCoordinate, TheLake(first).YCoordinate),
					 (TheLake(again).XCoordinate, TheLake(again).YCoordinate));

		GameMap other = MakeGrasslandMap();
		MapGenerator.AddFreshWaterLakes(MakeWorldCharacteristics(terrains, seed: 1234567), other);
		Assert.NotEqual((TheLake(first).XCoordinate, TheLake(first).YCoordinate),
						(TheLake(other).XCoordinate, TheLake(other).YCoordinate));
	}

	// ----------------------------------------------------------------- a whole map

	// Every big landmass the generator produces ends up with fresh water on it,
	// which is the guarantee the pass exists to provide. Pangaea at 80% water on
	// this seed has two big landmasses, and neither has any fresh water on it
	// without the pass.
	[Fact]
	public void EveryBigLandmassOfAGeneratedMapHasFreshWater() {
		GameMap m = MapGenerator.GenerateMap(GeneratedWorldCharacteristics(
			WorldCharacteristics.OceanCoverage.Percent_80, seed: 3));

		List<List<Tile>> bigBodies = BigLandBodies(m);
		Assert.Equal(2, bigBodies.Count);

		foreach (List<Tile> body in bigBodies) {
			Assert.True(body.Any(t => t.NeighborsFreshWater()),
						$"a landmass of {body.Count} tiles has no fresh water on it");
		}
	}

	// The pass on a real generated map: one lake per dry big landmass, each lake
	// reading as fresh water in a body of its own, and a city on the shore of one
	// of them growing without an aqueduct.
	[Fact]
	public void ThePassSeedsAFreshWaterLakeIntoEveryDryLandmass() {
		WorldCharacteristics wc = GeneratedWorldCharacteristics(
			WorldCharacteristics.OceanCoverage.Percent_80, seed: 3);
		GameMap m = MapGenerator.GenerateMap(wc);
		ReturnToThePreRiverState(m, wc.terrainTypes.Find(t => t.Key == "grassland"));

		List<List<Tile>> bigBodies = BigLandBodies(m);
		Assert.Equal(2, bigBodies.Count);

		// The state the pass is there for: these landmasses have no fresh water
		// on them at all.
		foreach (List<Tile> body in bigBodies) {
			Assert.False(body.Any(t => t.NeighborsFreshWater()),
						 $"the big landmass of {body.Count} tiles is not dry");
		}

		HashSet<Tile> waterBefore = m.tiles.Where(t => !t.IsLand()).ToHashSet();
		Assert.Equal(bigBodies.Count, MapGenerator.AddFreshWaterLakes(wc, m));

		List<Tile> newLakes = m.tiles.Where(t => !t.IsLand() && !waterBefore.Contains(t)).ToList();
		Assert.Equal(bigBodies.Count, newLakes.Count);

		foreach (List<Tile> body in bigBodies) {
			Assert.True(body.Any(t => t.NeighborsFreshWater()),
						$"a landmass of {body.Count} tiles has no fresh water on it");
		}

		foreach (Tile lake in newLakes) {
			Assert.True(lake.isFreshWater, $"the lake at ({lake.XCoordinate},{lake.YCoordinate}) is not fresh water");
			Assert.Equal("coast", lake.baseTerrainType.Key);
			Assert.Single(m.continents.First(c => c.Contains(lake)));
		}

		// And a city on the shore of one of those lakes gets the growth the
		// game's fresh-water rules hand out.
		C7GameData.GameData gd = CityCaptureTest.Game.NewGameData(out Player player, out _);
		gd.map = m;
		C7Engine.EngineStorage.InitializeGameDataForTests(gd);

		Tile lakeTile = newLakes[0];
		Tile shore = lakeTile.neighbors.Values.First(n => n.IsLand());
		Assert.True(shore.NeighborsFreshWater());
		Assert.Equal(7, GrowToTheFreshWaterLimit(gd, player, shore, plentyOfFood: true).residents.Count);
	}

	private WorldCharacteristics GeneratedWorldCharacteristics(WorldCharacteristics.OceanCoverage oceanCoverage, int seed) {
		return new(saveGameFixture.saveGame) {
			landform = WorldCharacteristics.Landform.Pangaea,
			oceanCoverage = oceanCoverage,
			age = WorldCharacteristics.Age.Billion_4,
			climate = WorldCharacteristics.Climate.Normal,
			temperature = WorldCharacteristics.Temperature.Temperate,
			worldSize = new WorldSize() { width = 100, height = 100 },
			mapSeed = seed,
		};
	}

	// Puts a generated map back into the state the lake pass actually sees. The
	// pass runs before the rivers are laid, so a world that has been generated
	// through to the end is not the world it is handed: its rivers go, and so do
	// the shallow seas, because an inland sea of 20 tiles or fewer already counts
	// as fresh water. What is left has big landmasses and no fresh water on them,
	// which is exactly the case the pass is there for.
	private static void ReturnToThePreRiverState(GameMap m, TerrainType land) {
		foreach (Tile t in m.tiles) {
			t.riverNorth = false;
			t.riverNortheast = false;
			t.riverEast = false;
			t.riverSoutheast = false;
			t.riverSouth = false;
			t.riverSouthwest = false;
			t.riverWest = false;
			t.riverNorthwest = false;

			if (!t.IsLand() && t.isFreshWater) {
				t.baseTerrainType = land;
				t.overlayTerrainType = land;
			}
		}

		m.recomputeContinents();
	}

	private static List<List<Tile>> BigLandBodies(GameMap m) {
		Dictionary<int, List<Tile>> landBodies = new();
		foreach (Tile t in m.tiles.Where(t => t.IsLand())) {
			if (!landBodies.TryGetValue(t.continent, out List<Tile> body)) {
				body = new();
				landBodies[t.continent] = body;
			}
			body.Add(t);
		}

		return landBodies.Values.Where(b => b.Count > MapGenerator.LAKE_BODY_MIN_AREA).ToList();
	}

	// The point of the pass: a city on the lake's shore gets the growth the
	// game's freshwater rules hand out, without an aqueduct.
	[Fact]
	public void ACityOnTheLakesShoreGrowsWithoutAnAqueduct() {
		C7GameData.GameData gd = CityCaptureTest.Game.NewGameData(out Player player, out _);
		List<TerrainType> terrains = RuleTerrains();
		WorldCharacteristics wc = MakeWorldCharacteristics(terrains, irrigableTerrainKeys: "grassland");
		GameMap m = MakeGrasslandMap();
		gd.map = m;
		C7Engine.EngineStorage.InitializeGameDataForTests(gd);

		Assert.Equal(1, MapGenerator.AddFreshWaterLakes(wc, m));
		Tile lake = TheLake(m);
		Tile shore = lake.neighbors.Values.First(n => n.IsLand());
		Assert.True(shore.NeighborsFreshWater());

		// The lake is the only fresh water on the map, so a city elsewhere on
		// the same landmass is the control.
		Tile dryShore = m.tiles.First(t => t.IsLand() && !t.NeighborsFreshWater());

		City onTheLake = GrowToTheFreshWaterLimit(gd, player, shore);
		Assert.Equal(7, onTheLake.residents.Count);

		City inland = GrowToTheFreshWaterLimit(gd, player, dryShore);
		Assert.Equal(6, inland.residents.Count);
	}

	// Builds a size-6 city on `tile` with enough food to grow once, and runs the
	// turn's growth. A size-6 city can only grow with fresh water or an
	// aqueduct, and these cities have neither building. `plentyOfFood` is for
	// maps whose terrain yields this test does not want to depend on; the fresh
	// water gate is what is under test.
	private static City GrowToTheFreshWaterLimit(C7GameData.GameData gd, Player player, Tile tile,
			bool plentyOfFood = false) {
		City city = CityCaptureTest.Game.AddCity(gd, player, tile, population: 6);

		List<Tile> worked = gd.map.tiles.Where(t => t.IsLand() && t != tile).Take(6).ToList();
		Assert.Equal(6, worked.Count);
		foreach (Tile t in worked) {
			t.owningCity = city;
		}
		for (int i = 0; i < city.residents.Count; ++i) {
			city.residents[i].tileWorked = worked[i];
		}

		city.foodStored = plentyOfFood ? 100000 : city.FoodNeededToGrow();
		city.HandleCityGrowth(gd);
		return city;
	}
}
