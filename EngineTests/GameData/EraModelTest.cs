using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using C7Engine;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using QueryCiv3;
using Xunit;

namespace EngineTests.GameData;

// The era model is rules data, not four constants: the BIQ's ERAS section for a
// BIQ game, or the "eras" array of ruleset.json for a game that has no BIQ. A
// scenario can carry any number of eras, so the era index, the next/previous
// era, the display name and the tech-box art all resolve through the loaded
// list. This pins the shipped four-era list from both paths and then proves the
// lookups follow a list of a different length.
public class EraModelTest : IClassFixture<SaveGameFixture> {
	private readonly SaveGameFixture fixture;

	public EraModelTest(SaveGameFixture fixture) {
		this.fixture = fixture;
	}

	private static string ScenarioPath =>
		Path.Combine(Civ3Location.GetCiv3Path(), "Conquests", "Conquests", "1 Mesopotamia.biq");

	private static string ConquestsBiqPath =>
		Path.Combine(Civ3Location.GetCiv3Path(), "Conquests", "conquests.biq");

	private static List<Era> ShippedBiqEras() {
		return ImportCiv3.BuildEras(BiqData.LoadFile(ConquestsBiqPath));
	}

	private static List<Era> Shipped() {
		return ShippedEras.Load();
	}

	private static string[] CivilopediaNames(IEnumerable<Era> eras) {
		return eras.Select(e => e.civilopediaName).ToArray();
	}

	private static string[] Names(IEnumerable<Era> eras) {
		return eras.Select(e => e.name).ToArray();
	}

	private static string[] ArtNames(IEnumerable<Era> eras) {
		return eras.Select(e => e.artName).ToArray();
	}

	// ---------------------------------------------------------------------
	// The shipped list, reached through the ruleset (non-BIQ) path
	// ---------------------------------------------------------------------

	[Fact]
	public void RulesetJsonCarriesTheShippedEraList() {
		// A non-BIQ game has no BIQ to read the eras from, so the complete set
		// has to be in ruleset.json. This never skips.
		using JsonDocument ruleset = JsonUtils.LoadBaseRuleset();
		JsonElement eras = ruleset.RootElement.GetProperty("eras");
		Assert.Equal(JsonValueKind.Array, eras.ValueKind);
		Assert.Equal(4, eras.GetArrayLength());

		string[] expectedCivilopediaNames = { "ERAS_Ancient_Times", "ERAS_Middle_Ages", "ERAS_Industrial_Age", "ERAS_Modern_Era" };
		string[] expectedNames = { "Ancient Times", "Middle Ages", "Industrial Ages", "Modern Times" };
		string[] expectedArtNames = { "ancient", "middle", "industrial", "modern" };

		for (int i = 0; i < 4; i++) {
			JsonElement era = eras[i];
			Assert.Equal(expectedCivilopediaNames[i], era.GetProperty("civilopediaName").GetString());
			Assert.Equal(expectedNames[i], era.GetProperty("name").GetString());
			Assert.Equal(expectedArtNames[i], era.GetProperty("artName").GetString());
		}
	}

	[Fact]
	public void RulesetErasReachTheGameData() {
		// The ruleset's list is what a real game runs on: SaveGame carries it
		// into GameData, and a new game's players start in its first era.
		C7GameData.GameData gameData = fixture.saveGame.ToGameData(fixture.behaviors);

		Assert.Equal(4, gameData.eras.Count);
		Assert.Equal(
			new[] { "ERAS_Ancient_Times", "ERAS_Middle_Ages", "ERAS_Industrial_Age", "ERAS_Modern_Era" },
			CivilopediaNames(gameData.eras));
		Assert.All(gameData.players, p => Assert.Equal(gameData.eras[0].civilopediaName, p.eraCivilopediaName));
	}

	// ---------------------------------------------------------------------
	// The same list, reached through the BIQ path
	// ---------------------------------------------------------------------

	[SkippableFact]
	public void ImportedEraListMatchesTheShippedBiq() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");

		// Measured from the shipped conquests.biq with the QueryCiv3 probe:
		// four ERAS records, in this order, with these names and civilopedia
		// keys. Nothing enforces four eras in OpenCiv3 - this is the file's
		// content, not a rule of the model.
		List<Era> eras = ShippedBiqEras();

