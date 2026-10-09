using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using C7Engine;
using C7Engine.Pathing;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using Xunit;

namespace EngineTests.AI.Pathing;

// Tests for the three passes of the trade network: land (roads), air
// (airports) and water (harbours plus the Astronomy/Navigation/Magnetism and
// Great Lighthouse gates).
//
// The land pass's revealed-tile requirement is asymmetric on purpose: a human
// player's network may only use tiles that player has revealed, while an AI's
// ignores the requirement (27_resources_trade_network.md section 4.4, and the
// open question of section 8.1).
public class TradeNetworkTest {
	private static int cityCounter = 0;

	private static TerrainType Land() {
		return new TerrainType() { Key = "grassland" };
	}

	private static TerrainType Water(string key) {
		return new TerrainType() { Key = key };
	}

	private static Tile MakeTile(TerrainType terrain) {
		return new Tile(ID.None("tile")) {
			baseTerrainType = terrain,
			overlayTerrainType = terrain,
		};
	}

	// Connects two tiles in a straight line, so a flood fill or pathfind can
	// walk from one to the other.
	private static void Connect(Tile from, Tile to, TileDirection direction) {
		from.neighbors[direction] = to;
		to.neighbors[Opposite(direction)] = from;
	}

	private static TileDirection Opposite(TileDirection direction) {
		return direction switch {
			TileDirection.EAST => TileDirection.WEST,
			TileDirection.WEST => TileDirection.EAST,
			TileDirection.NORTH => TileDirection.SOUTH,
			TileDirection.SOUTH => TileDirection.NORTH,
			TileDirection.NORTHEAST => TileDirection.SOUTHWEST,
			TileDirection.SOUTHWEST => TileDirection.NORTHEAST,
			TileDirection.NORTHWEST => TileDirection.SOUTHEAST,
			TileDirection.SOUTHEAST => TileDirection.NORTHWEST,
			_ => TileDirection.EAST,
		};
	}

	private static Player MakePlayer(C7GameData.GameData gameData, string id) {
		Player player = new() {
			id = ID.FromString(id),
			civilization = new Civilization(),
			government = new Government(),
			rules = new Rules(),
		};
		gameData.players.Add(player);
		return player;
	}

	private static City MakeCity(C7GameData.GameData gameData, Player owner, Tile tile, string name) {
		City city = new City(tile, owner, name, ID.FromString($"city-{++cityCounter}"));
		tile.cityAtTile = city;
		owner.cities.Add(city);
		gameData.cities.Add(city);
		return city;
	}

	private static Building MakeBuilding(C7GameData.GameData gameData, string name, params SaveBuilding.Flag[] flags) {
		SaveBuilding building = new() { name = name };
		foreach (SaveBuilding.Flag flag in flags) {
			building.flags.Add(flag);
		}
		return new Building(building, gameData);
	}

	private static Building MakeGreatWonder(C7GameData.GameData gameData, string name, params SaveBuilding.Flag[] flags) {
		SaveBuilding building = new() {
			name = name,
			greatWonderProperties = new SaveBuilding.GreatWonderProperties(),
		};
		foreach (SaveBuilding.Flag flag in flags) {
			building.flags.Add(flag);
		}
		return new Building(building, gameData);
	}

	private static Tech MakeTech(C7GameData.GameData gameData, string name, bool overSea = false, bool overOcean = false) {
		Tech tech = new() {
			id = ID.FromString($"tech-{gameData.techs.Count + 1}"),
			Name = name,
			EnablesTradeOverSea = overSea,
			EnablesTradeOverOcean = overOcean,
		};
		gameData.techs.Add(tech);
		return tech;
	}

	private static void RevealTo(Player player, params Tile[] tiles) {
		foreach (Tile tile in tiles) {
			player.tileKnowledge.knownTiles.Add(tile);
		}
	}

	private static void DeclareWar(Player a, Player b) {
		a.playerRelationships[b.id] = new PlayerRelationship();
		b.playerRelationships[a.id] = new PlayerRelationship();
	}

