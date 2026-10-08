using System.Collections.Generic;
using System.Linq;
using C7Engine;
using C7Engine.Lua;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using QueryCiv3;
using QueryCiv3.Biq;
using Xunit;

namespace EngineTests.GameData;

// Behavioural tests for the happiness inputs added in this change (spec 15):
// the four building fields, the DoublesHappiness wonder flag and the four RULE
// constants. The building-pass tests fail against the old loop, which used only
// contentFacesInCity, and the WLTKD/riot tests could not compile before the new
// rule constants existed.
public class HappinessTest {
	private const string Temple = "Temple";
	private const string Cathedral = "Cathedral";

	private static C7GameData.GameData NewGameData() {
		C7GameData.GameData gameData = new(1234) {
			gameDifficulty = new Difficulty { NumberOfCitizensBornContent = 0 },
			rules = new Rules {
				ChanceOfRioting = 20,
				TurnPenaltyForEachDraftedCitizen = 20,
				TurnPenaltyForEachHurrySacrifice = 20,
				CitizensAffectedByEachHappyFace = 1,
				MinimumPopulationForWeLoveTheKing = 6,
				MaximumLevel1CitySize = 6,
				MaximumLevel2CitySize = 12,
			},
		};
		EngineStorage.InitializeGameDataForTests(gameData);
		return gameData;
	}

	private static Player NewPlayer(C7GameData.GameData gameData) {
		Player player = new Player {
			civilization = new Civilization(),
			government = new Government(),
			rules = gameData.rules,
			isHuman = true,
		};
		gameData.players.Add(player);
		return player;
	}

	private static Tile NewTile(int continent, int food = 0, int commerce = 0) {
		Tile tile = new Tile(ID.None("tile"));
		tile.continent = continent;
		tile.overlayTerrainType = new TerrainType {
			baseFoodProduction = food,
			baseCommerceProduction = commerce,
		};
		return tile;
	}

	private static City NewCity(C7GameData.GameData gameData, Player player, int continent, int population, int food = 0, int commerce = 0) {
		Tile cityTile = NewTile(continent, food, commerce);
		City city = new City(cityTile, player, $"City{player.cities.Count}", ID.None("city"));
		cityTile.cityAtTile = city;
		player.cities.Add(city);

		CitizenType defaultCitizen = new CitizenType { IsDefaultCitizen = true };
		for (int i = 0; i < population; ++i) {
			city.residents.Add(new CityResident {
				citizenType = defaultCitizen,
				tileWorked = NewTile(continent, food, commerce),
				nationality = player.civilization,
				city = city,
			});
		}
		return city;
	}

	private static Building NewBuilding(C7GameData.GameData gameData, string name, int contentFacesInCity = 0,
			int contentFacesAllCities = 0, bool continentalMoodEffects = false, bool greatWonder = false,
			Government requiredGovernment = null, Tech renderedObsoleteBy = null) {
		SaveBuilding saveBuilding = new() {
			name = name,
			contentFacesInCity = contentFacesInCity,
			contentFacesAllCities = contentFacesAllCities,
			continentalMoodEffects = continentalMoodEffects,
			greatWonderProperties = greatWonder ? new SaveBuilding.GreatWonderProperties() : null,
			renderedObsoleteBy = renderedObsoleteBy?.id,
		};
		Building building = new(saveBuilding, gameData);
		building.requiredGovernment = requiredGovernment;
		building.renderedObsoleteBy = renderedObsoleteBy;
		gameData.Buildings.Add(building);
		return building;
	}

	private static int CountMood(City city, CityResident.Mood mood) {
		return city.residents.Count(r => r.mood == mood);
	}

	[Fact]
	public void AllCitiesFacesGoToEveryOtherCityOfTheOwner() {
		var gameData = NewGameData();
		Player player = NewPlayer(gameData);
		City wonderCity = NewCity(gameData, player, continent: 1, population: 4);
		City sameContinent = NewCity(gameData, player, continent: 1, population: 4);
		City otherContinent = NewCity(gameData, player, continent: 2, population: 4);

		// The Hanging Gardens pattern: three content faces in the city that
		// built it, one content face in every other city of the owner.
		Building hangingGardens = NewBuilding(gameData, "The Hanging Gardens",
			contentFacesInCity: 3, contentFacesAllCities: 1, greatWonder: true);
		wonderCity.AddBuilding(hangingGardens);

		wonderCity.RecalculateCitizenMoods(gameData);
		sameContinent.RecalculateCitizenMoods(gameData);
		otherContinent.RecalculateCitizenMoods(gameData);

		// The wonder city gets its own faces plus no other city's.
		Assert.Equal(3, CountMood(wonderCity, CityResident.Mood.Content));
		// Every other city gets one face, on either continent, because the
		// wonder is not ContinentalMoodEffects.
		Assert.Equal(1, CountMood(sameContinent, CityResident.Mood.Content));
		Assert.Equal(1, CountMood(otherContinent, CityResident.Mood.Content));
	}

