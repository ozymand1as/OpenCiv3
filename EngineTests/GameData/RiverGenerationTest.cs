using System.Collections.Generic;
using System.Linq;
using C7Engine;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using Xunit;

namespace EngineTests.GameData;

// The original river pass lays rivers along tile *edges*: it picks the bodies
// it will start a river in by area, probes a tile for a water pattern before
// accepting it as a source, and records every river edge on both of the tiles
// that share it. See re/notes/civ3_map_generator_spec.md section 2.9.
public class RiverGenerationTest : IClassFixture<SaveGameFixture> {
	private readonly SaveGameFixture saveGameFixture;

	public RiverGenerationTest(SaveGameFixture fix) {
		saveGameFixture = fix;
	}

	private static List<TerrainType> RuleTerrains() {
		return new List<TerrainType>() {
			new() { Key = "grassland" },
			new() { Key = "plains" },
			new() { Key = "desert" },
			new() { Key = "flood plain" },
			new() { Key = "tundra" },
			new() { Key = "coast" },
			new() { Key = "sea" },
			new() { Key = "ocean" },
			new() { Key = "forest" },
			new() { Key = "jungle" },
			new() { Key = "marsh" },
			new() { Key = "hills" },
			new() { Key = "volcano" },
			new() { Key = "mountains" },
		};
	}

	private WorldCharacteristics MakeWorldCharacteristics() {
		return new WorldCharacteristics(saveGameFixture.saveGame) {
			landform = WorldCharacteristics.Landform.Pangaea,
			oceanCoverage = WorldCharacteristics.OceanCoverage.Percent_70,
			age = WorldCharacteristics.Age.Billion_4,
			climate = WorldCharacteristics.Climate.Arid,
			temperature = WorldCharacteristics.Temperature.Warm,
			worldSize = new WorldSize() { width = 100, height = 100 },
			mapSeed = 123456,
		};
	}

	private static string RiverLayout(GameMap m) {
		return string.Join("\n", m.tiles
			.OrderBy(t => t.YCoordinate).ThenBy(t => t.XCoordinate)
			.Select(t => $"{t.XCoordinate},{t.YCoordinate}:{t.baseTerrainType.Key}/" +
						 $"{t.overlayTerrainType.Key}:{t.riverNorth}{t.riverNortheast}{t.riverEast}" +
						 $"{t.riverSoutheast}{t.riverSouth}{t.riverSouthwest}{t.riverWest}{t.riverNorthwest}"));
	}

	// The pass draws on its own random numbers, so the same seed has to keep
	// producing the same rivers - and the same map around them, because the
	// stages after the river pass key off the rivers.
	[Fact]
	public void TheSameSeedProducesTheSameRivers() {
		GameMap first = MapGenerator.GenerateMap(MakeWorldCharacteristics());
		GameMap second = MapGenerator.GenerateMap(MakeWorldCharacteristics());

		Assert.Equal(RiverLayout(first), RiverLayout(second));
	}

	// The budget is one river per 750 tiles, capped at 256, and it is the
	// number of rivers the pass may start. It is not a cap on the number of
	// edges a river lays down: that is bounded by the 21-step limit on a run.
	[Theory]
	[InlineData(0, 0)]
	[InlineData(1000, 15)]
	[InlineData(5000, 75)]
	[InlineData(100000, 256)]
	public void TheRiverBudgetIsOnePer750TilesCappedAt256(int tileCount, int expected) {
		Assert.Equal(expected, MapGenerator.RiverBudgetFor(tileCount));
	}

	// Bodies are picked in proportion to their area, and the 36-tile threshold
	// is what earns a body its first river rather than a share of the budget.
	[Fact]
	public void OnlyBodiesLargerThan36TilesEarnTheirOwnRiver() {
		// On a map this size the proportional share of a 37-tile body rounds
		// down to nothing, so the one slot it gets is the threshold's.
		int[] atThreshold = MapGenerator.AllocateRiverSlots(new[] { 4963, 36 }, 5000);
		Assert.Equal(0, atThreshold[1]);

		int[] overThreshold = MapGenerator.AllocateRiverSlots(new[] { 4962, 37 }, 5000);
		Assert.Equal(1, overThreshold[1]);
	}