	// A road between the two cities makes them one component.
	[Fact]
	public void CitiesConnectedByARoadShareANetwork() {
		C7GameData.GameData gameData = new();
		EngineStorage.InitializeGameDataForTests(gameData);
		Player player = MakePlayer(gameData, "player-1");

		Tile capitalTile = MakeTile(Land());
		Tile secondTile = MakeTile(Land());
		Connect(capitalTile, secondTile, TileDirection.EAST);

		City capital = MakeCity(gameData, player, capitalTile, "Capital");
		City second = MakeCity(gameData, player, secondTile, "Second");

		// The flood fill only enqueues roaded neighbours.
		secondTile.overlays.Add(new TerrainImprovement(Tile.TileOverlays.ROAD, TerrainImprovement.Layer.Roads, 1f / 3));

		Resource resource = new() { Key = "Wines", Name = "Wines" };
		secondTile.Resource = resource;

		TradeNetwork network = new(gameData);

		Assert.True(network.ConnectedToCapital(player, second));
		Assert.Equal(1, network.GetResourcesAvailableToCity(player, capital)[resource]);
	}

	[Fact]
	public void CitiesWithoutARoadAreNotConnected() {
		C7GameData.GameData gameData = new();
		EngineStorage.InitializeGameDataForTests(gameData);
		Player player = MakePlayer(gameData, "player-1");

		Tile capitalTile = MakeTile(Land());
		Tile secondTile = MakeTile(Land());
		Connect(capitalTile, secondTile, TileDirection.EAST);

		City capital = MakeCity(gameData, player, capitalTile, "Capital");
		City second = MakeCity(gameData, player, secondTile, "Second");

		TradeNetwork network = new(gameData);

		Assert.False(network.ConnectedToCapital(player, second));
	}

	// A road the human has not seen is not part of the human's network. The
	// unseen tile here is the middle of a chain, so both the step into it and
	// the step out of it fail; the same test applies to a city tile at either
	// end, which is why the two city tiles are revealed and the road tile is
	// not. The capital is the flood fill's start and is therefore never gated,
	// exactly as the original tests only trade-capability on the tile it starts
	// from.
	[Fact]
	public void AHumansLandNetworkNeedsTheConnectingTileRevealed() {
		C7GameData.GameData gameData = new();
		EngineStorage.InitializeGameDataForTests(gameData);
		Player player = MakePlayer(gameData, "player-1");
		player.isHuman = true;

		Tile capitalTile = MakeTile(Land());
		Tile middleTile = MakeTile(Land());
		Tile secondTile = MakeTile(Land());
		Connect(capitalTile, middleTile, TileDirection.EAST);
		Connect(middleTile, secondTile, TileDirection.EAST);

		City capital = MakeCity(gameData, player, capitalTile, "Capital");
		City second = MakeCity(gameData, player, secondTile, "Second");

		middleTile.overlays.Add(new TerrainImprovement(Tile.TileOverlays.ROAD, TerrainImprovement.Layer.Roads, 1f / 3));
		secondTile.overlays.Add(new TerrainImprovement(Tile.TileOverlays.ROAD, TerrainImprovement.Layer.Roads, 1f / 3));

		// Both city tiles are known, the road tile between them is not.
		RevealTo(player, capitalTile);
		RevealTo(player, secondTile);

		TradeNetwork network = new(gameData);
		Assert.False(network.ConnectedToCapital(player, second));

		// Revealing the connecting tile is what makes the road usable.
		RevealTo(player, middleTile);
		TradeNetwork revealed = new(gameData);
		Assert.True(revealed.ConnectedToCapital(player, second));
	}

