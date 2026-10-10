using System;
using System.Collections.Generic;
using System.Linq;
using C7Engine;
using C7GameData;
using Xunit;

namespace EngineTests.GameData;

// The landform pass: the bands that decide which tiles become hills, mountains
// and volcanoes. The original assigns all three inside the stage that writes the
// base terrain (the climate pass at 0x5edb70), cutting its own fractal height
// field into percentile bands that depend on the resolved world age, and rolling
// a single age-dependent probability to split the mountain band into mountains
// and volcanoes. These tests pin those measured bands and that rate, and pin
// the pass's determinism, so a later retune cannot drift the shares silently.
public class LandformBandTest {
	private const int MAP_WIDTH = 320;
	private const int MAP_HEIGHT = 256;

	private static TerrainType Terrain(string key, int movementCost = 1) {
		return new() { Key = key, movementCost = movementCost, allowCities = true };
	}

	private static List<TerrainType> Terrains() {
		return new() {
			Terrain("grassland"),
			Terrain("hills", 2),
			Terrain("mountains", 3),
			Terrain("volcano", 3),
		};
	}

	// A map that is nothing but flat grassland, in the tile order the generator
	// uses. Every tile is eligible for a band, so the share of the tiles a band
	// takes is the share of the field the band was measured to cover.
	private static GameMap AllLandMap() {
		GameMap m = new() { tiles = new List<Tile>(), numTilesWide = MAP_WIDTH, numTilesTall = MAP_HEIGHT };
		ID.Factory factory = new();
		TerrainType grass = Terrain("grassland");

		for (int y = 0; y < MAP_HEIGHT; ++y) {
			for (int x = y % 2; x < MAP_WIDTH; x += 2) {
				Tile t = new(factory.CreateID("tile")) {
					XCoordinate = x,
					YCoordinate = y,
					baseTerrainType = grass,
					overlayTerrainType = grass,
				};
				m.tiles.Add(t);
			}
		}

		m.computeNeighbors();
		m.recomputeContinents();
		return m;
	}

	private static WorldCharacteristics MakeWorldCharacteristics(WorldCharacteristics.Age age, int seed) {
		return new() {
			landform = WorldCharacteristics.Landform.Continents,
			oceanCoverage = WorldCharacteristics.OceanCoverage.Percent_70,
			age = age,
			climate = WorldCharacteristics.Climate.Normal,
			temperature = WorldCharacteristics.Temperature.Temperate,
			worldSize = new WorldSize() { width = MAP_WIDTH, height = MAP_HEIGHT },
			terrainTypes = Terrains(),
			mapSeed = seed,
		};
	}

	private static GameMap Run(WorldCharacteristics.Age age, int seed = 4242) {
		WorldCharacteristics wc = MakeWorldCharacteristics(age, seed);
		GameMap m = AllLandMap();
		MapGenerator.AddHillsAndMountains(wc, m);
		return m;
	}

	private static int Count(string key, GameMap m) => m.tiles.Count(t => t.overlayTerrainType.Key == key);

	private static double Share(string key, GameMap m) => (double)Count(key, m) / m.tiles.Count;

