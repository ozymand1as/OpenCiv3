using System;
using System.Collections.Generic;
using System.Linq;
using C7Engine;
using C7GameData;
using Xunit;

namespace EngineTests.GameData;

// The dense-feature pass, which this fork calls `AddBonusGrasslands`: it marks
// the tile flag the resource candidacy predicate refuses (the "potential shield
// bonus"), which on a grassland tile shows up as an extra shield. The original's
// version of the pass (0x5f2090) runs after the resource pass, asks for one
// terrain class and rolls a one-in-three chance on the tiles that carry no
// resource. The pass used to roll a one-in-four chance here, which is a quarter
// fewer marked tiles than the original marks; these tests pin the measured rate,
// the fork's narrower eligibility rule, and the pass's determinism.
public class DenseFeaturePassTest {
	private static TerrainType Terrain(string key, int movementCost = 1) {
		return new() { Key = key, movementCost = movementCost, allowCities = true };
	}

	private static List<TerrainType> Terrains() {
		return new() {
			Terrain("grassland"),
			Terrain("plains"),
			Terrain("hills", 2),
			Terrain("forest", 2),
			Terrain("coast"),
		};
	}

	// A sheet of open grassland, with a few hills, forest and plains tiles to
	// show which of them the fork's rule keeps out. The terrain list is the one
	// the world characteristics carry, so a permission granted to a resource is
	// the permission the pass reads.
	private static GameMap MixedMap(List<TerrainType> terrains, int width = 300, int height = 200) {
		GameMap m = new() { tiles = new List<Tile>(), numTilesWide = width, numTilesTall = height };
		ID.Factory factory = new();

		for (int y = 0; y < height; ++y) {
			for (int x = y % 2; x < width; x += 2) {
				// Every seventh row is a hill band, every eleventh a forest
				// band, and every ninety-seventh column is plains: enough of each
				// to measure, none of them enough to dominate.
				TerrainType baseTerrain = y % 7 == 0 ? terrains[2] : (y % 11 == 0 ? terrains[3] : terrains[0]);
				if (x % 97 == 0) {
					baseTerrain = terrains[1];
				}

				Tile t = new(factory.CreateID("tile")) {
					XCoordinate = x,
					YCoordinate = y,
					baseTerrainType = baseTerrain,
					overlayTerrainType = baseTerrain,
				};
				m.tiles.Add(t);
			}
		}

		m.computeNeighbors();
		m.recomputeContinents();
		return m;
	}

	private static WorldCharacteristics MakeWorldCharacteristics(List<TerrainType> terrains, int seed, List<Resource> resources = null) {
		return new() {
			landform = WorldCharacteristics.Landform.Continents,
			oceanCoverage = WorldCharacteristics.OceanCoverage.Percent_70,
			age = WorldCharacteristics.Age.Billion_4,
			climate = WorldCharacteristics.Climate.Normal,
			temperature = WorldCharacteristics.Temperature.Temperate,
			worldSize = new WorldSize() { width = 300, height = 200, numberOfCivs = 8, distanceBetweenCivs = 12 },
			terrainTypes = terrains,
			resources = resources ?? new List<Resource>(),
			maxRankOfWorkableTiles = 2,
			maxRankOfBarbarianCampTiles = 2,
			mapSeed = seed,
		};
	}

	private static int Eligible(GameMap m) => m.tiles.Count(
		t => t.baseTerrainType.Key == "grassland" && !t.overlayTerrainType.isHilly());

	// The measured rate: one eligible tile in three carries the mark. The pass
	// used to roll a one-in-four chance, which on a sheet this size is a
	// difference of thousands of tiles and many tens of standard deviations, so
	// the old rate cannot land inside the window.
	[Fact]
	public void TheMarkedShareOfEligibleGrasslandIsOneInThree() {
		List<TerrainType> terrains = Terrains();
		GameMap m = MixedMap(terrains);
		int eligible = Eligible(m);
		MapGenerator.AddBonusGrasslands(MakeWorldCharacteristics(terrains, seed: 4242), m);

		int marked = m.tiles.Count(t => t.isBonusShield);
		double share = (double)marked / eligible;

		Assert.True(eligible > 10000, $"the fixture map only has {eligible} eligible tiles");
		Assert.InRange(share, 1.0 / 3.0 - 0.01, 1.0 / 3.0 + 0.01);
	}