	// The other half of the asymmetry: an AI's land network is not gated, even
	// though it is looking at the same roads.
	[Fact]
	public void AnAisLandNetworkIgnoresTheRevealRequirement() {
		C7GameData.GameData gameData = new();
		EngineStorage.InitializeGameDataForTests(gameData);
		Player player = MakePlayer(gameData, "player-1");

		Tile capitalTile = MakeTile(Land());
		Tile middleTile = MakeTile(Land());
		Tile secondTile = MakeTile(Land());
		Connect(capitalTile, middleTile, TileDirection.EAST);
		Connect(middleTile, secondTile, TileDirection.EAST);

		City capital = MakeCity(gameData, player, capitalTile, "Capital");
		City second = MakeCity(gameData, player, secondTile, "Second");

		middleTile.overlays.Add(new TerrainImprovement(Tile.TileOverlays.ROAD, TerrainImprovement.Layer.Roads, 1f / 3));
		secondTile.overlays.Add(new TerrainImprovement(Tile.TileOverlays.ROAD, TerrainImprovement.Layer.Roads, 1f / 3));

		// Nothing is revealed to the AI at all.
		Assert.Empty(player.tileKnowledge.knownTiles);

		TradeNetwork network = new(gameData);
		Assert.True(network.ConnectedToCapital(player, second));
	}

	private class Islands {
		public C7GameData.GameData gameData;
		public Player player;
		public City a;
		public City b;
		public Tile[] waterTiles;

		public Islands(string firstWaterTerrain, int waterCount) {
			gameData = new();
			EngineStorage.InitializeGameDataForTests(gameData);
			player = MakePlayer(gameData, "player-1");

			Tile landA = MakeTile(Land());
			Tile landB = MakeTile(Land());
			a = MakeCity(gameData, player, landA, "Island A");
			b = MakeCity(gameData, player, landB, "Island B");

			waterTiles = new Tile[waterCount];
			for (int i = 0; i < waterCount; i++) {
				waterTiles[i] = MakeTile(Water(firstWaterTerrain));
			}
			Connect(landA, waterTiles[0], TileDirection.EAST);
			for (int i = 1; i < waterCount; i++) {
				Connect(waterTiles[i - 1], waterTiles[i], TileDirection.EAST);
			}
			Connect(waterTiles[waterCount - 1], landB, TileDirection.EAST);

			RevealTo(player, landA);
			RevealTo(player, waterTiles);
			RevealTo(player, landB);
		}

		public void AddHarbor(City city) {
			city.AddBuilding(MakeBuilding(gameData, "Harbor", SaveBuilding.Flag.AllowsWaterTrade));
		}
	}

	[Fact]
	public void IslandCitiesWithHarborsConnectOverCoast() {
		Islands islands = new("coast", 3);
		islands.AddHarbor(islands.a);
		islands.AddHarbor(islands.b);

		TradeNetwork network = new(islands.gameData);

		Assert.True(network.ConnectedToCapital(islands.player, islands.b));
		Assert.True(network.AreConnected(islands.player, islands.a, islands.b));
	}

	[Fact]
	public void ASeaRouteNeedsAHarborInBothCities() {
		Islands islands = new("coast", 3);
		islands.AddHarbor(islands.a);

		TradeNetwork network = new(islands.gameData);

		Assert.False(network.AreConnected(islands.player, islands.a, islands.b));
	}

	[Fact]
	public void SeaTilesNeedEnablesTradeOverSea() {
		Islands islands = new("sea", 3);
		islands.AddHarbor(islands.a);
		islands.AddHarbor(islands.b);

		TradeNetwork withoutAstronomy = new(islands.gameData);
		Assert.False(withoutAstronomy.AreConnected(islands.player, islands.a, islands.b));

		MakeTech(islands.gameData, "Astronomy", overSea: true);
		islands.player.knownTechs.Add(islands.gameData.techs[0].id);
		TradeNetwork withAstronomy = new(islands.gameData);
		Assert.True(withAstronomy.AreConnected(islands.player, islands.a, islands.b));
	}

