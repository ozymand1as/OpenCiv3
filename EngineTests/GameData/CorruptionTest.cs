using System;
using System.Collections.Generic;
using System.Linq;
using C7Engine;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using Xunit;

namespace EngineTests.GameData;

/// <summary>
/// Guards the per-city corruption calculation against the Civ3 rules that the
/// engine applies before capping a yield's loss.
///
/// Civ3 scales the distance term by 5/4 only when a city has no trade route to
/// its capital, counts "decorruption points" for corruption-reducing
/// improvements, the capital itself and corruption-reducing small wonders, and
/// caps the loss at max(0, 9 - points) tenths of the yield. These tests drive
/// the shipped ruleset through the real City/Player code rather than a
/// hand-built rule set, so a regression in the rules data or the calculation
/// fails here.
/// </summary>
public class CorruptionTest : IClassFixture<SaveGameFixture> {
	private readonly C7GameData.GameData rules;

	private const int MapSpan = 100;

	// Civ3's distance cap is (mapWidth + mapHeight) / 4.
	private const int MaxDistance = (MapSpan + MapSpan) / 4;

	public CorruptionTest(SaveGameFixture fixture) {
		rules = fixture.saveGame.ToGameData(fixture.behaviors);
	}

	[Fact]
	public void UnconnectedCityHasMoreCorruptionThanATradeConnectedOne() {
		TestEmpire empire = MakeEmpire("Republic", cityX: 28);
		AddRoadPath(empire);

		empire.player.DoCorruptionCalculations(empire.gameData);
		float connected = empire.city.corruption;

		RemoveRoadPath(empire);
		empire.player.DoCorruptionCalculations(empire.gameData);
		float unconnected = empire.city.corruption;

		Assert.True(unconnected > connected,
			$"a city with no trade route to the capital should lose more commerce, but got {unconnected} vs {connected}");
	}

	[Fact]
	public void CapitalIsFreeOfCorruptionBecauseItsDecorruptionPointsCapTheLoss() {
		TestEmpire empire = MakeEmpire("Communism", cityX: 28);
		AddRoadPath(empire);
		empire.player.DoCorruptionCalculations(empire.gameData);

		// Under Communism the capital still has a nonzero rank term, so only
		// the capital's ten decorruption points (which force the cap to zero)
		// can keep it corruption free.
		Assert.Equal(0f, empire.capital.corruption);
	}

	[Fact]
	public void CourthouseAddsAFractionOfTheMapOptimalCityCountToTheOptimalCityNumber() {
		TestEmpire empire = MakeEmpire("Republic", cityX: 28);
		AddRoadPath(empire);
		empire.city.constructed_buildings.Add(new CityBuilding { building = FindBuilding("Courthouse") });
		empire.player.DoCorruptionCalculations(empire.gameData);

		// One decorruption point raises the optimal city number by a quarter of
		// the map's optimal city count, and halves the distance term once.
		int expectedNopt = Math.Max(1, empire.player.GetAdjustedOptimalCityNumber(empire.gameData))
				+ empire.gameData.map.optimalNumberOfCities / 4;
		int clampedDistance = Math.Max(2,
			Math.Min(empire.capitalTile.RankDistanceTo(empire.cityTile), MaxDistance));
		int halvedDistance = (clampedDistance + 1) / 2;
		float expected = (float)halvedDistance / MaxDistance
				+ empire.city.rankIndex / (2f * expectedNopt);

		Assert.Equal(expected, empire.city.corruption, 5);
	}

	[Fact]
	public void CourthouseReducesCorruption() {
		TestEmpire empire = MakeEmpire("Despotism", cityX: 28);
		AddRoadPath(empire);
		empire.player.DoCorruptionCalculations(empire.gameData);
		float withoutCourthouse = empire.city.corruption;

		empire.city.constructed_buildings.Add(new CityBuilding { building = FindBuilding("Courthouse") });
		empire.player.DoCorruptionCalculations(empire.gameData);
		float withCourthouse = empire.city.corruption;

		Assert.True(withCourthouse < withoutCourthouse,
			$"a courthouse should reduce corruption, but got {withCourthouse} vs {withoutCourthouse}");
	}

