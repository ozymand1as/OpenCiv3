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
/// Tests for 18_terrain_improvement.md sections 6 (pollution) and 7 (global
/// warming), plus the "clear damage" worker job from sections 3.5 and 5 and the
/// tile-flag persistence the polluted state relies on.
/// </summary>
public class PollutionTest {
	private const int TestSeed = 42;

	// The Civ3 terrain ids the shipped rules use, so tests can name the
	// pollution effect the way the BIQ stores it.
	private const int DesertId = 0;
	private const int PlainsId = 1;
	private const int GrasslandId = 2;
	private const int CoastId = 11;
	private const int UnderlyingId = TerrainType.UnderlyingTerrainPollutionEffect;

	private static TerrainType MakeTerrain(string key, int pollutionEffect, int food = 1, int shields = 1, int commerce = 0) {
		return new TerrainType {
			Key = key,
			DisplayName = key,
			baseFoodProduction = food,
			baseShieldProduction = shields,
			baseCommerceProduction = commerce,
			movementCost = 1,
			pollutionEffect = pollutionEffect,
		};
	}

	/// <summary>
	/// Builds a 1-tile map holding the named shipped terrain (with an optional
	/// different underlying terrain). A one tile map keeps the global-warming
	/// tile draw deterministic.
	/// </summary>
	private static (C7GameData.GameData, Tile) MakeSingleTileGame(string terrainKey, string baseTerrainKey = null) {
		C7GameData.GameData gameData = MakeGameData();
		TerrainType terrain = gameData.terrainTypes.First(t => t.Key == terrainKey);
		TerrainType baseTerrain = baseTerrainKey == null
			? terrain
			: gameData.terrainTypes.First(t => t.Key == baseTerrainKey);

		Tile tile = new(ID.None("tile")) {
			baseTerrainType = baseTerrain,
			overlayTerrainType = terrain,
			XCoordinate = 0,
			YCoordinate = 0,
			continent = 0,
		};
		GameMap map = new() { numTilesWide = 2, numTilesTall = 1, tiles = new List<Tile> { tile } };
		tile.map = map;
		gameData.map = map;

		return (gameData, tile);
	}

	private static C7GameData.GameData MakeGameData() {
		C7GameData.GameData gameData = new(TestSeed);
		gameData.rules = new Rules {
			MaximumLevel1CitySize = 6,
			MaximumLevel2CitySize = 12,
			MaxRankOfWorkableTiles = 2,
		};
		gameData.terrainTypes = new List<TerrainType> {
			MakeTerrain("desert", -1, food: 0, shields: 1, commerce: 0),
			MakeTerrain("plains", DesertId, food: 1, shields: 1, commerce: 0),
			MakeTerrain("grassland", PlainsId, food: 2, shields: 0, commerce: 0),
			MakeTerrain("forest", UnderlyingId, food: 1, shields: 2, commerce: 0),
			MakeTerrain("jungle", UnderlyingId, food: 1, shields: 0, commerce: 0),
			MakeTerrain("marsh", CoastId, food: 1, shields: 0, commerce: 0),
			MakeTerrain("coast", -1, food: 1, shields: 0, commerce: 2),
		};
		gameData.terrainImprovements = new List<TerrainImprovement> {
			new("pollution", TerrainImprovement.Layer.Pollution),
			new("craters", TerrainImprovement.Layer.Craters),
			new("mine", TerrainImprovement.Layer.ResourceDevelopment),
			new("irrigation", TerrainImprovement.Layer.ResourceDevelopment),
		};
		EngineStorage.InitializeGameDataForTests(gameData);
		C7GameData.GameData.rng = new Random(TestSeed);
		return gameData;
	}

	private static Player MakePlayer(C7GameData.GameData gameData) {
		Player player = new() {
			isHuman = false,
			civilization = new Civilization(),
			government = new Government(),
		};
		player.rules = gameData.rules;
		return player;
	}