	[Fact]
	public void ContinentalAllCitiesFacesSkipCitiesOnOtherContinents() {
		var gameData = NewGameData();
		Player player = NewPlayer(gameData);
		City wonderCity = NewCity(gameData, player, continent: 1, population: 4);
		City sameContinent = NewCity(gameData, player, continent: 1, population: 4);
		City otherContinent = NewCity(gameData, player, continent: 2, population: 4);

		// The JS Bach's Cathedral pattern: two content faces in every other
		// city on the same continent only.
		Building jsBach = NewBuilding(gameData, "JS Bach's Cathedral",
			contentFacesInCity: 2, contentFacesAllCities: 2, continentalMoodEffects: true, greatWonder: true);
		wonderCity.AddBuilding(jsBach);

		wonderCity.RecalculateCitizenMoods(gameData);
		sameContinent.RecalculateCitizenMoods(gameData);
		otherContinent.RecalculateCitizenMoods(gameData);

		Assert.Equal(2, CountMood(wonderCity, CityResident.Mood.Content));
		Assert.Equal(2, CountMood(sameContinent, CityResident.Mood.Content));
		Assert.Equal(0, CountMood(otherContinent, CityResident.Mood.Content));
		Assert.Equal(4, CountMood(otherContinent, CityResident.Mood.Unhappy));
	}

	[Fact]
	public void NegativeAllCitiesFacesMakeCitizensUnhappy() {
		var gameData = NewGameData();
		Player player = NewPlayer(gameData);
		City wonderCity = NewCity(gameData, player, continent: 1, population: 4);
		City otherCity = NewCity(gameData, player, continent: 1, population: 4);

		// Pretending a modder gave a wonder two unhappy faces in every other
		// city and no content faces of its own, while the other city has a
		// three-face temple.
		Building unhappyWonder = NewBuilding(gameData, "Unhappy Wonder",
			contentFacesInCity: 0, contentFacesAllCities: -2, greatWonder: true);
		wonderCity.AddBuilding(unhappyWonder);
		wonderCity.AddBuilding(NewBuilding(gameData, Temple, contentFacesInCity: 3));
		Building temple = NewBuilding(gameData, Temple, contentFacesInCity: 3);
		otherCity.AddBuilding(temple);

		// The wonder city has no "other" copy, so it keeps three faces; the
		// other city's temple loses two of them.
		wonderCity.RecalculateCitizenMoods(gameData);
		otherCity.RecalculateCitizenMoods(gameData);

		Assert.Equal(3, CountMood(wonderCity, CityResident.Mood.Content));
		Assert.Equal(1, CountMood(otherCity, CityResident.Mood.Content));
	}

	[Fact]
	public void RequiredGovernmentGatesAGreatWondersFaces() {
		var gameData = NewGameData();
		Player player = NewPlayer(gameData);
		City city = NewCity(gameData, player, continent: 1, population: 4);

		Government communism = new() { name = "Communism" };
		Government despotism = new() { name = "Despotism" };
		player.government = despotism;

		Building secretPolice = NewBuilding(gameData, "Test Wonder",
			contentFacesInCity: 5, greatWonder: true, requiredGovernment: communism);
		city.AddBuilding(secretPolice);

		// Under the wrong government the wonder contributes nothing.
		city.RecalculateCitizenMoods(gameData);
		Assert.Equal(0, CountMood(city, CityResident.Mood.Content));

		// Under the required government it does.
		player.government = communism;
		city.RecalculateCitizenMoods(gameData);
		Assert.Equal(4, CountMood(city, CityResident.Mood.Content));
	}