	[Fact]
	public void TheGreatLighthouseAllowsSeaTradeWithoutTheAdvance() {
		Islands islands = new("sea", 3);
		islands.AddHarbor(islands.a);
		islands.AddHarbor(islands.b);
		islands.a.AddBuilding(MakeGreatWonder(islands.gameData, "The Great Lighthouse", SaveBuilding.Flag.SafeSeaTravel));

		TradeNetwork network = new(islands.gameData);

		Assert.True(network.AreConnected(islands.player, islands.a, islands.b));
	}

	[Fact]
	public void OceanTilesNeedEnablesTradeOverOcean() {
		Islands islands = new("ocean", 3);
		islands.AddHarbor(islands.a);
		islands.AddHarbor(islands.b);

		TradeNetwork withoutNavigation = new(islands.gameData);
		Assert.False(withoutNavigation.AreConnected(islands.player, islands.a, islands.b));

		// The Great Lighthouse only covers Sea tiles, not Ocean.
		islands.a.AddBuilding(MakeGreatWonder(islands.gameData, "The Great Lighthouse", SaveBuilding.Flag.SafeSeaTravel));
		TradeNetwork withLighthouseOnly = new(islands.gameData);
		Assert.False(withLighthouseOnly.AreConnected(islands.player, islands.a, islands.b));

		MakeTech(islands.gameData, "Navigation", overOcean: true);
		islands.player.knownTechs.Add(islands.gameData.techs[0].id);
		TradeNetwork withNavigation = new(islands.gameData);
		Assert.True(withNavigation.AreConnected(islands.player, islands.a, islands.b));
	}

	[Fact]
	public void TheGreatLighthouseStopsWorkingWhenObsolete() {
		Islands islands = new("sea", 3);
		islands.AddHarbor(islands.a);
		islands.AddHarbor(islands.b);

		Tech magnetism = MakeTech(islands.gameData, "Magnetism", overOcean: true);
		SaveBuilding lighthouse = new() {
			name = "The Great Lighthouse",
			greatWonderProperties = new SaveBuilding.GreatWonderProperties(),
			renderedObsoleteBy = magnetism.id,
		};
		lighthouse.flags.Add(SaveBuilding.Flag.SafeSeaTravel);
		Building greatLighthouse = new Building(lighthouse, islands.gameData);
		greatLighthouse.renderedObsoleteBy = magnetism;
		islands.a.AddBuilding(greatLighthouse);
		islands.player.knownTechs.Add(magnetism.id);

		TradeNetwork network = new(islands.gameData);
		Assert.False(network.AreConnected(islands.player, islands.a, islands.b));
	}

	[Fact]
	public void ALandTileBetweenTwoHarborsBlocksTheSeaRoute() {
		C7GameData.GameData gameData = new();
		EngineStorage.InitializeGameDataForTests(gameData);
		Player player = MakePlayer(gameData, "player-1");

		Tile landA = MakeTile(Land());
		Tile middle = MakeTile(Land());
		Tile landB = MakeTile(Land());
		Connect(landA, middle, TileDirection.EAST);
		Connect(middle, landB, TileDirection.EAST);

		City a = MakeCity(gameData, player, landA, "Island A");
		City b = MakeCity(gameData, player, landB, "Island B");
		a.AddBuilding(MakeBuilding(gameData, "Harbor", SaveBuilding.Flag.AllowsWaterTrade));
		b.AddBuilding(MakeBuilding(gameData, "Harbor", SaveBuilding.Flag.AllowsWaterTrade));
		RevealTo(player, landA, middle, landB);

		TradeNetwork network = new(gameData);

		Assert.False(network.AreConnected(player, a, b));
	}

	[Fact]
	public void TheSeaRouteMustRunThroughRevealedTiles() {
		Islands islands = new("coast", 3);
		islands.AddHarbor(islands.a);
		islands.AddHarbor(islands.b);
		islands.player.tileKnowledge.knownTiles.Remove(islands.waterTiles[1]);

		TradeNetwork network = new(islands.gameData);

		Assert.False(network.AreConnected(islands.player, islands.a, islands.b));
	}

	private class AirScenario {
		public C7GameData.GameData gameData;
		public Player player;
		public Player rival;
		public City airport;
		public City target;