	private static City MakeCity(C7GameData.GameData gameData, Player player, int population, params Building[] buildings) {
		Tile cityTile = new(ID.None("tile")) {
			baseTerrainType = gameData.terrainTypes[0],
			overlayTerrainType = gameData.terrainTypes[0],
		};
		City city = new(cityTile, player, "Test City", ID.None("city"));
		for (int i = 0; i < population; ++i) {
			city.residents.Add(new CityResident());
		}
		foreach (Building building in buildings) {
			city.AddBuilding(building);
		}
		cityTile.cityAtTile = city;
		player.cities.Add(city);
		gameData.cities.Add(city);
		return city;
	}

	private static Building MakeBuilding(C7GameData.GameData gameData, string name, int pollution = 0, bool removesPopulationPollution = false, bool reducesBuildingPollution = false) {
		HashSet<SaveBuilding.Flag> flags = new();
		if (removesPopulationPollution) {
			flags.Add(SaveBuilding.Flag.RemovesPopulationPollution);
		}
		if (reducesBuildingPollution) {
			flags.Add(SaveBuilding.Flag.ReducesBuildingPollution);
		}
		return new Building(new SaveBuilding { name = name, pollution = pollution, flags = flags }, gameData);
	}

	private static GameMap MakeMap(int width, int height, Func<int, int, Tile> makeTile) {
		GameMap map = new() { numTilesWide = width, numTilesTall = height };
		List<Tile> tiles = new();
		for (int y = 0; y < height; ++y) {
			for (int x = y % 2; x < width; x += 2) {
				Tile tile = makeTile(x, y);
				tile.XCoordinate = x;
				tile.YCoordinate = y;
				tile.map = map;
				tile.continent = 0;
				tiles.Add(tile);
			}
		}
		map.tiles = tiles;
		map.computeNeighbors();
		return map;
	}

	private static Tile MakeLandTile(C7GameData.GameData gameData) {
		return new Tile(ID.None("tile")) {
			baseTerrainType = gameData.terrainTypes[2],
			overlayTerrainType = gameData.terrainTypes[2],
		};
	}

	/// <summary>
	/// Builds a game whose accumulated pollution is exactly
	/// <paramref name="accumulatedPollution"/> (via one city with a building of
	/// that pollution value) and whose map has exactly
	/// <paramref name="tileCount"/> tiles, so the global-warming severity bands
	/// can be exercised at their boundaries.
	/// </summary>
	private static C7GameData.GameData MakeSeverityGame(int accumulatedPollution, int tileCount) {
		C7GameData.GameData gameData = MakeGameData();
		Player player = MakePlayer(gameData);

		List<Tile> tiles = new();
		for (int i = 0; i < tileCount; ++i) {
			Tile tile = MakeLandTile(gameData);
			tile.XCoordinate = i;
			tile.YCoordinate = 0;
			tiles.Add(tile);
		}
		gameData.map = new GameMap { numTilesWide = tileCount, numTilesTall = 1, tiles = tiles };

		if (accumulatedPollution > 0) {
			City city = MakeCity(gameData, player, 1,
				MakeBuilding(gameData, "Polluter", pollution: accumulatedPollution));
			city.location = tiles[0];
			tiles[0].cityAtTile = city;
		}

		return gameData;
	}

	// ---- Population pollution (section 6.1) ----

	[Fact]
	public void PopulationPollution_IsZeroAtTheSizeThreshold() {
		C7GameData.GameData gameData = MakeGameData();
		City city = MakeCity(gameData, MakePlayer(gameData), 12);

		Assert.Equal(0, city.PollutionFromPopulation());
	}

	[Fact]
	public void PopulationPollution_IsOneJustOverTheSizeThreshold() {
		C7GameData.GameData gameData = MakeGameData();
		City city = MakeCity(gameData, MakePlayer(gameData), 13);

		Assert.Equal(1, city.PollutionFromPopulation());
	}