	[Fact]
	public void TheBudgetIsSharedOutInProportionToBodyArea() {
		int[] slots = MapGenerator.AllocateRiverSlots(new[] { 100, 200, 300 }, 600);

		Assert.True(slots[0] < slots[1], $"the 200-tile body got {slots[1]} and the 100-tile one {slots[0]}");
		Assert.True(slots[1] < slots[2], $"the 300-tile body got {slots[2]} and the 200-tile one {slots[1]}");

		// Every slot in the budget is handed to exactly one body.
		Assert.Equal(MapGenerator.RiverBudgetFor(600), slots.Sum());
	}

	[Fact]
	public void TheRiverPassStartsNoMoreRiversThanItsBudget() {
		WorldCharacteristics wc = MakeWorldCharacteristics();
		GameMap m = MapGenerator.GenerateMap(wc);

		// Re-run the pass on the generated map so the count can be read back.
		foreach (Tile t in m.tiles) {
			t.riverNorth = false;
			t.riverNortheast = false;
			t.riverEast = false;
			t.riverSoutheast = false;
			t.riverSouth = false;
			t.riverSouthwest = false;
			t.riverWest = false;
			t.riverNorthwest = false;
		}

		int rivers = MapGenerator.AddRivers(wc, m);
		int budget = MapGenerator.RiverBudgetFor(m.tiles.Count);

		Assert.True(budget > 0, "the fixture map should be big enough to have a river budget");
		Assert.InRange(rivers, 1, budget);

		// A run is only kept when it reaches the 21-step limit, so the number
		// of tiles a river can mark is bounded even though rivers branch.
		int marked = m.tiles.Count(t => t.BordersRiver());
		Assert.True(marked <= budget * 21, $"{marked} river tiles for {rivers} rivers on a budget of {budget}");
	}

	// The pass records a river on the pair of edges that bound it, on both of
	// the tiles it passes through: a north-east/south-west river sets both of
	// those bits on both tiles. So the four bits always come in their two axis
	// pairs, each marked tile reads the edge back in both directions of its
	// axis, and a marked tile always has a marked neighbour along that axis.
	[Fact]
	public void EveryRiverCrossingComesInItsAxisPair() {
		GameMap m = MapGenerator.GenerateMap(MakeWorldCharacteristics());
		int marked = 0;

		foreach (Tile t in m.tiles) {
			Assert.Equal(t.riverNortheast, t.riverSouthwest);
			Assert.Equal(t.riverSoutheast, t.riverNorthwest);

			if (!t.BordersRiver()) {
				continue;
			}

			++marked;

			if (t.riverNortheast) {
				Assert.True(t.HasRiverOnEdge(TileDirection.NORTHEAST));
				Assert.True(t.HasRiverOnEdge(TileDirection.SOUTHWEST));
				Assert.True(ContinuesAlong(t, TileDirection.NORTHEAST, TileDirection.SOUTHWEST),
							$"the north-east river at ({t.XCoordinate},{t.YCoordinate}) goes nowhere");
			}

			if (t.riverSoutheast) {
				Assert.True(t.HasRiverOnEdge(TileDirection.SOUTHEAST));
				Assert.True(t.HasRiverOnEdge(TileDirection.NORTHWEST));
				Assert.True(ContinuesAlong(t, TileDirection.SOUTHEAST, TileDirection.NORTHWEST),
							$"the south-east river at ({t.XCoordinate},{t.YCoordinate}) goes nowhere");
			}
		}

		Assert.True(marked > 0, "the fixture map should have rivers");
	}