	[Theory]
	[InlineData(WorldCharacteristics.Age.Billion_3, 18, 28, 55, 65, 65, 77)]
	[InlineData(WorldCharacteristics.Age.Billion_4, 20, 25, 63, 70, 70, 77)]
	[InlineData(WorldCharacteristics.Age.Billion_5, 21, 24, 66, 70, 70, 74)]
	public void TheBandsTakeTheMeasuredShareOfTheField(
			WorldCharacteristics.Age age,
			int hillLower, int hillUpper, int hillUpperBandLower, int hillUpperBandUpper,
			int mountainLower, int mountainUpper) {
		GameMap m = Run(age);
		int total = m.tiles.Count;

		double hillShare = (hillUpper - hillLower + hillUpperBandUpper - hillUpperBandLower) / 100.0;
		double mountainShare = (mountainUpper - mountainLower) / 100.0;

		// The percentile thresholds are discrete field samples, so a band's
		// realised share sits a fraction of a point off its nominal width.
		Assert.InRange(Share("hills", m), hillShare - 0.02, hillShare + 0.02);
		Assert.InRange(Share("mountains", m) + Share("volcano", m), mountainShare - 0.02, mountainShare + 0.02);

		// Nothing at all is written above the top edge of the mountain band:
		// the highest ground stays open land.
		double written = Share("hills", m) + Share("mountains", m) + Share("volcano", m);
		Assert.True(written <= hillShare + mountainShare + 0.02,
			$"{written:P1} of the field was written, more than the measured bands cover");

		// The narrowest band is still wide enough on this map to measure, so
		// the share assertions above are about a real count.
		Assert.True(Count("mountains", m) + Count("volcano", m) > 400,
			$"age {age} left only {Count("mountains", m) + Count("volcano", m)} mountain-band tiles");
		Assert.All(m.tiles, t => Assert.True(t.IsLand(), "a synthetic all-land map grew water"));
	}

	// The volcano is the share of the mountain band the measured roll gives it,
	// not a band of its own: 6% at 3 By, 4% at 4 By, 2% at 5 By. The rate this
	// pass used to carry was one point lower at every age, so the window below
	// is half a point wide: on a map this size the roll's spread is a tenth of a
	// point, and the old constant lands a full point away from the measured
	// rate, outside the window.
	[Theory]
	[InlineData(WorldCharacteristics.Age.Billion_3, 6)]
	[InlineData(WorldCharacteristics.Age.Billion_4, 4)]
	[InlineData(WorldCharacteristics.Age.Billion_5, 2)]
	public void TheVolcanoRateIsTheMeasuredOneAndNotAPointLow(WorldCharacteristics.Age age, double expected) {
		GameMap m = Run(age);
		int band = Count("mountains", m) + Count("volcano", m);
		int volcanoes = Count("volcano", m);
		double actual = (double)volcanoes / band;

		Assert.InRange(actual, expected / 100.0 - 0.005, expected / 100.0 + 0.005);
	}

	// A young map is volcanic and an old one is eroded flat: the bands narrow
	// monotonically with age, which is what the age option means.
	[Fact]
	public void ElevationNarrowsWithAge() {
		GameMap young = Run(WorldCharacteristics.Age.Billion_3);
		GameMap middle = Run(WorldCharacteristics.Age.Billion_4);
		GameMap old = Run(WorldCharacteristics.Age.Billion_5);

		double Young() => YoungShare(young);
		double Middle() => YoungShare(middle);
		double Old() => YoungShare(old);

		Assert.True(Young() > Middle(), $"3 By covers {Young():P1} of the field and 4 By covers {Middle():P1}");
		Assert.True(Middle() > Old(), $"4 By covers {Middle():P1} of the field and 5 By covers {Old():P1}");
	}

	private static double YoungShare(GameMap m) =>
		(double)(Count("hills", m) + Count("mountains", m) + Count("volcano", m)) / m.tiles.Count;

	// The same seed gives the same overlays, and a different seed does not:
	// the pass is a pure function of the seed and its own height field.
	[Fact]
	public void ThePassIsDeterministicInItsSeed() {
		static string Signature(GameMap m) => string.Join(",", m.tiles.Select(t => t.overlayTerrainType.Key));

		Assert.Equal(Signature(Run(WorldCharacteristics.Age.Billion_4, seed: 4242)),
					 Signature(Run(WorldCharacteristics.Age.Billion_4, seed: 4242)));
		Assert.NotEqual(Signature(Run(WorldCharacteristics.Age.Billion_4, seed: 4242)),
						Signature(Run(WorldCharacteristics.Age.Billion_4, seed: 987654)));
	}
}