	[Fact]
	public void PopulationPollution_IsCappedAtOneByANonObsoleteMassTransit() {
		C7GameData.GameData gameData = MakeGameData();
		Building massTransit = MakeBuilding(gameData, "Mass Transit System", removesPopulationPollution: true);
		City city = MakeCity(gameData, MakePlayer(gameData), 15, massTransit);

		Assert.Equal(1, city.PollutionFromPopulation());
	}

	[Fact]
	public void PopulationPollution_IgnoresAnObsoleteMassTransit() {
		C7GameData.GameData gameData = MakeGameData();
		Player player = MakePlayer(gameData);
		Building massTransit = MakeBuilding(gameData, "Mass Transit System", removesPopulationPollution: true);
		massTransit.renderedObsoleteBy = new Tech { id = ID.None("obsoleting-tech") };
		player.knownTechs.Add(massTransit.renderedObsoleteBy.id);
		City city = MakeCity(gameData, player, 15, massTransit);

		Assert.Equal(3, city.PollutionFromPopulation());
	}

	// ---- Building pollution (section 6.2) ----

	[Fact]
	public void BuildingPollution_IsTheSumOfTheBuildingsValues() {
		C7GameData.GameData gameData = MakeGameData();
		City city = MakeCity(gameData, MakePlayer(gameData), 1,
			MakeBuilding(gameData, "Factory", pollution: 2),
			MakeBuilding(gameData, "Coal Plant", pollution: 2));

		Assert.Equal(4, city.PollutionFromBuildings());
	}

	[Fact]
	public void BuildingPollution_IsCappedAtOneByANonObsoleteRecyclingCenter() {
		C7GameData.GameData gameData = MakeGameData();
		City city = MakeCity(gameData, MakePlayer(gameData), 1,
			MakeBuilding(gameData, "Factory", pollution: 4),
			MakeBuilding(gameData, "Recycling Center", reducesBuildingPollution: true));

		Assert.Equal(1, city.PollutionFromBuildings());
	}

	[Fact]
	public void BuildingPollution_IgnoresAnObsoleteRecyclingCenter() {
		C7GameData.GameData gameData = MakeGameData();
		Player player = MakePlayer(gameData);
		Building recyclingCenter = MakeBuilding(gameData, "Recycling Center", reducesBuildingPollution: true);
		recyclingCenter.renderedObsoleteBy = new Tech { id = ID.None("obsoleting-tech") };
		player.knownTechs.Add(recyclingCenter.renderedObsoleteBy.id);
		City city = MakeCity(gameData, player, 1, MakeBuilding(gameData, "Factory", pollution: 4), recyclingCenter);

		Assert.Equal(4, city.PollutionFromBuildings());
	}

	[Fact]
	public void TotalPollution_AddsThePopulationAndBuildingTerms() {
		C7GameData.GameData gameData = MakeGameData();
		City city = MakeCity(gameData, MakePlayer(gameData), 15, MakeBuilding(gameData, "Factory", pollution: 2));

		Assert.Equal(5, city.TotalPollution());
	}

	// ---- Spawning (section 6.3) ----

	[Fact]
	public void SpawnPollution_PollutesTheOneEligibleTileInTheCityRadius() {
		C7GameData.GameData gameData = MakeGameData();
		Player player = MakePlayer(gameData);
		// Make every tile water except one eligible land tile, so the choice is
		// deterministic no matter which candidate the roll starts from.
		TerrainType water = gameData.terrainTypes.First(t => t.Key == "coast");
		GameMap map = MakeMap(16, 16, (x, y) => new Tile(ID.None("tile")) {
			baseTerrainType = water,
			overlayTerrainType = water,
			continent = 0,
		});
		gameData.map = map;

		Tile center = map.tileAt(6, 6);
		TerrainType grassland = gameData.terrainTypes.First(t => t.Key == "grassland");
		center.baseTerrainType = grassland;
		center.overlayTerrainType = grassland;
		City city = MakeCity(gameData, player, 1, MakeBuilding(gameData, "Guaranteed Polluter", pollution: 100));
		city.location = center;
		center.cityAtTile = city;

		List<Tile> radius = center.GetTilesWithinRankDistance(2);
		Tile expected = radius.First(t => t != center && t != Tile.NONE);
		expected.baseTerrainType = grassland;
		expected.overlayTerrainType = grassland;

		Assert.True(Pollution.SpawnPollution(city));
		Assert.True(expected.HasPollution());

		// The city centre is never polluted, water is skipped, and only the one
		// eligible tile was polluted.
		Assert.False(center.HasPollution());
		Assert.Single(radius.Where(t => t != Tile.NONE && t.HasPollution()));
		Assert.Equal(expected, radius.Single(t => t != Tile.NONE && t.HasPollution()));
	}