	[Fact]
	public void RenderedObsoleteByGatesABuildingsFaces() {
		var gameData = NewGameData();
		Player player = NewPlayer(gameData);
		City city = NewCity(gameData, player, continent: 1, population: 4);

		Tech obsoleteTech = new() { id = ID.None("tech") };
		Building temple = NewBuilding(gameData, Temple, contentFacesInCity: 2, renderedObsoleteBy: obsoleteTech);
		city.AddBuilding(temple);

		city.RecalculateCitizenMoods(gameData);
		Assert.Equal(2, CountMood(city, CityResident.Mood.Content));

		// Once the owner knows the technology the building stops working.
		player.knownTechs.Add(obsoleteTech.id);
		city.RecalculateCitizenMoods(gameData);
		Assert.Equal(0, CountMood(city, CityResident.Mood.Content));
	}

	[Fact]
	public void DoublesHappinessDoublesOnlyTheNamedBuildingType() {
		var gameData = NewGameData();
		Player player = NewPlayer(gameData);
		City city = NewCity(gameData, player, continent: 1, population: 12);

		Building temple = NewBuilding(gameData, Temple, contentFacesInCity: 1);
		Building cathedral = NewBuilding(gameData, Cathedral, contentFacesInCity: 3);
		Building colosseum = NewBuilding(gameData, "Colosseum", contentFacesInCity: 2);
		city.AddBuilding(temple);
		city.AddBuilding(cathedral);
		city.AddBuilding(colosseum);

		// The Oracle doubles temples, the Sistine Chapel cathedrals; neither
		// touches the colosseum. Together they leave 1*2 + 3*2 + 2 = 10 faces.
		Building oracle = NewBuilding(gameData, "The Oracle", greatWonder: true);
		oracle.doublesHappinessFor = temple;
		Building sistine = NewBuilding(gameData, "Sistine Chapel", greatWonder: true);
		sistine.doublesHappinessFor = cathedral;
		city.AddBuilding(oracle);
		city.AddBuilding(sistine);

		city.RecalculateCitizenMoods(gameData);
		Assert.Equal(10, CountMood(city, CityResident.Mood.Content));
	}

	[Fact]
	public void DoublesHappinessWonderRequiresItsOwnGovernment() {
		var gameData = NewGameData();
		Player player = NewPlayer(gameData);
		player.government = new Government { name = "Despotism" };
		City city = NewCity(gameData, player, continent: 1, population: 4);

		Building temple = NewBuilding(gameData, Temple, contentFacesInCity: 1);
		city.AddBuilding(temple);

		Building oracle = NewBuilding(gameData, "The Oracle", greatWonder: true,
			requiredGovernment: new Government { name = "Communism" });
		oracle.doublesHappinessFor = temple;
		city.AddBuilding(oracle);

		// The Oracle is not active under this government, so the temple keeps
		// its single face.
		city.RecalculateCitizenMoods(gameData);
		Assert.Equal(1, CountMood(city, CityResident.Mood.Content));
	}

	[Fact]
	public void ChanceOfRiotingControlsTheRiotLootRoll() {
		var gameData = NewGameData();
		Player player = NewPlayer(gameData);
		City city = NewCity(gameData, player, continent: 1, population: 3);

		// A small, non-capital city rolls the chance and then the doubled
		// chance: 20 and 40 in the shipped rules.
		Assert.True(city.RiotLootRollSucceeds(firstRoll: 19, secondRoll: 39));
		Assert.False(city.RiotLootRollSucceeds(firstRoll: 19, secondRoll: 40));
		Assert.False(city.RiotLootRollSucceeds(firstRoll: 20, secondRoll: 0));

		// A city larger than MaximumLevel1CitySize passes without the second
		// roll.
		City largeCity = NewCity(gameData, player, continent: 1, population: 7);
		Assert.True(largeCity.RiotLootRollSucceeds(firstRoll: 19, secondRoll: 99));

		// Capitals never loot.
		city.capital = true;
		Assert.False(city.RiotLootRollSucceeds(firstRoll: 0, secondRoll: 0));
	}

