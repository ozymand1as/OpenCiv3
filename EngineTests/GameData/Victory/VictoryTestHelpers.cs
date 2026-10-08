using System.Collections.Generic;
using C7GameData;

namespace EngineTests.GameData.Victory;

/// Shared builders for the victory-condition tests. Each helper keeps the
/// game's city list and the owner's city list in sync so conditions can be
/// evaluated exactly as they are in a real game.
internal static class VictoryTestHelpers {

	public static Player MakePlayer(string id, string civ, bool barbarian = false, bool defeated = false) {
		return new Player {
			id = ID.FromString(id),
			civilization = new Civilization(civ) { isBarbarian = barbarian },
			defeated = defeated,
		};
	}

	public static C7GameData.GameData MakeGame(int turn = 0) {
		return new C7GameData.GameData {
			turn = turn,
			history = new Dictionary<string, List<HistTurnRecord>>(),
		};
	}

	public static TerrainType Land() => new TerrainType { Key = "grassland" };
	public static TerrainType Coast() => new TerrainType { Key = "coast" };
	public static TerrainType Sea() => new TerrainType { Key = "sea" };

	/// Adds a city of the given population, whose culture for its owner is
	/// `culture`, to both the game and the owner.
	public static City AddCity(C7GameData.GameData game, Player owner, int population, int culture = 0) {
		City city = new City(Tile.NONE, owner, $"{owner.civilization.name} City", ID.None("city"));
		for (int i = 0; i < population; ++i) {
			city.residents.Add(new CityResident { citizenType = new CitizenType { IsDefaultCitizen = true } });
		}
		city.perPlayerCulture[owner] = culture;
		owner.cities.Add(city);
		game.cities.Add(city);
		return city;
	}

	public static Tile AddOwnedTile(C7GameData.GameData game, TerrainType terrain, City city) {
		Tile tile = new Tile(ID.None("tile")) { baseTerrainType = terrain, owningCity = city };
		game.map.tiles.Add(tile);
		return tile;
	}

	public static Tile AddUnownedTile(C7GameData.GameData game, TerrainType terrain) {
		Tile tile = new Tile(ID.None("tile")) { baseTerrainType = terrain };
		game.map.tiles.Add(tile);
		return tile;
	}

	public static void AddHistory(C7GameData.GameData game, Player player, int score) {
		game.history[player.id.ToString()] = new List<HistTurnRecord> { new HistTurnRecord { Score = score } };
	}
}