	[Fact]
	public void SpawnPollution_DoesNothingWhenTheCityGeneratesNoPollution() {
		C7GameData.GameData gameData = MakeGameData();
		Player player = MakePlayer(gameData);
		GameMap map = MakeMap(16, 16, (x, y) => MakeLandTile(gameData));
		gameData.map = map;

		Tile center = map.tileAt(6, 6);
		City city = MakeCity(gameData, player, 12);
		city.location = center;
		center.cityAtTile = city;

		Assert.False(Pollution.SpawnPollution(city));
		Assert.DoesNotContain(center.GetTilesWithinRankDistance(2), t => t != Tile.NONE && t.HasPollution());
	}

	[Fact]
	public void SpawnPollution_SkipsWaterTiles() {
		C7GameData.GameData gameData = MakeGameData();
		Player player = MakePlayer(gameData);
		TerrainType coast = gameData.terrainTypes.First(t => t.Key == "coast");
		GameMap map = MakeMap(16, 16, (x, y) => new Tile(ID.None("tile")) {
			baseTerrainType = coast,
			overlayTerrainType = coast,
			continent = 0,
		});
		gameData.map = map;

		Tile center = map.tileAt(6, 6);
		City city = MakeCity(gameData, player, 1, MakeBuilding(gameData, "Guaranteed Polluter", pollution: 100));
		city.location = center;
		center.cityAtTile = city;

		Assert.False(Pollution.SpawnPollution(city));
		Assert.DoesNotContain(center.GetTilesWithinRankDistance(2), t => t != Tile.NONE && t.HasPollution());
	}

	// ---- Tile yields (section 6.4) ----

	[Fact]
	public void PollutionZeroesTheTilesFoodShieldsAndCommerce() {
		C7GameData.GameData gameData = MakeGameData();
		Player player = MakePlayer(gameData);
		TerrainType rich = MakeTerrain("rich", pollutionEffect: -1, food: 2, shields: 2, commerce: 1);
		Tile tile = new(ID.None("tile")) { baseTerrainType = rich, overlayTerrainType = rich };

		Assert.Equal(2, tile.FoodYield(player).yield);
		Assert.Equal(2, tile.ProductionYield(player).yield);
		Assert.Equal(1, tile.CommerceYield(player).yield);

		Tile.TryAddPollution(tile);

		Assert.Equal(0, tile.FoodYield(player).yield);
		Assert.Equal(0, tile.ProductionYield(player).yield);
		Assert.Equal(0, tile.CommerceYield(player).yield);
	}

	// ---- Cleanup (sections 3.5 and 5) ----

	[Fact]
	public void ClearDamage_RemovesPollution() {
		C7GameData.GameData gameData = MakeGameData();
		Tile tile = MakeLandTile(gameData);
		Tile.TryAddPollution(tile);

		tile.ClearDamage();

		Assert.False(tile.HasPollution());
		Assert.False(tile.HasCraters());
	}

	[Fact]
	public void ClearDamage_RemovesCratersOnACrateredTile() {
		C7GameData.GameData gameData = MakeGameData();
		Tile tile = MakeLandTile(gameData);
		Tile.TryAddCraters(tile);

		tile.ClearDamage();

		Assert.False(tile.HasCraters());
		Assert.False(tile.HasPollution());
	}

