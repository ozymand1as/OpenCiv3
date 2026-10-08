using System.Collections.Generic;
using System.Text.Json;
using C7Engine;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using Xunit;

namespace EngineTests.GameData;

// Civ3's future technology: once every technology in the tree is known a
// player keeps researching, and each completion increments a per-player
// counter that the score's technology term reads (16_science.md §3.1).
public class FutureTechTest {

	private static C7GameData.GameData MakeGameData(Player player) {
		C7GameData.GameData gameData = new() {
			rules = new Rules { MinimumResearchTime = 4, MaximumResearchTime = 50, FutureTechCost = 400 },
			gameDifficulty = new Difficulty { AiCostFactor = 1, HumanCostFactor = 1 },
		};
		gameData.map.techRate = 1;
		gameData.players.Add(player);
		return gameData;
	}

	// Give the player a city whose only commerce is research, so the test does
	// not depend on terrain or buildings.
	private static void AddCityWithBeakers(Player player, int beakersPerTurn) {
		City city = new(new Tile(ID.None("tile")), player, "Roma", ID.None("city"));
		player.cities.Add(city);
		for (int i = 0; i < beakersPerTurn; ++i) {
			city.residents.Add(new CityResident {
				citizenType = new CitizenType { Research = 1 },
				tileWorked = new Tile(ID.None("tile")),
			});
		}
	}

	private static Player MakePlayer() {
		return new Player {
			id = ID.FromString("player-1"),
			civilization = new Civilization("Rome"),
			government = new Government(),
			rules = new Rules { MinimumResearchTime = 4, MaximumResearchTime = 50, FutureTechCost = 400 },
			eraCivilopediaName = EraUtils.MODERN_ERA_CVLPD,
		};
	}

	[Fact]
	public void ResearchContinuesWithFutureTechnologyWhenEveryTechIsKnown() {
		Player player = MakePlayer();
		C7GameData.GameData gameData = MakeGameData(player);
		Tech pottery = new() { id = ID.FromString("pottery-1"), Name = "Pottery", EraCivilopediaName = EraUtils.MODERN_ERA_CVLPD };
		gameData.techs.Add(pottery);
		player.knownTechs.Add(pottery.id);

		PlayerAI.MaybePickTechToResearch(player, gameData);

		Assert.Equal(C7GameData.GameData.FutureTechId, player.currentlyResearchedTech);
		// The future technology is repeatable, so it must not sit on the queue
		// that is saved and reloaded.
		Assert.Empty(player.ResearchQueue);
	}

	[Fact]
	public void CompletingAFutureTechIncrementsTheCounterAndStartsTheNextOne() {
		Player player = MakePlayer();
		C7GameData.GameData gameData = MakeGameData(player);
		AddCityWithBeakers(player, 1);
		EngineStorage.InitializeGameDataForTests(gameData);

		player.SetCurrentlyResearchedTech(C7GameData.GameData.FutureTechId);
		// Already past the maximum research time, so the min/max clamp
		// completes it this turn regardless of the beaker rate.
		player.turnsResearched = gameData.rules.MaximumResearchTime;

		player.DoPerTurnScienceUpdates(gameData);

		Assert.Equal(1, player.futureTechs);
		// Research does not stall: the next future technology begins.
		Assert.Equal(C7GameData.GameData.FutureTechId, player.currentlyResearchedTech);
		// Progress and the turn counter are zeroed, as on every completion.
		Assert.Equal(0, player.beakers);
		Assert.Equal(0, player.turnsResearched);
		// A future technology is never recorded as a known tech.
		Assert.DoesNotContain(C7GameData.GameData.FutureTechId, player.knownTechs);
	}

	[Fact]
	public void FutureTechnologiesAreRepeatable() {
		Player player = MakePlayer();
		C7GameData.GameData gameData = MakeGameData(player);
		AddCityWithBeakers(player, 1);
		EngineStorage.InitializeGameDataForTests(gameData);

		player.SetCurrentlyResearchedTech(C7GameData.GameData.FutureTechId);
		for (int i = 0; i < 3; ++i) {
			player.turnsResearched = gameData.rules.MaximumResearchTime;
			player.DoPerTurnScienceUpdates(gameData);
			Assert.Equal(i + 1, player.futureTechs);
			Assert.Equal(C7GameData.GameData.FutureTechId, player.currentlyResearchedTech);
		}
	}

	[Fact]
	public void CompletingAFutureTechDoesNotAdvanceTheEra() {
		Player player = MakePlayer();
		player.eraCivilopediaName = EraUtils.MIDDLE_AGES_CVLPD;
		C7GameData.GameData gameData = MakeGameData(player);
		AddCityWithBeakers(player, 1);
		EngineStorage.InitializeGameDataForTests(gameData);

		player.SetCurrentlyResearchedTech(C7GameData.GameData.FutureTechId);
		player.turnsResearched = gameData.rules.MaximumResearchTime;

		player.DoPerTurnScienceUpdates(gameData);

		Assert.Equal(1, player.futureTechs);
		Assert.Equal(EraUtils.MIDDLE_AGES_CVLPD, player.eraCivilopediaName);
	}

	[Fact]
	public void ResearchingAnOrdinaryTechStillRecordsItAndLeavesTheCounterAlone() {
		Player player = MakePlayer();
		C7GameData.GameData gameData = MakeGameData(player);
		Tech pottery = new() { id = ID.FromString("pottery-1"), Name = "Pottery", EraCivilopediaName = EraUtils.MODERN_ERA_CVLPD };
		gameData.techs.Add(pottery);
		AddCityWithBeakers(player, 1);
		EngineStorage.InitializeGameDataForTests(gameData);

		PlayerAI.MaybePickTechToResearch(player, gameData);
		Assert.Equal(pottery.id, player.currentlyResearchedTech);
		Assert.Single(player.ResearchQueue);

		player.turnsResearched = gameData.rules.MaximumResearchTime;
		player.DoPerTurnScienceUpdates(gameData);

		Assert.Contains(pottery.id, player.knownTechs);
		Assert.Equal(0, player.futureTechs);
	}

	[Fact]
	public void TheBaseRulesetCarriesTheFutureTechCost() {
		// A non-BIQ game needs the value in ruleset.json; the shipped base-rules
		// value matches conquests.biq (16_science.md §4.2).
		using JsonDocument ruleset = JsonUtils.LoadBaseRuleset();
		JsonElement rules = ruleset.RootElement.GetProperty("rules");
		Assert.True(rules.TryGetProperty("futureTechCost", out JsonElement futureTechCost),
			"ruleset.json must carry futureTechCost for non-BIQ games");
		Assert.Equal(400, futureTechCost.GetInt32());
	}

	[Fact]
	public void FutureTechCounterSurvivesASaveRoundTrip() {
		Player player = new() {
			id = ID.FromString("player-1"),
			civilization = new Civilization("Rome"),
			government = new Government { id = ID.FromString("government-1") },
			futureTechs = 7,
		};

		SavePlayer save = new(player);
		Player restored = save.ToPlayer(new GameMap(),
			new List<Civilization> { player.civilization },
			new List<Government> { player.government },
			new List<Tech>(), player.rules, new HashSet<Alliance>());

		Assert.Equal(7, restored.futureTechs);
	}
}