		public AirScenario() {
			gameData = new();
			EngineStorage.InitializeGameDataForTests(gameData);
			player = MakePlayer(gameData, "player-1");
			rival = MakePlayer(gameData, "player-2");

			// Two widely separated islands: no road and no sea route exists,
			// so any connection has to come from the airport pass.
			Tile homeTile = MakeTile(Land());
			Tile targetTile = MakeTile(Land());
			airport = MakeCity(gameData, player, homeTile, "Airport City");
			target = MakeCity(gameData, rival, targetTile, "Target City");
			airport.AddBuilding(MakeBuilding(gameData, "Airport", SaveBuilding.Flag.AllowsAirTrade));
			target.AddBuilding(MakeBuilding(gameData, "Airport", SaveBuilding.Flag.AllowsAirTrade));

			RevealTo(player, homeTile);
		}
	}

	[Fact]
	public void AnAirportConnectsToARevealedCityOfANonWarringCiv() {
		AirScenario scenario = new();
		RevealTo(scenario.player, scenario.target.location);

		TradeNetwork network = new(scenario.gameData);

		Assert.True(network.AreConnected(scenario.player, scenario.airport, scenario.target));
	}

	[Fact]
	public void AnAirportDoesNotConnectToAnUnrevealedCity() {
		AirScenario scenario = new();

		TradeNetwork network = new(scenario.gameData);

		Assert.False(network.AreConnected(scenario.player, scenario.airport, scenario.target));
	}

	[Fact]
	public void AnAirportDoesNotConnectAcrossAWar() {
		AirScenario scenario = new();
		RevealTo(scenario.player, scenario.target.location);
		DeclareWar(scenario.player, scenario.rival);

		TradeNetwork network = new(scenario.gameData);

		Assert.False(network.AreConnected(scenario.player, scenario.airport, scenario.target));
	}

	[Fact]
	public void AnAirportDoesNotConnectToACityWithoutAnAirport() {
		AirScenario scenario = new();
		RevealTo(scenario.player, scenario.target.location);
		scenario.target.RemoveBuilding(scenario.target.GetBuildings().First());

		TradeNetwork network = new(scenario.gameData);

		Assert.False(network.AreConnected(scenario.player, scenario.airport, scenario.target));
	}
}

// Guards the shipped rules data for the trade-network flags. A typo in
// ruleset.json would otherwise fail silently, and a flag list that covers only
// some of the buildings the BIQ flags would be worse than none.
public class TradeNetworkRulesetTest {
	private static List<string> NamesWithFlag(JsonElement collection, string flag) {
		List<string> result = new();
		foreach (JsonElement entry in collection.EnumerateArray()) {
			if (!entry.TryGetProperty("flags", out JsonElement flags)) {
				continue;
			}
			if (flags.EnumerateArray().Any(f => f.GetString() == flag)) {
				result.Add(entry.GetProperty("name").GetString());
			}
		}
		result.Sort();
		return result;
	}

	[Fact]
	public void RulesetCarriesTheShippedTradeFlags() {
		JsonDocument ruleset = JsonUtils.LoadBaseRuleset();

		Assert.Equal(new List<string> { "Harbor" },
			NamesWithFlag(ruleset.RootElement.GetProperty("buildings"), "allowsWaterTrade"));
		Assert.Equal(new List<string> { "Airport" },
			NamesWithFlag(ruleset.RootElement.GetProperty("buildings"), "allowsAirTrade"));
		Assert.Equal(new List<string> { "The Great Lighthouse" },
			NamesWithFlag(ruleset.RootElement.GetProperty("buildings"), "safeSeaTravel"));
		Assert.Equal(new List<string> { "Astronomy" },
			NamesWithFlag(ruleset.RootElement.GetProperty("techs"), "enablesTradeOverSea"));
		Assert.Equal(new List<string> { "Magnetism", "Navigation" },
			NamesWithFlag(ruleset.RootElement.GetProperty("techs"), "enablesTradeOverOcean"));
	}
}
