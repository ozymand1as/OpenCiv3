using System.IO;
using C7Engine;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using Xunit;

namespace EngineTests.GameData;

public class ResearchTest : IClassFixture<SaveGameFixture> {
	private readonly SaveGameFixture fixture;

	public ResearchTest(SaveGameFixture fixture) {
		this.fixture = fixture;
	}

	private static string WriteSave(SaveGame save, string fileName) {
		string path = PathUtils.getDataPath($"output/{fileName}");
		Directory.CreateDirectory(Path.GetDirectoryName(path));
		save.Save(path);
		return path;
	}

	// A player with one city that produces a known amount of commerce, and the
	// rules needed for research to progress at all.
	private static C7GameData.GameData MakeGame(params Tech[] techs) {
		C7GameData.GameData gameData = new C7GameData.GameData(1234);
		gameData.map.techRate = 1;
		gameData.gameDifficulty = new Difficulty { HumanCostFactor = 10, AiCostFactor = 10 };
		gameData.rules = new Rules {
			MinimumResearchTime = 4,
			MaximumResearchTime = 50,
			MaximumLevel1CitySize = 6,
			MaximumLevel2CitySize = 12,
		};
		gameData.techs.AddRange(techs);
		return gameData;
	}

	private static Player MakePlayer(C7GameData.GameData gameData, bool isHuman = true) {
		Player player = new() {
			id = ID.None("player"),
			isHuman = isHuman,
			civilization = new Civilization(),
			government = new Government(),
			rules = gameData.rules,
			eraCivilopediaName = "ERAS_Ancient_Times",
		};
		gameData.players.Add(player);
		return player;
	}

	// A city whose commerce yield is exactly `commerce`, so the science slider
	// splits it deterministically. The city center yields one commerce at size
	// one; the rest comes from a worked tile.
	private static City MakeCity(C7GameData.GameData gameData, Player player, int commerce) {
		Tile center = new(ID.None("city-tile")) { overlayTerrainType = new TerrainType { Key = "grassland" } };
		City city = new(center, player, "Test City", ID.None("city"));
		center.cityAtTile = city;
		player.cities.Add(city);
		gameData.cities.Add(city);

		Tile worked = new(ID.None("worked-tile")) {
			overlayTerrainType = new TerrainType { Key = "grassland", baseCommerceProduction = commerce - 1 },
		};
		city.residents.Add(new CityResident { citizenType = new CitizenType(), tileWorked = worked });
		return city;
	}

	private static Tech MakeTech(string id, int cost) {
		return new Tech {
			id = ID.None(id),
			Name = id,
			Cost = cost,
			EraCivilopediaName = "ERAS_Ancient_Times",
		};
	}

	[Fact]
	public void FreeTechsRemainingRoundTripsThroughJson() {
		SaveGame save = fixture.saveGame.Clone();
		save.Players[0].freeTechsRemaining = 3;

		string path = WriteSave(save, "research_free_techs.json");
		SaveGame loaded = SaveGame.Load(path, unused => unused);
		File.Delete(path);

		Assert.Equal(3, loaded.Players[0].freeTechsRemaining.Value);
	}

	[Fact]
	public void FreeTechsRemainingIsOmittedWhenZero() {
		SaveGame save = fixture.saveGame.Clone();
		save.Players[0].freeTechsRemaining = null;

		string path = WriteSave(save, "research_no_free_techs.json");
		string json = File.ReadAllText(path);
		File.Delete(path);

		Assert.DoesNotContain("freeTechsRemaining", json);
	}

	[Fact]
	public void ResearchOverflowIsCarriedIntoTheNextTech() {
		Tech philosophy = MakeTech("tech-philosophy", 40);
		Tech pottery = MakeTech("tech-pottery", 10);
		pottery.RequiredForEraAdvancement = true;
		C7GameData.GameData gameData = MakeGame(philosophy, pottery);
		Player player = MakePlayer(gameData);
		MakeCity(gameData, player, commerce: 8);
		EngineStorage.InitializeGameDataForTests(gameData);

		int fullCost = gameData.TechCostFor(philosophy, player);
		player.SetCurrentlyResearchedTech(philosophy.id);
		player.beakers = fullCost + 7;
		player.turnsResearched = gameData.rules.MinimumResearchTime;

		player.DoPerTurnScienceUpdates(gameData);

		Assert.Contains(philosophy.id, player.knownTechs);
		Assert.Equal(pottery.id, player.currentlyResearchedTech);
		Assert.Equal(7, player.beakers);
	}

	[Fact]
	public void NoOverflowIsCarriedWhenTheCostIsMetExactly() {
		Tech philosophy = MakeTech("tech-philosophy", 40);
		Tech pottery = MakeTech("tech-pottery", 10);
		pottery.RequiredForEraAdvancement = true;
		C7GameData.GameData gameData = MakeGame(philosophy, pottery);
		Player player = MakePlayer(gameData);
		MakeCity(gameData, player, commerce: 8);
		EngineStorage.InitializeGameDataForTests(gameData);

		int fullCost = gameData.TechCostFor(philosophy, player);
		player.SetCurrentlyResearchedTech(philosophy.id);
		player.beakers = fullCost;
		player.turnsResearched = gameData.rules.MinimumResearchTime;

		player.DoPerTurnScienceUpdates(gameData);

		Assert.Contains(philosophy.id, player.knownTechs);
		Assert.Equal(pottery.id, player.currentlyResearchedTech);
		Assert.Equal(0, player.beakers);
	}

	[Fact]
	public void NoOverflowIsCarriedWhenTheTechTreeIsExhausted() {
		Tech philosophy = MakeTech("tech-philosophy", 40);
		C7GameData.GameData gameData = MakeGame(philosophy);
		Player player = MakePlayer(gameData);
		MakeCity(gameData, player, commerce: 8);
		EngineStorage.InitializeGameDataForTests(gameData);

		int fullCost = gameData.TechCostFor(philosophy, player);
		player.SetCurrentlyResearchedTech(philosophy.id);
		player.beakers = fullCost + 7;
		player.turnsResearched = gameData.rules.MinimumResearchTime;

		player.DoPerTurnScienceUpdates(gameData);

		Assert.Contains(philosophy.id, player.knownTechs);
		Assert.Null(player.currentlyResearchedTech);
		Assert.Equal(0, player.beakers);
	}
}