	private Building FindBuilding(string name) {
		return rules.Buildings.Find(b => b.name == name)
			?? throw new System.InvalidOperationException($"the shipped ruleset has no building named {name}");
	}

	// The controlled empire the corruption tests run against. Distances and the
	// road network are set up by hand so that the distance and rank terms are
	// known.
	private class TestEmpire {
		public C7GameData.GameData gameData;
		public Player player;
		public GameMap map;
		public City capital;
		public City city;
		public Tile capitalTile;
		public Tile cityTile;
		public List<Tile> roadTiles = new();
		// In Civ3 internal movement units, like the shipped ruleset's road.
		public TerrainImprovement road = new("road", TerrainImprovement.Layer.Roads, 1);
	}

	private TestEmpire MakeEmpire(string governmentName, int cityX) {
		GameMap map = new() {
			numTilesWide = MapSpan,
			numTilesTall = MapSpan,
			optimalNumberOfCities = 20,
		};

		C7GameData.GameData gameData = new() {
			map = map,
			gameDifficulty = rules.gameDifficulty,
			Buildings = rules.Buildings,
			governments = rules.governments,
		};

		Player player = new() {
			id = ID.None("corruption-test-player"),
			isHuman = true,
			civilization = new Civilization(),
			government = rules.governments.Find(g => g.name == governmentName),
		};
		gameData.players.Add(player);

		// Tile.cityAtTile and the terrain improvement bookkeeping reach for
		// EngineStorage, so the test game data has to be installed first.
		// Parallelization is disabled repo-wide (XunitSettings.cs).
		EngineStorage.InitializeGameDataForTests(gameData);

		TestEmpire empire = new() { gameData = gameData, player = player, map = map };

		empire.capitalTile = AddTile(map, 20, 20);
		empire.cityTile = AddTile(map, cityX, 20);

		empire.capital = AddCity(player, empire.capitalTile, "Corruption Capital", capital: true);
		empire.city = AddCity(player, empire.cityTile, "Corruption City", capital: false);

		return empire;
	}

	private static Tile AddTile(GameMap map, int x, int y) {
		Tile tile = new(ID.None($"corruption-tile-{x}-{y}")) {
			XCoordinate = x,
			YCoordinate = y,
			map = map,
		};
		map.tiles.Add(tile);
		return tile;
	}

	private static City AddCity(Player player, Tile tile, string name, bool capital) {
		City city = new(tile, player, name, ID.None(name)) { capital = capital };
		player.cities.Add(city);
		tile.cityAtTile = city;
		return city;
	}

	// Runs a road between the capital and the test city, roading every tile on
	// the path, so the trade network flood fill connects them. The tiles are
	// marked known to the player as well: a human's land trade network may only
	// use tiles that player has revealed (27_resources_trade_network.md
	// section 4.4), and a real empire's own city and road tiles are always
	// revealed - a city's tile is where its founding unit stood, and a worker
	// reveals each tile it roads. Without this the fixture would be testing an
	// empire that no game can produce.
	private void AddRoadPath(TestEmpire empire) {
		Tile previous = empire.capitalTile;
		for (int x = empire.capitalTile.XCoordinate + 2; x <= empire.cityTile.XCoordinate; x += 2) {
			Tile next = x == empire.cityTile.XCoordinate
				? empire.cityTile
				: AddTile(empire.map, x, empire.cityTile.YCoordinate);
			previous.neighbors[TileDirection.EAST] = next;
			next.neighbors[TileDirection.WEST] = previous;
			empire.roadTiles.Add(previous);
			previous = next;
		}
		empire.roadTiles.Add(previous);

		foreach (Tile t in empire.roadTiles) {
			t.overlays.Add(empire.road);
			empire.player.tileKnowledge.knownTiles.Add(t);
		}
	}

	private static void RemoveRoadPath(TestEmpire empire) {
		foreach (Tile t in empire.roadTiles) {
			t.overlays.Remove(empire.road);
		}
	}
}