	[Fact]
	public void SecondTurnOfDisorderDestroysAnImprovement() {
		var gameData = NewGameData();
		// Make the roll certain, so the test does not depend on the RNG.
		gameData.rules.ChanceOfRioting = 100;
		Player player = NewPlayer(gameData);
		City city = NewCity(gameData, player, continent: 1, population: 3);

		Building granary = NewBuilding(gameData, "Granary", contentFacesInCity: 0);
		city.AddBuilding(granary);

		// The first turn of disorder only sets the flag; the city must already
		// have been rioting for the loot roll to happen.
		player.RecalculateCitizenMoods(gameData, goIntoDisorderIfUnhappy: true);
		Assert.True(city.isInCivilDisorder);
		Assert.Contains(city.constructed_buildings, cb => cb.building == granary);

		player.RecalculateCitizenMoods(gameData, goIntoDisorderIfUnhappy: true);
		Assert.DoesNotContain(city.constructed_buildings, cb => cb.building == granary);
	}

	[Fact]
	public void RiotLootNeverDestroysWondersOrThePalace() {
		var gameData = NewGameData();
		gameData.rules.ChanceOfRioting = 100;
		Player player = NewPlayer(gameData);
		City city = NewCity(gameData, player, continent: 1, population: 3);

		Building wonder = NewBuilding(gameData, "A Wonder", greatWonder: true);
		Building smallWonder = NewBuilding(gameData, "A Small Wonder");
		smallWonder.isSmallWonder = true;
		Building palace = NewBuilding(gameData, "Palace");
		palace.isCenterOfEmpire = true;
		Building aqueduct = NewBuilding(gameData, "Aqueduct");
		aqueduct.allowsCitySize2 = true;
		city.AddBuilding(wonder);
		city.AddBuilding(smallWonder);
		city.AddBuilding(palace);
		city.AddBuilding(aqueduct);

		player.RecalculateCitizenMoods(gameData, goIntoDisorderIfUnhappy: true);
		player.RecalculateCitizenMoods(gameData, goIntoDisorderIfUnhappy: true);

		Assert.Equal(4, city.constructed_buildings.Count);
	}

	[Fact]
	public void CitizensAffectedByEachHappyFaceDividesLuxuryHappiness() {
		var gameData = NewGameData();
		Player player = NewPlayer(gameData);
		player.luxuryRate = 10;
		City city = NewCity(gameData, player, continent: 1, population: 4, commerce: 2);

		// Four citizens on commerce-2 tiles produce eight commerce, which at a
		// 100% luxury rate is eight faces at a divisor of one and four faces at
		// a divisor of two.
		city.RecalculateCitizenMoods(gameData);
		Assert.Equal(4, CountMood(city, CityResident.Mood.Happy));

		gameData.rules.CitizensAffectedByEachHappyFace = 2;
		city.RecalculateCitizenMoods(gameData);
		Assert.Equal(2, CountMood(city, CityResident.Mood.Happy));
	}

	[Fact]
	public void TurnPenaltyForEachDraftedCitizenMakesContentCitizensUnhappy() {
		var gameData = NewGameData();
		gameData.gameDifficulty.NumberOfCitizensBornContent = 4;
		Player player = NewPlayer(gameData);
		City city = NewCity(gameData, player, continent: 1, population: 4);

		// One drafted citizen's worth of penalty (20 turns) costs one unhappy
		// face, turning one content citizen unhappy.
		city.turnsOfUnhappinessDueToDrafting = 20;
		city.RecalculateCitizenMoods(gameData);
		Assert.Equal(1, CountMood(city, CityResident.Mood.Unhappy));
		Assert.Equal(3, CountMood(city, CityResident.Mood.Content));
	}

	[Fact]
	public void MinimumPopulationForWeLoveTheKingGatesTheCelebration() {
		var gameData = NewGameData();
		gameData.gameDifficulty.NumberOfCitizensBornContent = 6;
		Player player = NewPlayer(gameData);
		player.luxuryRate = 10;

		// Six happy citizens and a positive food surplus celebrate; the same
		// city one citizen below the threshold does not, even though it is just
		// as happy. Both cities exist before the first mood calculation so the
		// cached trade network knows about both of them.
		City bigCity = NewCity(gameData, player, continent: 1, population: 6, food: 2, commerce: 1);
		City smallCity = NewCity(gameData, player, continent: 1, population: 5, food: 2, commerce: 1);

		bigCity.RecalculateCitizenMoods(gameData);
		Assert.Equal(6, CountMood(bigCity, CityResident.Mood.Happy));
		Assert.True(bigCity.isWeLoveTheKingDay);

		smallCity.RecalculateCitizenMoods(gameData);
		Assert.Equal(5, CountMood(smallCity, CityResident.Mood.Happy));
		Assert.False(smallCity.isWeLoveTheKingDay);
	}