	[Fact]
	public void ClearDamage_LeavesCratersOnATileThatIsAlsoPolluted() {
		C7GameData.GameData gameData = MakeGameData();
		Tile tile = MakeLandTile(gameData);
		Tile.TryAddPollution(tile);
		Tile.TryAddCraters(tile);

		tile.ClearDamage();

		Assert.False(tile.HasPollution());
		Assert.True(tile.HasCraters());
	}

	// ---- Global warming (section 7) ----

	[Fact]
	public void GlobalWarming_TurnsGrasslandIntoPlains() {
		(C7GameData.GameData gameData, Tile tile) = MakeSingleTileGame("grassland");
		MakeCity(gameData, MakePlayer(gameData), 1, MakeBuilding(gameData, "Factory", pollution: 1));

		Pollution.DoPerTurnGlobalWarming(gameData);

		Assert.Equal(PlainsId, TerrainType.Civ3TerrainIdForKey(tile.overlayTerrainType.Key));
		// Civ3's Map_change_tile_terrain changes only the rule terrain (+0xC8),
		// so the underlying terrain is still grassland.
		Assert.Equal(GrasslandId, TerrainType.Civ3TerrainIdForKey(tile.baseTerrainType.Key));
	}

	[Fact]
	public void GlobalWarming_PreservesTheTilesUnderlyingTerrain() {
		// A grassland tile that is warmed to plains keeps grassland underneath,
		// exactly as Map_change_tile_terrain leaves Tile + 0xC4 alone. The
		// underlying terrain is what the 0xE PollutionEffect sentinel reads, so
		// overwriting it would also corrupt the forest/jungle fallback.
		(C7GameData.GameData gameData, Tile tile) = MakeSingleTileGame("grassland");
		MakeCity(gameData, MakePlayer(gameData), 1, MakeBuilding(gameData, "Factory", pollution: 1));

		Pollution.DoPerTurnGlobalWarming(gameData);

		Assert.Equal("plains", tile.overlayTerrainType.Key);
		Assert.Equal("grassland", tile.baseTerrainType.Key);
	}

	[Fact]
	public void GlobalWarming_TurnsPlainsIntoDesert() {
		(C7GameData.GameData gameData, Tile tile) = MakeSingleTileGame("plains");
		MakeCity(gameData, MakePlayer(gameData), 1, MakeBuilding(gameData, "Factory", pollution: 1));

		Pollution.DoPerTurnGlobalWarming(gameData);

		Assert.Equal(DesertId, TerrainType.Civ3TerrainIdForKey(tile.overlayTerrainType.Key));
		Assert.Equal(PlainsId, TerrainType.Civ3TerrainIdForKey(tile.baseTerrainType.Key));
	}

	[Fact]
	public void GlobalWarming_StripsForestDownToItsUnderlyingTerrain() {
		(C7GameData.GameData gameData, Tile tile) = MakeSingleTileGame("forest", baseTerrainKey: "grassland");
		MakeCity(gameData, MakePlayer(gameData), 1, MakeBuilding(gameData, "Factory", pollution: 1));

		Pollution.DoPerTurnGlobalWarming(gameData);

		Assert.Equal("grassland", tile.overlayTerrainType.Key);
		Assert.Equal("grassland", tile.baseTerrainType.Key);
	}

	[Fact]
	public void GlobalWarming_ClearsMinesAndIrrigation() {
		(C7GameData.GameData gameData, Tile tile) = MakeSingleTileGame("grassland");
		TerrainImprovement mine = gameData.terrainImprovements.First(ti => ti.key == "mine");
		tile.overlays.Add(mine);
		MakeCity(gameData, MakePlayer(gameData), 1, MakeBuilding(gameData, "Factory", pollution: 1));

		Pollution.DoPerTurnGlobalWarming(gameData);

		Assert.Null(tile.overlays.ImprovementAtLayer(TerrainImprovement.Layer.ResourceDevelopment));
	}

