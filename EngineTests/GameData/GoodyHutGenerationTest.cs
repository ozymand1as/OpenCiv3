using System;
using System.Collections.Generic;
using System.Linq;
using C7Engine;
using C7GameData;
using Xunit;

namespace EngineTests.GameData;

/// <summary>
/// Covers goody-hut placement in the map generator: huts exist, they obey the
/// placement predicate, and none are placed when barbarians are disabled.
/// </summary>
public class GoodyHutGenerationTest {
	private static List<TerrainType> TerrainTypes() {
		List<TerrainType> terrainTypes = new();
		terrainTypes.Add(new TerrainType() { Key = "grassland", movementCost = 1, allowCities = true });
		terrainTypes.Add(new TerrainType() { Key = "plains", movementCost = 1, allowCities = true });
		terrainTypes.Add(new TerrainType() { Key = "desert", movementCost = 1, allowCities = true });
		terrainTypes.Add(new TerrainType() { Key = "tundra", movementCost = 1, allowCities = true });
		terrainTypes.Add(new TerrainType() { Key = "coast", movementCost = 1, allowCities = false });
		terrainTypes.Add(new TerrainType() { Key = "sea", movementCost = 1, allowCities = false });
		terrainTypes.Add(new TerrainType() { Key = "ocean", movementCost = 1, allowCities = false });
		terrainTypes.Add(new TerrainType() { Key = "forest", movementCost = 2, allowCities = true });
		terrainTypes.Add(new TerrainType() { Key = "jungle", movementCost = 2, allowCities = true });
		terrainTypes.Add(new TerrainType() { Key = "marsh", movementCost = 2, allowCities = true });
		terrainTypes.Add(new TerrainType() { Key = "hills", movementCost = 2, allowCities = true });
		terrainTypes.Add(new TerrainType() { Key = "volcano", movementCost = 3, allowCities = false });
		terrainTypes.Add(new TerrainType() { Key = "mountains", movementCost = 3, allowCities = false });
		return terrainTypes;
	}

	private static GameMap Generate(BarbarianActivity activity) {
		return MapGenerator.GenerateMap(new WorldCharacteristics() {
			landform = WorldCharacteristics.Landform.Pangaea,
			oceanCoverage = WorldCharacteristics.OceanCoverage.Percent_70,
			age = WorldCharacteristics.Age.Billion_4,
			climate = WorldCharacteristics.Climate.Normal,
			temperature = WorldCharacteristics.Temperature.Temperate,
			barbarianActivity = activity,
			worldSize = new WorldSize() { width = 64, height = 64, numberOfCivs = 4, distanceBetweenCivs = 12 },
			terrainTypes = TerrainTypes(),
			mapSeed = 20260101,
		});
	}

	[Fact]
	public void HutsArePlacedAndObeyThePlacementRules() {
		GameMap map = Generate(BarbarianActivity.Roaming);
		List<Tile> huts = map.tiles.Where(t => t.hasGoodyHut).ToList();

		// The pass makes tileCount >> 5 = 128 attempts on a 64x64 map, so
		// some huts must land.
		Assert.NotEmpty(huts);

		foreach (Tile hut in huts) {
			Assert.True(hut.IsLand(), $"{hut} should be land");
			Assert.False(hut.hasBarbarianCamp, $"{hut} should not be a barbarian camp");
			Assert.False(hut.HasCity(), $"{hut} should not hold a city");
			Assert.Equal(0, hut.unitsOnTile.Count);
			Assert.True(hut.Resource == null || hut.Resource == Resource.NONE, $"{hut} should have no resource");
			Assert.DoesNotContain(hut, map.startingLocations);
		}

		// No two huts inside the same 7x7 Chebyshev block: at least four tiles
		// apart in both wrapped axes.
		for (int i = 0; i < huts.Count; ++i) {
			for (int k = i + 1; k < huts.Count; ++k) {
				int dx = Math.Abs(map.CalculateXDelta(huts[i].XCoordinate, huts[k].XCoordinate));
				int dy = Math.Abs(map.CalculateYDelta(huts[i].YCoordinate, huts[k].YCoordinate));
				Assert.True(dx > 3 || dy > 3, $"{huts[i]} and {huts[k]} are too close");
			}
		}
	}

	[Fact]
	public void NoBarbariansMeansNoHuts() {
		GameMap map = Generate(BarbarianActivity.None);

		Assert.Empty(map.tiles.Where(t => t.hasGoodyHut));
	}

	[Fact]
	public void SedentaryBarbariansStillGetHuts() {
		GameMap map = Generate(BarbarianActivity.Sedentary);

		Assert.NotEmpty(map.tiles.Where(t => t.hasGoodyHut));
	}

	[Fact]
	public void PlacementIsDeterministicForASeed() {
		GameMap first = Generate(BarbarianActivity.Roaming);
		GameMap second = Generate(BarbarianActivity.Roaming);

		List<(int, int)> firstHuts = first.tiles.Where(t => t.hasGoodyHut).Select(t => (t.XCoordinate, t.YCoordinate)).OrderBy(t => t).ToList();
		List<(int, int)> secondHuts = second.tiles.Where(t => t.hasGoodyHut).Select(t => (t.XCoordinate, t.YCoordinate)).OrderBy(t => t).ToList();

		Assert.NotEmpty(firstHuts);
		Assert.Equal(firstHuts, secondHuts);
	}
}