	[Fact]
	public void ShippedRulesetCarriesEveryHappinessInput() {
		SaveGame save = LoadShippedRuleset();

		Assert.Equal(20, save.Rules.ChanceOfRioting);
		Assert.Equal(20, save.Rules.TurnPenaltyForEachDraftedCitizen);
		Assert.Equal(1, save.Rules.CitizensAffectedByEachHappyFace);
		Assert.Equal(6, save.Rules.MinimumPopulationForWeLoveTheKing);

		// The whole set of buildings with a happiness field, asserted so a
		// dropped field cannot pass silently.
		Dictionary<string, (int InCity, int AllCities, bool Continental, string Doubles, string Government)> expected = new() {
			["Temple"] = (1, 0, false, null, null),
			["Cathedral"] = (3, 0, false, null, null),
			["Colosseum"] = (2, 0, false, null, null),
			["Shakespeare's Theater"] = (8, 0, false, null, null),
			["The Hanging Gardens"] = (3, 1, false, null, null),
			["The Oracle"] = (0, 0, false, "Temple", null),
			["Sistine Chapel"] = (0, 0, false, "Cathedral", null),
			["JS Bach's Cathedral"] = (2, 2, true, null, null),
			["Cure for Cancer"] = (1, 1, false, null, null),
			["The Mausoleum of Mausollos"] = (3, 0, false, null, null),
			["Secret Police HQ"] = (0, 0, false, null, "Communism"),
		};

		Dictionary<string, SaveBuilding> actual = save.Buildings
			.Where(b => b.contentFacesInCity != 0 || b.contentFacesAllCities != 0 || b.continentalMoodEffects
				|| b.doublesHappinessFor != null || b.requiredGovernment != null)
			.ToDictionary(b => b.name);

		Assert.Equal(expected.Keys.OrderBy(k => k), actual.Keys.OrderBy(k => k));
		foreach (string name in expected.Keys) {
			Assert.Equal(expected[name].InCity, actual[name].contentFacesInCity);
			Assert.Equal(expected[name].AllCities, actual[name].contentFacesAllCities);
			Assert.Equal(expected[name].Continental, actual[name].continentalMoodEffects);
			Assert.Equal(expected[name].Doubles, actual[name].doublesHappinessFor);
			Assert.Equal(expected[name].Government, actual[name].requiredGovernment);
		}
	}

	// Cross-checks the whole building table against the shipped BIQ, so the
	// hand-written ruleset values above cannot drift from the data the original
	// engine reads.
	[SkippableFact]
	public void ShippedRulesetHappinessFieldsMatchTheConquestsBiq() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "Civ3 assets are not available.");

		BiqData biq = BiqData.LoadFile(PathUtils.defaultBicPath);
		SaveGame save = LoadShippedRuleset();
		Dictionary<string, SaveBuilding> byName = save.Buildings.ToDictionary(b => b.name);

		foreach (BLDG bldg in biq.Bldg) {
			if (bldg.Name == "Wealth") {
				continue; // Wealth is an inflow, not a building.
			}
			Assert.True(byName.ContainsKey(bldg.Name), $"ruleset.json is missing {bldg.Name}");
			SaveBuilding building = byName[bldg.Name];

			Assert.Equal(bldg.ContentFaces - bldg.UnhappyFaces, building.contentFacesInCity);
			Assert.Equal(bldg.ContentFacesAllCities - bldg.UnhappyFacesAllCities, building.contentFacesAllCities);
			Assert.Equal(bldg.ContinentalMoodEffects, building.continentalMoodEffects);
			Assert.Equal(bldg.DoublesHappiness >= 0 ? biq.Bldg[bldg.DoublesHappiness].Name : null,
				building.doublesHappinessFor);
			Assert.Equal(bldg.RequiredGovernment >= 0 ? biq.Govt[bldg.RequiredGovernment].Name : null,
				building.requiredGovernment);
			Assert.Equal(bldg.RenderedObsoleteBy >= 0 ? save.Techs[bldg.RenderedObsoleteBy].id : null,
				building.renderedObsoleteBy);
		}
	}

	private static SaveGame LoadShippedRuleset() {
		GameMode.Config config = new("civ3");
		return GameMode.Load(PathUtils.GameModesDir, config).GetSave();
	}
}