		Assert.Equal(4, eras.Count);
		Assert.Equal(
			new[] { "ERAS_Ancient_Times", "ERAS_Middle_Ages", "ERAS_Industrial_Age", "ERAS_Modern_Era" },
			CivilopediaNames(eras));
		Assert.Equal(new[] { "Ancient Times", "Middle Ages", "Industrial Ages", "Modern Times" }, Names(eras));
		Assert.Equal(new[] { "ancient", "middle", "industrial", "modern" }, ArtNames(eras));
	}

	[SkippableFact]
	public void RulesetEraListMatchesTheShippedBiq() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");

		// The two rules sources must not drift: a generated game and a BIQ game
		// have to play the same four eras.
		List<Era> fromBiq = ShippedBiqEras();
		List<Era> fromRuleset = Shipped();

		Assert.Equal(fromBiq.Count, fromRuleset.Count);
		for (int i = 0; i < fromBiq.Count; i++) {
			Assert.Equal(fromBiq[i].civilopediaName, fromRuleset[i].civilopediaName);
			Assert.Equal(fromBiq[i].name, fromRuleset[i].name);
			Assert.Equal(fromBiq[i].artName, fromRuleset[i].artName);
		}
	}

	[SkippableFact]
	public void ImportBiqCarriesTheEraList() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");

		// The whole BIQ import path, not just the helper: a scenario with a map
		// runs ImportBiq and must end up with its ERAS records on the SaveGame.
		EngineStorage.animationsEnabled = false;
		SaveGame save = ImportCiv3.ImportBiq(ScenarioPath, PathUtils.defaultBicPath, GetPediaIconsPath("Conquests/Conquests"));

		Assert.Equal(4, save.Eras.Count);
		Assert.Equal(
			new[] { "ERAS_Ancient_Times", "ERAS_Middle_Ages", "ERAS_Industrial_Age", "ERAS_Modern_Era" },
			CivilopediaNames(save.Eras));
	}

	// ---------------------------------------------------------------------
	// The lookups: data-driven, and unchanged for the shipped four eras
	// ---------------------------------------------------------------------

	[Fact]
	public void ShippedEraLookupsMatchTheOldFourEraBehaviour() {
		List<Era> eras = Shipped();

		// The index of every shipped era, in order.
		for (int i = 0; i < eras.Count; i++) {
			Assert.Equal(i, EraUtils.GetEraIndex(eras, eras[i].civilopediaName));
			Assert.Equal(eras[i].civilopediaName, EraUtils.EraIndexToEra(eras, i));
		}

		// The old four-era functions clamped at both ends, and so do these.
		Assert.Equal(eras[0].civilopediaName, EraUtils.EraIndexToEra(eras, -1));
		Assert.Equal(eras[^1].civilopediaName, EraUtils.EraIndexToEra(eras, eras.Count));
		Assert.Equal(eras[1].civilopediaName, EraUtils.GetNextEraNameByIndex(eras, 0));
		Assert.Equal(eras[0].civilopediaName, EraUtils.GetPreviousEraNameByIndex(eras, 1));

		// Next/previous stop at the ends of the list.
		Assert.Equal(eras[0].civilopediaName, EraUtils.GetPreviousEraNameByIndex(eras, 0));
		Assert.Equal(eras[^1].civilopediaName, EraUtils.GetNextEraNameByIndex(eras, eras.Count - 1));

		// An era the rules do not describe has no index.
		Assert.Equal(-1, EraUtils.GetEraIndex(eras, "ERAS_Not_An_Era"));
	}

	[Fact]
	public void TheStartingEraIsTheFirstEraOfTheRules() {
		// A new game starts in the head of the rules' list, whatever length
		// that list has.
		Assert.Equal("ERAS_Ancient_Times", EraUtils.GetStartingEraCivilopediaName(Shipped()));
		Assert.Equal("ERA_Alpha", EraUtils.GetStartingEraCivilopediaName(SixEras()));

		// A rules file with no era list has no starting era. It does not fall
		// back to one of the shipped four, and it does not index the empty list.
		Assert.Null(EraUtils.GetStartingEraCivilopediaName(new List<Era>()));
		Assert.Null(EraUtils.GetStartingEraCivilopediaName(null));
	}

	[Fact]
	public void ARulesFileWithNoErasIsRejectedWithAnActionableMessage() {
		// A game mode whose ruleset.json has no "eras" array cannot start a
		// game: there is no era to put the players in. Setting up a game used
		// to index the empty list and throw ArgumentOutOfRangeException (the
		// List indexer's "Index was out of range") from deep inside the player
		// loop. It must name the missing block instead.
		//
		// The ruleset (not the BIQ) path is the one that can be era-less: a
		// BIQ always carries four ERAS records, and the format rejects any
		// other count with BIC_ERROR_ERAS (spec 28_formats.md).
		SaveGame save = fixture.saveGame.Clone();
		save.Eras.Clear();
		save.Players.Clear();

		GameSetup gameSetup = new() {
			playerCivilization = save.Civilizations.Find(c => !c.isBarbarian),
			difficulty = save.Difficulties.First(),
			worldCharacteristics = new WorldCharacteristics(save),
			opponents = [],
			victoryConditions = new VictoryConditions(),
		};

		// The clone already has a generated map, so Populate reaches the player
		// setup without regenerating the world.
		InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => gameSetup.Populate(save));
		Assert.Contains("define no eras", error.Message);
		Assert.Contains("ruleset.json", error.Message);
		Assert.Contains("\"eras\"", error.Message);

		// The game failed before it was half-built.
		Assert.Empty(save.Players);
	}

	[Fact]
	public void EraLookupsFollowASixEraRulesList() {
		List<Era> eras = SixEras();

		for (int i = 0; i < eras.Count; i++) {
			Assert.Equal(i, EraUtils.GetEraIndex(eras, eras[i].civilopediaName));
			Assert.Equal(eras[i].civilopediaName, EraUtils.EraIndexToEra(eras, i));
			Assert.Equal(eras[i].artName, EraUtils.GetEraArtName(eras, eras[i].civilopediaName));
		}

		// The clamps track the length of the list, not a four-era constant.
		Assert.Equal(5, EraUtils.GetEraIndex(eras, "ERA_Zeta"));
		Assert.Equal(eras[5].civilopediaName, EraUtils.EraIndexToEra(eras, 6));
		Assert.Equal(eras[5].civilopediaName, EraUtils.EraIndexToEra(eras, 99));
		Assert.Equal(eras[5].civilopediaName, EraUtils.GetNextEraNameByIndex(eras, 5));
		Assert.Equal(eras[4].civilopediaName, EraUtils.GetNextEraNameByIndex(eras, 3));
	}

	// ---------------------------------------------------------------------
	// A different number of eras actually works
	// ---------------------------------------------------------------------

	// Six eras with names and civilopedia keys no shipped table knows: if any
	// code still resolved eras from four literals, none of these would resolve.
	private static List<Era> SixEras() {
		return new List<Era> {
			new Era { civilopediaName = "ERA_Alpha", name = "Alpha Age", artName = "art-0" },
			new Era { civilopediaName = "ERA_Beta", name = "Beta Age", artName = "art-1" },
			new Era { civilopediaName = "ERA_Gamma", name = "Gamma Age", artName = "art-2" },
			new Era { civilopediaName = "ERA_Delta", name = "Delta Age", artName = "art-3" },
			new Era { civilopediaName = "ERA_Epsilon", name = "Epsilon Age", artName = "art-4" },
			new Era { civilopediaName = "ERA_Zeta", name = "Zeta Age", artName = "art-5" },
		};
	}

	private static C7GameData.GameData MakeGame(List<Era> eras) {
		C7GameData.GameData gameData = new(1234);
		gameData.map.techRate = 1;
		gameData.gameDifficulty = new Difficulty { HumanCostFactor = 10, AiCostFactor = 10 };
		gameData.rules = new Rules {
			MinimumResearchTime = 4,
			MaximumResearchTime = 50,
			MaximumLevel1CitySize = 6,
			MaximumLevel2CitySize = 12,
		};
		gameData.eras = eras;
		return gameData;
	}

	private static Player MakePlayer(C7GameData.GameData gameData, string eraCivilopediaName) {
		Player player = new() {
			id = ID.None("player"),
			isHuman = true,
			civilization = new Civilization(),
			government = new Government(),
			rules = gameData.rules,
			eraCivilopediaName = eraCivilopediaName,
		};
		gameData.players.Add(player);
		return player;
	}

	// A city whose only output is research, so the turn is deterministic.
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

	// One required technology per era, so completing the era's technology is
	// exactly what lets the player advance out of it.
	private static List<Tech> MakeEraTechs(C7GameData.GameData gameData, List<Era> eras) {
		List<Tech> techs = new();
		for (int i = 0; i < eras.Count; i++) {
			Tech tech = new() {
				id = gameData.ids.CreateID("tech"),
				Name = $"Tech {i}",
				Cost = 2,
				EraCivilopediaName = eras[i].civilopediaName,
				RequiredForEraAdvancement = true,
			};
			gameData.techs.Add(tech);
			techs.Add(tech);
		}
		return techs;
	}

	private static void CompleteTech(C7GameData.GameData gameData, Player player, Tech tech) {
		player.SetCurrentlyResearchedTech(tech.id);
		// Equal to the tech's remaining cost, so the completion test fires on
		// this turn without depending on the per-turn rate.
		player.beakers = gameData.TechCostFor(tech, player);
		player.turnsResearched = gameData.rules.MinimumResearchTime;
		player.DoPerTurnScienceUpdates(gameData);
	}

	[Fact]
	public void EraAdvancementFollowsASixEraRulesList() {
		List<Era> eras = SixEras();
		C7GameData.GameData gameData = MakeGame(eras);
		Player player = MakePlayer(gameData, eras[0].civilopediaName);
		List<Tech> techs = MakeEraTechs(gameData, eras);
		AddCityWithBeakers(player, 1);
		EngineStorage.InitializeGameDataForTests(gameData);

		// Completing each era's required technology advances the player to the
		// next era of the list, all six of them.
		for (int i = 0; i < eras.Count; i++) {
			Assert.Equal(eras[i].civilopediaName, player.eraCivilopediaName);
			CompleteTech(gameData, player, techs[i]);
			Assert.Contains(techs[i].id, player.knownTechs);
		}

		// The last era is the last: a completion there cannot advance past the
		// end of the list.
		Assert.Equal(eras[^1].civilopediaName, player.eraCivilopediaName);
		Assert.Equal(0, player.futureTechs);
	}

	[Fact]
	public void PlayerEraIndexReadsTheRulesList() {
		List<Era> eras = SixEras();
		C7GameData.GameData gameData = MakeGame(eras);
		Player player = MakePlayer(gameData, eras[0].civilopediaName);
		EngineStorage.InitializeGameDataForTests(gameData);

		for (int i = 0; i < eras.Count; i++) {
			player.eraCivilopediaName = eras[i].civilopediaName;
			Assert.Equal(i, player.EraIndex());
		}

		// An era outside the rules has no index, and does not silently fall
		// back to one of the four shipped eras.
		player.eraCivilopediaName = "ERAS_Modern_Era";
		Assert.Equal(-1, player.EraIndex());
	}

	[Fact]
	public void AnUnmoddedGameShippedEraAdvancementIsUnchanged() {
		// The shipped list resolves ancient -> middle -> industrial -> modern
		// and stops there, exactly as the four literals did.
		List<Era> eras = Shipped();

		Assert.Equal(eras[1].civilopediaName, EraUtils.GetNextEraNameByIndex(eras, EraUtils.GetEraIndex(eras, eras[0].civilopediaName)));
		Assert.Equal(eras[2].civilopediaName, EraUtils.GetNextEraNameByIndex(eras, EraUtils.GetEraIndex(eras, eras[1].civilopediaName)));
		Assert.Equal(eras[3].civilopediaName, EraUtils.GetNextEraNameByIndex(eras, EraUtils.GetEraIndex(eras, eras[2].civilopediaName)));
		Assert.Equal(eras[3].civilopediaName, EraUtils.GetNextEraNameByIndex(eras, EraUtils.GetEraIndex(eras, eras[3].civilopediaName)));

		// The art mapping the tech boxes and the science advisor read is the
		// same four shipped texture sets, now keyed on the era's data.
		Assert.Equal("ancient", EraUtils.GetEraArtName(eras, "ERAS_Ancient_Times"));
		Assert.Equal("middle", EraUtils.GetEraArtName(eras, "ERAS_Middle_Ages"));
		Assert.Equal("industrial", EraUtils.GetEraArtName(eras, "ERAS_Industrial_Age"));
		Assert.Equal("modern", EraUtils.GetEraArtName(eras, "ERAS_Modern_Era"));
		// An era with no art in the rules falls back to the first shipped set,
		// the way the old default arm did.
		Assert.Equal("ancient", EraUtils.GetEraArtName(eras, "ERAS_Not_An_Era"));
	}

	private static Func<string, string> GetPediaIconsPath(string subfolder) {
		return (relativeModPath) => {
			if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
				relativeModPath = relativeModPath.Replace("\\conquests\\", "/Conquests/");
			}

			return Path.GetFullPath(Path.Combine(
				Civ3Location.GetCiv3Path(), subfolder, relativeModPath, "Text", "PediaIcons.txt"));
		};
	}
}