	[Fact]
	public void GlobalWarming_LeavesImmuneTerrainsAlone() {
		(C7GameData.GameData gameData, Tile tile) = MakeSingleTileGame("desert");
		MakeCity(gameData, MakePlayer(gameData), 1, MakeBuilding(gameData, "Factory", pollution: 1));

		Pollution.DoPerTurnGlobalWarming(gameData);

		Assert.Equal(DesertId, TerrainType.Civ3TerrainIdForKey(tile.overlayTerrainType.Key));
	}

	[Fact]
	public void GlobalWarming_DoesNothingWithoutAccumulatedPollution() {
		(C7GameData.GameData gameData, Tile tile) = MakeSingleTileGame("grassland");
		MakeCity(gameData, MakePlayer(gameData), 12);

		Pollution.DoPerTurnGlobalWarming(gameData);

		Assert.Equal(GrasslandId, TerrainType.Civ3TerrainIdForKey(tile.overlayTerrainType.Key));
	}

	// ---- Severity indicator (section 7 step 2) ----

	[Theory]
	// A clean world is 0; any pollution at all is 1; past a quarter of the map
	// is 2; past half is 3. The boundaries are strict, so exactly a quarter or
	// exactly half does not reach the next band.
	[InlineData(0, 100, 0)]
	[InlineData(1, 100, 1)]
	[InlineData(25, 100, 1)]
	[InlineData(26, 100, 2)]
	[InlineData(50, 100, 2)]
	[InlineData(51, 100, 3)]
	[InlineData(100, 100, 3)]
	// An odd tile count pins the integer halves: 5/2 = 2 and 5/4 = 1.
	[InlineData(1, 5, 1)]
	[InlineData(2, 5, 2)]
	[InlineData(3, 5, 3)]
	public void GlobalWarmingSeverity_ReportsTheBandOfTheAccumulatedPollution(int accumulatedPollution, int tileCount, int expected) {
		C7GameData.GameData gameData = MakeSeverityGame(accumulatedPollution, tileCount);

		Assert.Equal(accumulatedPollution, Pollution.AccumulatedPollution(gameData));
		Assert.Equal(expected, Pollution.GlobalWarmingSeverity(gameData));
	}

	[Fact]
	public void AccumulatedPollution_AddsTheSquareOfTheNukesUsed() {
		// Civ3 adds nukes^2 to the sum (section 7 step 1). Nothing in OpenCiv3
		// increments nukesUsed yet, so this term is always zero in a real game,
		// but the rule itself is implemented and pinned here.
		C7GameData.GameData gameData = MakeSeverityGame(5, 100);
		gameData.nukesUsed = 3;

		Assert.Equal(14, Pollution.AccumulatedPollution(gameData));
	}

	[Fact]
	public void GlobalWarmingPass_RecordsTheSeverityIndicator() {
		C7GameData.GameData gameData = MakeSeverityGame(3, 4);

		Pollution.DoPerTurnGlobalWarming(gameData);

		Assert.Equal(3, gameData.globalWarmingSeverity);
		Assert.Equal(3, Pollution.GlobalWarmingSeverity(gameData));
	}

	[Fact]
	public void GlobalWarmingPass_RecordsAZeroSeverityForACleanWorld() {
		C7GameData.GameData gameData = MakeSeverityGame(0, 100);

		Pollution.DoPerTurnGlobalWarming(gameData);

		Assert.Equal(0, gameData.globalWarmingSeverity);
	}

	// ---- Persistence ----

	[Fact]
	public void SaveTileRoundTrip_PreservesPollution() {
		C7GameData.GameData gameData = MakeGameData();
		Tile tile = MakeLandTile(gameData);
		Tile.TryAddPollution(tile);

		Tile restored = new SaveTile(tile).ToTile(gameData.terrainTypes, new List<Resource>(), gameData.terrainImprovements);

		Assert.True(restored.HasPollution());
	}
}

/// <summary>
/// Exercises the "clear damage" worker job through the real ruleset, so the
/// Terraform-to-Lua wiring (and the terrains' TERR.PollutionEffect values) are
/// covered and not just the C# helpers.
/// </summary>
public class ClearDamageTerraformTest : IClassFixture<SaveGameFixture> {
	private readonly SaveGameFixture fixture;

