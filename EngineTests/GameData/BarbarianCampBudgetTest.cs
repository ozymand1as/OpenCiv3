using System;
using System.Collections.Generic;
using System.Linq;
using C7Engine;
using C7GameData;
using Xunit;

namespace EngineTests.GameData;

// The barbarian-camp budget of the generator. The original's map generator
// creates no camps at all - its camp-placing routine (0x55f9f0) is called only
// from the per-turn barbarian phase (0x560833) and the era-change reseed
// (0x55fd72), never from the generator driver, and it needs a city on the
// candidate's continent, which a fresh map does not have. The sparse-feature
// pass that this gap note used to name as the camp candidate (0x5f21b0) is the
// goody-hut pass: it asks the world predicate at vtable slot 0x48 (the hut
// predicate 0x5f2c60) and writes the hut bit, while the camp predicate 0x5f2df0
// is its neighbour at slot 0x40 and is called from the camp routine alone.
//
// So the budget below is a fork tuning curve with no Civ3 counterpart, kept
// because this engine has no in-play pass that would create camps during play.
// These tests pin what the curve currently says, so a future change - closing
// the gap towards the original by dropping camps from generated maps, or
// growing the in-play pass - has to know exactly what it is replacing.
public class BarbarianCampBudgetTest {
	private const int WIDTH = 64;
	private const int HEIGHT = 64;

	private static List<TerrainType> TerrainTypes() {
		return new List<TerrainType> {
			new() { Key = "grassland", movementCost = 1, allowCities = true },
			new() { Key = "plains", movementCost = 1, allowCities = true },
			new() { Key = "desert", movementCost = 1, allowCities = true },
			new() { Key = "tundra", movementCost = 1, allowCities = true },
			new() { Key = "coast", movementCost = 1, allowCities = false },
			new() { Key = "sea", movementCost = 1, allowCities = false },
			new() { Key = "ocean", movementCost = 1, allowCities = false },
			new() { Key = "forest", movementCost = 2, allowCities = true },
			new() { Key = "jungle", movementCost = 2, allowCities = true },
			new() { Key = "marsh", movementCost = 2, allowCities = true },
			new() { Key = "hills", movementCost = 2, allowCities = true },
			new() { Key = "volcano", movementCost = 3, allowCities = false },
			new() { Key = "mountains", movementCost = 3, allowCities = false },
		};
	}

	private static WorldCharacteristics MakeWorldCharacteristics(BarbarianActivity activity, int seed = 20260101) {
		return new WorldCharacteristics() {
			landform = WorldCharacteristics.Landform.Pangaea,
			oceanCoverage = WorldCharacteristics.OceanCoverage.Percent_70,
			age = WorldCharacteristics.Age.Billion_4,
			climate = WorldCharacteristics.Climate.Normal,
			temperature = WorldCharacteristics.Temperature.Temperate,
			barbarianActivity = activity,
			worldSize = new WorldSize() { width = WIDTH, height = HEIGHT, numberOfCivs = 4, distanceBetweenCivs = 12 },
			terrainTypes = TerrainTypes(),
			mapSeed = seed,
		};
	}

	private static GameMap Generate(BarbarianActivity activity) =>
		MapGenerator.GenerateMap(MakeWorldCharacteristics(activity));

	// The budget is one camp per hundred land tiles, scaled by the activity
	// level. Nothing is claimed here about Civ3: the original has no such
	// number. What is claimed is that this curve is what a generated map
	// carries, so a change to it is visible.
	[Theory]
	[InlineData(BarbarianActivity.Sedentary, 1.0)]
	[InlineData(BarbarianActivity.Roaming, 1.0)]
	[InlineData(BarbarianActivity.Restless, 1.25)]
	[InlineData(BarbarianActivity.Raging, 1.50)]
	public void TheBudgetScalesWithTheActivityLevel(BarbarianActivity activity, double multiplier) {
		GameMap m = Generate(activity);
		int landTiles = m.tiles.Count(t => t.IsLand());
		int budget = (int)Math.Round(landTiles / 100.0 * multiplier);

		Assert.Equal(budget, m.barbarianCamps.Count);
		Assert.All(m.barbarianCamps, c => Assert.True(c.hasBarbarianCamp, "a camp in the list is not marked on its tile"));
	}

	// No barbarians, no camps, whatever the map.
	[Fact]
	public void NoBarbariansMeansNoCamps() {
		GameMap m = Generate(BarbarianActivity.None);

		Assert.Empty(m.barbarianCamps);
		Assert.DoesNotContain(m.tiles, t => t.hasBarbarianCamp);
	}

	// The pass stops at its budget even when the map still offers candidates,
	// and it never exceeds it.
	[Fact]
	public void ThePassNeverPlacesMoreThanItsBudget() {
		foreach (BarbarianActivity activity in new[] { BarbarianActivity.Roaming, BarbarianActivity.Raging }) {
			GameMap m = Generate(activity);
			int landTiles = m.tiles.Count(t => t.IsLand());
			int budget = (int)Math.Round(landTiles / 100.0 * (activity == BarbarianActivity.Raging ? 1.5 : 1.0));

			Assert.True(m.barbarianCamps.Count <= budget,
				$"{activity} placed {m.barbarianCamps.Count} camps on a budget of {budget}");
			Assert.Equal(m.barbarianCamps.Count, m.tiles.Count(t => t.hasBarbarianCamp));
		}
	}

	// The pass runs after the starting locations, so a camp is never closer to
	// a start than the pass' own minimum and a start never screens one off -
	// the ordering this fork chose, and the one the gap note records as the
	// reason the original's camp screen cannot fire here.
	[Fact]
	public void CampsArePlacedAfterTheStarts() {
		GameMap m = Generate(BarbarianActivity.Raging);

		Assert.NotEmpty(m.startingLocations);
		Assert.NotEmpty(m.barbarianCamps);
		foreach (Tile c in m.barbarianCamps) {
			Assert.False(m.startingLocations.Contains(c),
				$"a camp was placed on the starting location at ({c.XCoordinate},{c.YCoordinate})");
			foreach (Tile s in m.startingLocations) {
				Assert.True(c.DistanceTo(s) >= 6,
					$"the camp at ({c.XCoordinate},{c.YCoordinate}) is inside six tiles of a start");
			}
		}
	}

	// The pass is a pure function of the seed.
	[Fact]
	public void ThePassIsDeterministicInItsSeed() {
		static string Signature(GameMap m) => string.Join(",", m.barbarianCamps.Select(c => $"{c.XCoordinate},{c.YCoordinate}"));

		Assert.Equal(Signature(Generate(BarbarianActivity.Roaming)), Signature(Generate(BarbarianActivity.Roaming)));
	}
}