	// The original's eligible set is the open-ground terrain class, which covers
	// forest and jungle as well as grassland. This fork keeps its own narrower
	// rule - grassland with no hill or mountain over it - because a mark on any
	// other terrain reads nowhere in this engine: the extra shield is only
	// applied to a grassland overlay. So the marked set must stay inside the
	// grassland set, and must leave hilly tiles alone, whatever the roll does.
	[Fact]
	public void TheMarkOnlyEverLandsOnFlatGrassland() {
		List<TerrainType> terrains = Terrains();
		GameMap m = MixedMap(terrains);
		MapGenerator.AddBonusGrasslands(MakeWorldCharacteristics(terrains, seed: 4242), m);

		Assert.Contains(m.tiles, t => t.isBonusShield);
		Assert.All(m.tiles.Where(t => t.isBonusShield), t => {
			Assert.Equal("grassland", t.baseTerrainType.Key);
			Assert.False(t.overlayTerrainType.isHilly(), $"the marked tile at ({t.XCoordinate},{t.YCoordinate}) is hilly");
		});

		// A hill band is never marked even though its underlying terrain is
		// grassland.
		Assert.DoesNotContain(m.tiles, t => t.isBonusShield && t.overlayTerrainType.Key == "hills");
	}

	// A tile the resource pass has already taken keeps its mark off: the pass
	// skips a tile that carries a resource, exactly as the original's dense pass
	// does. That refusal is also the one the resource candidacy predicate carries
	// in the other direction, so the two passes can never leave a tile with both.
	[Fact]
	public void AResourceKeepsTheMarkOffItsTile() {
		List<TerrainType> terrains = Terrains();
		Resource wheat = new() {
			Key = "Wheat",
			Name = "Wheat",
			Category = ResourceCategory.BONUS,
			AppearanceRatio = 1000,
		};
		terrains[0].allowedResources.Add(wheat.Key);

		GameMap m = MixedMap(terrains);
		// Every fiftieth grassland tile already holds the resource, in the same
		// way the resource pass leaves a map.
		List<Tile> taken = m.tiles.Where(t => t.overlayTerrainType.Key == "grassland")
								  .Where((t, i) => i % 50 == 0)
								  .ToList();
		foreach (Tile t in taken) {
			t.Resource = wheat;
		}

		WorldCharacteristics wc = MakeWorldCharacteristics(terrains, seed: 4242, new List<Resource>() { wheat });
		MapGenerator.AddBonusGrasslands(wc, m);

		Assert.NotEmpty(taken);
		Assert.DoesNotContain(m.tiles, t => t.isBonusShield && t.Resource != null && t.Resource != Resource.NONE);
	}

	// Same seed, same marks; a different seed, different marks. The pass is a
	// pure function of the seed, like every other pass of the generator.
	[Fact]
	public void ThePassIsDeterministicInItsSeed() {
		static string Signature(GameMap m) => string.Join(",", m.tiles.Select(t => t.isBonusShield ? "1" : "0"));

		List<TerrainType> terrains = Terrains();
		GameMap first = MixedMap(terrains);
		MapGenerator.AddBonusGrasslands(MakeWorldCharacteristics(terrains, seed: 4242), first);

		List<TerrainType> againTerrains = Terrains();
		GameMap again = MixedMap(againTerrains);
		MapGenerator.AddBonusGrasslands(MakeWorldCharacteristics(againTerrains, seed: 4242), again);

		List<TerrainType> otherTerrains = Terrains();
		GameMap other = MixedMap(otherTerrains);
		MapGenerator.AddBonusGrasslands(MakeWorldCharacteristics(otherTerrains, seed: 987654), other);

		Assert.Equal(Signature(first), Signature(again));
		Assert.NotEqual(Signature(first), Signature(other));
	}
}