	private static bool ContinuesAlong(Tile t, TileDirection forward, TileDirection backward) {
		foreach (TileDirection dir in new[] { forward, backward }) {
			if (t.neighbors.TryGetValue(dir, out Tile n) && n != Tile.NONE && n.BordersRiver()) {
				return true;
			}
		}
		return false;
	}

	[Fact]
	public void RiversAreOnlyEverRecordedOnLand() {
		GameMap m = MapGenerator.GenerateMap(MakeWorldCharacteristics());

		foreach (Tile t in m.tiles) {
			if (t.BordersRiver()) {
				Assert.True(t.IsLand(), $"({t.XCoordinate},{t.YCoordinate}) is water but carries a river");
			}
		}
	}

	// A 60x60 map that is land above row 30 and ocean below it has exactly one
	// tile with the water pattern the pass looks for, at (0,30). From there
	// only one continuation of each step keeps clear of the river already
	// laid, so the river is forced: a 21-step staircase north-east to (21,9),
	// each tile marked on its north-east and south-west edges.
	[Fact]
	public void ASyntheticCoastlineCarriesTheRiverThePassDerives() {
		List<TerrainType> terrains = RuleTerrains();
		GameMap m = MakeSyntheticMap(60, 60, terrains, (x, y) => y <= 30);

		WorldCharacteristics wc = new() {
			landform = WorldCharacteristics.Landform.Pangaea,
			oceanCoverage = WorldCharacteristics.OceanCoverage.Percent_70,
			age = WorldCharacteristics.Age.Billion_4,
			climate = WorldCharacteristics.Climate.Normal,
			temperature = WorldCharacteristics.Temperature.Temperate,
			worldSize = new WorldSize() { width = 60, height = 60 },
			terrainTypes = terrains,
			mapSeed = 7,
		};

		Assert.Equal(1, MapGenerator.AddRivers(wc, m));

		List<(int x, int y)> marked = m.tiles.Where(t => t.BordersRiver())
			.Select(t => (t.XCoordinate, t.YCoordinate))
			.OrderBy(p => p.YCoordinate).ToList();

		List<(int x, int y)> expected = Enumerable.Range(0, 22).Select(i => (21 - i, 9 + i)).ToList();
		Assert.Equal(expected, marked);

		foreach (Tile t in m.tiles.Where(t => t.BordersRiver())) {
			Assert.True(t.riverNortheast, $"({t.XCoordinate},{t.YCoordinate}) is missing its north-east edge");
			Assert.True(t.riverSouthwest, $"({t.XCoordinate},{t.YCoordinate}) is missing its south-west edge");
			Assert.False(t.riverNorth);
			Assert.False(t.riverEast);
			Assert.False(t.riverSoutheast);
			Assert.False(t.riverSouth);
			Assert.False(t.riverWest);
			Assert.False(t.riverNorthwest);
		}
	}

	// Builds a map with the tile ordering the generator uses, so that
	// GameMap.tileAt resolves coordinates the same way it does on a generated
	// map.
	private static GameMap MakeSyntheticMap(int width, int height, List<TerrainType> terrains,
											System.Func<int, int, bool> isLand) {
		GameMap m = new GameMap() {
			tiles = new List<Tile>(),
			numTilesWide = width,
			numTilesTall = height,
		};

		TerrainType land = terrains.Find(x => x.Key == "grassland");
		TerrainType water = terrains.Find(x => x.Key == "ocean");
		ID.Factory factory = new();

		for (int y = 0; y < height; ++y) {
			for (int x = y % 2; x < width; x += 2) {
				Tile t = new Tile(factory.CreateID("tile"));
				t.XCoordinate = x;
				t.YCoordinate = y;
				t.baseTerrainType = isLand(x, y) ? land : water;
				t.overlayTerrainType = t.baseTerrainType;
				m.tiles.Add(t);
			}
		}

		m.computeNeighbors();
		m.recomputeContinents();
		return m;
	}
}