	public ClearDamageTerraformTest(SaveGameFixture fixture) {
		this.fixture = fixture;
	}

	[Fact]
	public void ClearDamageTerraform_RemovesPollutionFromTheTile() {
		C7GameData.GameData gameData = fixture.saveGame.ToGameData(fixture.behaviors);
		Terraform clearDamage = gameData.Terraforms.First(t => t.Name == "Clear Damage");
		Player player = gameData.players.First(p => !p.isBarbarians);

		Tile tile = gameData.map.tiles.First(t => t.IsLand());
		Tile.TryAddPollution(tile);

		Assert.True(clearDamage.MeetsRequirements(player, tile));

		clearDamage.OnComplete(player, tile);

		Assert.False(tile.HasPollution());
	}

	[Fact]
	public void ClearDamageTerraform_IsRefusedOnACleanTile() {
		C7GameData.GameData gameData = fixture.saveGame.ToGameData(fixture.behaviors);
		Terraform clearDamage = gameData.Terraforms.First(t => t.Name == "Clear Damage");
		Player player = gameData.players.First(p => !p.isBarbarians);

		Tile tile = gameData.map.tiles.First(t => t.IsLand() && !t.HasPollution() && !t.HasCraters());

		Assert.False(clearDamage.MeetsRequirements(player, tile));
	}

	[Fact]
	public void RulesetTerrainPollutionEffects_MatchTheShippedBiq() {
		C7GameData.GameData gameData = fixture.saveGame.ToGameData(fixture.behaviors);

		Dictionary<string, int> expected = new() {
			["desert"] = -1,
			["plains"] = 0,
			["grassland"] = 1,
			["tundra"] = -1,
			["flood plain"] = -1,
			["hills"] = -1,
			["mountains"] = -1,
			["forest"] = 14,
			["jungle"] = 14,
			["marsh"] = 11,
			["volcano"] = -1,
			["coast"] = -1,
			["sea"] = -1,
			["ocean"] = -1,
		};

		foreach ((string key, int pollutionEffect) in expected) {
			TerrainType terrain = gameData.terrainTypes.First(t => t.Key == key);
			Assert.Equal(pollutionEffect, terrain.pollutionEffect);
		}
	}

	[Fact]
	public void RulesetBuildings_CarryTheShippedPollutionValuesAndFlags() {
		C7GameData.GameData gameData = fixture.saveGame.ToGameData(fixture.behaviors);

		Assert.Equal(4, gameData.Buildings.First(b => b.name == "Iron Works").pollution);
		Assert.Equal(2, gameData.Buildings.First(b => b.name == "Factory").pollution);
		Assert.Equal(2, gameData.Buildings.First(b => b.name == "Coal Plant").pollution);
		Assert.Equal(2, gameData.Buildings.First(b => b.name == "Manufacturing Plant").pollution);
		Assert.Equal(2, gameData.Buildings.First(b => b.name == "Offshore Platform").pollution);
		Assert.Equal(1, gameData.Buildings.First(b => b.name == "Research Lab").pollution);
		Assert.Equal(1, gameData.Buildings.First(b => b.name == "Airport").pollution);
		Assert.Equal(1, gameData.Buildings.First(b => b.name == "Commercial Dock").pollution);

		Assert.True(gameData.Buildings.First(b => b.name == "Mass Transit System").removesPopulationPollution);
		Assert.True(gameData.Buildings.First(b => b.name == "Recycling Center").reducesBuildingPollution);
	}

	[Fact]
	public void RulesetWorker_CanPerformClearDamage() {
		C7GameData.GameData gameData = fixture.saveGame.ToGameData(fixture.behaviors);

		UnitPrototype worker = gameData.unitPrototypes.First(u => u.name == "Worker");
		Assert.Contains(worker.terraformActions, t => t.Name == "Clear Damage");
	}
}
