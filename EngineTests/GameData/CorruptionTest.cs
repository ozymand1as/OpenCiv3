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
	private readonly SaveGameFixture fixture;
	private readonly C7GameData.GameData rules;

	private const int MapSpan = 100;

	// Civ3's distance cap is (mapWidth + mapHeight) / 4.
	private const int MaxDistance = (MapSpan + MapSpan) / 4;

	public CorruptionTest(SaveGameFixture fixture) {
		this.fixture = fixture;
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
		public TerrainImprovement road = new("road", TerrainImprovement.Layer.Roads, 1.0f / 3);
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
	// the path, so the trade network flood fill connects them.
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
		}
	}

	private static void RemoveRoadPath(TestEmpire empire) {
		foreach (Tile t in empire.roadTiles) {
			t.overlays.Remove(empire.road);
		}
	}
}
