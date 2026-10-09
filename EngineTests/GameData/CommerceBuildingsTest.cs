using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using C7Engine;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using QueryCiv3;
using Xunit;

namespace EngineTests.GameData;

/// <summary>
/// The commerce half of the building multipliers (spec 14 §4.2 step 5) and the
/// Wealth build's shields-to-gold conversion (step 6).
///
/// The three multipliers stack additively inside one integer division: each
/// counter starts at 2 (= 100 %) and every non-obsolete building with the
/// matching flag adds 1, so Bank + Stock Exchange is 2x tax, not 1.5 * 1.5 =
/// 2.25x. The science half is covered by ResearchTest; these tests pin the
/// luxury and tax halves to the same shape.
/// </summary>
public class CommerceBuildingsTest : IClassFixture<SaveGameFixture> {
	private readonly SaveGameFixture fixture;
	private readonly C7GameData.GameData shipped;

	private const string SkipReason = "No Civ3 install found.";

	public CommerceBuildingsTest(SaveGameFixture fixture) {
		this.fixture = fixture;
		shipped = fixture.saveGame.ToGameData(fixture.behaviors);
	}

	// ---------- the shipped flag sets ----------

	[Theory]
	[InlineData("Marketplace")]
	[InlineData("Bank")]
	[InlineData("Stock Exchange")]
	public void ShippedRulesetMarksTheCommerceBuildings(string name) {
		SaveBuilding building = fixture.saveGame.Buildings.Find(b => b.name == name);

		Assert.NotNull(building);
		Assert.Contains(SaveBuilding.Flag.Plus50PercentCommerce, building.flags);
	}

	// Measured from BLDG of the shipped conquests.biq with the QueryCiv3 probe:
	// Buildings 4, 9 and 76 (Marketplace, Bank, Stock Exchange) set Flags[0] bit
	// 4 (0x10, Plus50PercentCommerce) and nothing else in the file sets it. The
	// spec's prose names the Marketplace as the +50% *luxury* building, but the
	// shipped record carries the tax bit; the game text agrees ("The
	// Marketplace increases tax revenue ... by 50%").
	[Fact]
	public void OnlyThreeShippedBuildingsCarryTheCommerceFlag() {
		List<string> flagged = fixture.saveGame.Buildings
			.Where(b => b.flags.Contains(SaveBuilding.Flag.Plus50PercentCommerce))
			.Select(b => b.name)
			.OrderBy(n => n)
			.ToList();

		Assert.Equal(new List<string> { "Bank", "Marketplace", "Stock Exchange" }, flagged);
	}

	// No building in conquests.biq sets Flags[0] bit 3 (0x8,
	// Plus50PercentLuxury). The flag is real - the Mesopotamia conquest sets it
	// on the Amphitheatre, which ImportBiqCarriesTheLuxuryFlag covers - but the
	// base ruleset must ship it on nothing.
	[Fact]
	public void NoShippedBuildingCarriesTheLuxuryFlag() {
		Assert.DoesNotContain(
			fixture.saveGame.Buildings,
			b => b.flags.Contains(SaveBuilding.Flag.Plus50PercentLuxury));
	}

	[Fact]
	public void CommerceFlagsReachTheRuntimeBuilding() {
		Assert.True(shipped.Buildings.Find(b => b.name == "Marketplace").plus50PercentCommerce);
		Assert.True(shipped.Buildings.Find(b => b.name == "Bank").plus50PercentCommerce);
		Assert.True(shipped.Buildings.Find(b => b.name == "Stock Exchange").plus50PercentCommerce);

		// A research and a commerce flag are not confused with each other.
		Assert.False(shipped.Buildings.Find(b => b.name == "Library").plus50PercentCommerce);
		Assert.False(shipped.Buildings.Find(b => b.name == "Marketplace").plus50PercentLuxury);
	}

	// The Wealth build is modelled as an inflow rather than a building (it is
	// skipped by ImportBuildings), so the Capitalization marker has to ride on
	// the inflow for both the generated (ruleset.json) and BIQ-imported games.
	[Fact]
	public void TheWealthInflowCarriesTheCapitalizationFlag() {
		SaveInflow saveInflow = fixture.saveGame.Inflows.Find(i => i.name == "Wealth");
		Assert.NotNull(saveInflow);
		Assert.True(saveInflow.capitalization);

		Inflow inflow = shipped.Inflows.Find(i => i.name == "Wealth");
		Assert.NotNull(inflow);
		Assert.True(inflow.capitalization);
	}

	[Fact]
	public void OnlyTheWealthInflowCarriesTheCapitalizationFlag() {
		List<string> flagged = fixture.saveGame.Inflows
			.Where(i => i.capitalization)
			.Select(i => i.name)
			.ToList();

		Assert.Equal(new List<string> { "Wealth" }, flagged);
	}

	// ---------- the arithmetic, on a city with a controlled yield ----------

	// A city whose centre yields one commerce and one shield, plus one worked
	// tile that supplies the rest, so both splits are deterministic.
	private static City MakeCity(C7GameData.GameData gameData, Player player, int commerce, int shields, params Building[] buildings) {
		Tile center = new(ID.None("city-tile")) { overlayTerrainType = new TerrainType { Key = "grassland" } };
		City city = new(center, player, "Test City", ID.None("city"));
		center.cityAtTile = city;
		player.cities.Add(city);
		gameData.cities.Add(city);

		Tile worked = new(ID.None("worked-tile")) {
			overlayTerrainType = new TerrainType {
				Key = "grassland",
				baseCommerceProduction = commerce - 1,
				baseShieldProduction = shields - 1,
			},
		};
		city.residents.Add(new CityResident { citizenType = new CitizenType(), tileWorked = worked });

		foreach (Building building in buildings) {
			city.constructed_buildings.Add(new CityBuilding { building = building, builtByPlayer = player });
		}

		return city;
	}

	private static C7GameData.GameData MakeGame(params Tech[] techs) {
		C7GameData.GameData gameData = new(1234);
		gameData.rules = new Rules {
			MaximumLevel1CitySize = 6,
			MaximumLevel2CitySize = 12,
			ShieldCostPerGold = 4,
		};
		gameData.techs.AddRange(techs);
		return gameData;
	}

	private static Player MakePlayer(C7GameData.GameData gameData) {
		Player player = new() {
			id = ID.None("player"),
			isHuman = true,
			civilization = new Civilization(),
			government = new Government(),
			rules = gameData.rules,
		};
		gameData.players.Add(player);
		return player;
	}

	// The two ways a building can reach the runtime flag: a shipped one (its
	// SaveBuilding flags) or a modded one built here.
	private Building ShippedBuilding(string name) {
		return shipped.Buildings.Find(b => b.name == name);
	}

	private static Building MakeBuilding(C7GameData.GameData gameData, string name, params SaveBuilding.Flag[] flags) {
		return new Building(new SaveBuilding { name = name, flags = [.. flags] }, gameData);
	}

	private int TaxesFor(int commerce, int scienceRate, int luxuryRate, params Building[] buildings) {
		C7GameData.GameData gameData = MakeGame();
		Player player = MakePlayer(gameData);
		player.scienceRate = scienceRate;
		player.luxuryRate = luxuryRate;
		City city = MakeCity(gameData, player, commerce, 0, buildings);
		EngineStorage.InitializeGameDataForTests(gameData);
		return city.CurrentCommerceYield().taxes;
	}

	private int LuxuryFor(int commerce, int luxuryRate, params Building[] buildings) {
		C7GameData.GameData gameData = MakeGame();
		Player player = MakePlayer(gameData);
		player.scienceRate = 0;
		player.luxuryRate = luxuryRate;
		City city = MakeCity(gameData, player, commerce, 0, buildings);
		EngineStorage.InitializeGameDataForTests(gameData);
		return city.CurrentCommerceYield().happiness;
	}

	// A commerce-8 city at a 50/50 science/tax split makes four beakers and four
	// tax before any building. The luxury half below uses a 100% luxury split,
	// i.e. four luxury before any building.
	[Fact]
	public void BankGivesOneAndAHalfTimesTheTax() {
		Assert.Equal(4, TaxesFor(8, scienceRate: 5, luxuryRate: 0));
		Assert.Equal(6, TaxesFor(8, scienceRate: 5, luxuryRate: 0, ShippedBuilding("Bank")));
	}

	[Fact]
	public void MarketplaceGivesOneAndAHalfTimesTheTax() {
		// The shipped Marketplace carries the tax flag, not the luxury flag.
		Assert.Equal(6, TaxesFor(8, scienceRate: 5, luxuryRate: 0, ShippedBuilding("Marketplace")));
	}

	[Fact]
	public void TwoCommerceBuildingsStackAdditivelyInHalves() {
		Building bank = ShippedBuilding("Bank");
		Building stockExchange = ShippedBuilding("Stock Exchange");

		// Two +50% buildings are 2x, not 1.5 * 1.5 = 2.25x (which would be 9).
		Assert.Equal(8, TaxesFor(8, scienceRate: 5, luxuryRate: 0, bank, stockExchange));
		// All three shipped tax buildings are 2.5x, not 1.5^3 = 3.375x.
		Assert.Equal(10, TaxesFor(8, scienceRate: 5, luxuryRate: 0,
			bank, stockExchange, ShippedBuilding("Marketplace")));
	}

	[Fact]
	public void ALuxuryBuildingGivesOneAndAHalfTimesTheLuxury() {
		Building luxury = MakeBuilding(
			MakeGame(), "Test Luxury Building", SaveBuilding.Flag.Plus50PercentLuxury);

		Assert.Equal(4, LuxuryFor(8, luxuryRate: 5));
		Assert.Equal(6, LuxuryFor(8, luxuryRate: 5, luxury));
	}

	[Fact]
	public void TwoLuxuryBuildingsStackAdditivelyInHalves() {
		C7GameData.GameData unused = MakeGame();
		Building first = MakeBuilding(unused, "First Luxury Building", SaveBuilding.Flag.Plus50PercentLuxury);
		Building second = MakeBuilding(unused, "Second Luxury Building", SaveBuilding.Flag.Plus50PercentLuxury);

		// 2x, not 1.5 * 1.5 = 2.25x (which would be 9).
		Assert.Equal(8, LuxuryFor(8, luxuryRate: 5, first, second));
	}

	[Fact]
	public void TheLuxuryAndTaxMultipliersDoNotFeedEachOther() {
		// Bank doubles no luxury and the luxury building doubles no tax: each
		// counter is independent, and the tax remainder is still taken from the
		// *unmultiplied* luxury share (spec 14 §4.2 step 4).
		Building luxury = MakeBuilding(
			MakeGame(), "Test Luxury Building", SaveBuilding.Flag.Plus50PercentLuxury);

		Assert.Equal(4, TaxesFor(8, scienceRate: 0, luxuryRate: 5, luxury));
		Assert.Equal(6, LuxuryFor(8, luxuryRate: 5, luxury));
	}

	// ---------- the RenderedObsoleteBy skip ----------

	[Fact]
	public void AnObsoleteBuildingDoesNotContributeToTheTaxMultiplier() {
		C7GameData.GameData gameData = MakeGame();
		Player player = MakePlayer(gameData);
		player.scienceRate = 5;
		player.luxuryRate = 0;

		Building bank = ShippedBuilding("Bank");
		bank.renderedObsoleteBy = new Tech { id = ID.None("obsoleting-tech") };
		player.knownTechs.Add(bank.renderedObsoleteBy.id);

		City city = MakeCity(gameData, player, 8, 0, bank);
		EngineStorage.InitializeGameDataForTests(gameData);

		// The flag is present, but the owner knows the obsoleting technology.
		Assert.True(bank.plus50PercentCommerce);
		Assert.Equal(4, city.CurrentCommerceYield().taxes);
	}

	[Fact]
	public void AnObsoleteBuildingDoesNotContributeToTheLuxuryMultiplier() {
		C7GameData.GameData gameData = MakeGame();
		Player player = MakePlayer(gameData);
		player.scienceRate = 0;
		player.luxuryRate = 5;

		Building luxury = MakeBuilding(
			gameData, "Test Luxury Building", SaveBuilding.Flag.Plus50PercentLuxury);
		luxury.renderedObsoleteBy = new Tech { id = ID.None("obsoleting-tech") };
		player.knownTechs.Add(luxury.renderedObsoleteBy.id);

		City city = MakeCity(gameData, player, 8, 0, luxury);
		EngineStorage.InitializeGameDataForTests(gameData);

		Assert.True(luxury.plus50PercentLuxury);
		Assert.Equal(4, city.CurrentCommerceYield().happiness);
	}

	[Fact]
	public void AnObsoleteBuildingDoesNotContributeToTheScienceMultiplier() {
		// The same skip belongs to the research counter, which the commerce
		// refactor shares.
		C7GameData.GameData gameData = MakeGame();
		Player player = MakePlayer(gameData);
		player.scienceRate = 5;
		player.luxuryRate = 0;

		Building library = ShippedBuilding("Library");
		library.renderedObsoleteBy = new Tech { id = ID.None("obsoleting-tech") };
		player.knownTechs.Add(library.renderedObsoleteBy.id);

		City city = MakeCity(gameData, player, 8, 0, library);
		EngineStorage.InitializeGameDataForTests(gameData);

		Assert.Equal(4, city.CurrentCommerceYield().beakers);
	}

	// ---------- Wealth (spec 14 §4.2 step 6) ----------

	private static Inflow WealthInflow(SaveGameFixture fixture) {
		return fixture.saveGame.ToGameData(fixture.behaviors).Inflows.Find(i => i.name == "Wealth");
	}

	private int WealthGoldFor(int shields, params Tech[] techs) {
		C7GameData.GameData gameData = MakeGame(techs);
		Player player = MakePlayer(gameData);
		City city = MakeCity(gameData, player, commerce: 1, shields: shields);
		city.SetItemBeingProduced(WealthInflow(fixture));
		EngineStorage.InitializeGameDataForTests(gameData);
		return city.CurrentCommerceYield().wealth;
	}

	private static Tech MakeWealthTech(string id) {
		return new Tech { id = ID.None(id), Name = id, DoublesWealthProduction = true };
	}

	[Fact]
	public void WealthConvertsNetShieldsToGoldAtFourShieldsPerGold() {
		// No item is being produced, so the Wealth conversion does not run even
		// though the city has plenty of shields.
		C7GameData.GameData idle = MakeGame();
		Player idlePlayer = MakePlayer(idle);
		MakeCity(idle, idlePlayer, commerce: 1, shields: 8);
		EngineStorage.InitializeGameDataForTests(idle);
		Assert.Equal(0, idle.cities[0].CurrentCommerceYield().wealth);

		// 8 net shields / 4 = 2 gold.
		Assert.Equal(2, WealthGoldFor(8));
	}

	[Fact]
	public void AShortfallBelowOneGoldIsWorthExactlyOneGold() {
		// 1, 2 and 3 net shields would truncate to zero gold, so the original
		// returns exactly one gold (spec 14 §4.2 step 6: a remainder of at
		// least one shield yields exactly one gold). It is *not* a ceiling:
		// 5, 6 and 7 shields still give the single gold that 4/4 gives.
		Assert.Equal(1, WealthGoldFor(1));
		Assert.Equal(1, WealthGoldFor(2));
		Assert.Equal(1, WealthGoldFor(3));
		Assert.Equal(1, WealthGoldFor(4));
		Assert.Equal(1, WealthGoldFor(5));
		Assert.Equal(1, WealthGoldFor(7));
		Assert.Equal(2, WealthGoldFor(8));
		Assert.Equal(3, WealthGoldFor(12));
	}

	[Fact]
	public void TheWealthTechnologyHalvesTheShieldsPerGoldRate() {
		Tech economics = MakeWealthTech("tech-economics");

		// max(1, 4/2) = 2 shields per gold, so twice the gold.
		C7GameData.GameData gameData = MakeGame(economics);
		Player player = MakePlayer(gameData);
		player.knownTechs.Add(economics.id);
		City city = MakeCity(gameData, player, commerce: 1, shields: 4);
		city.SetItemBeingProduced(WealthInflow(fixture));
		EngineStorage.InitializeGameDataForTests(gameData);

		Assert.Equal(2, city.CurrentCommerceYield().wealth);

		// An unowned technology does not help.
		C7GameData.GameData withoutTech = MakeGame(economics);
		Player otherPlayer = MakePlayer(withoutTech);
		City otherCity = MakeCity(withoutTech, otherPlayer, commerce: 1, shields: 4);
		otherCity.SetItemBeingProduced(WealthInflow(fixture));
		EngineStorage.InitializeGameDataForTests(withoutTech);

		Assert.Equal(1, otherCity.CurrentCommerceYield().wealth);
	}

	[Fact]
	public void WealthYieldsNothingWhenTheCityHasNoNetShields() {
		// Civil disorder zeroes the city's useful production, so the guard on
		// net shields > 0 must return no gold rather than the one-gold minimum.
		C7GameData.GameData gameData = MakeGame();
		Player player = MakePlayer(gameData);
		City city = MakeCity(gameData, player, commerce: 1, shields: 4);
		city.SetItemBeingProduced(WealthInflow(fixture));
		city.isInCivilDisorder = true;
		EngineStorage.InitializeGameDataForTests(gameData);

		Assert.Equal(0, city.CurrentProductionYield().useful);
		Assert.Equal(0, city.CurrentCommerceYield().wealth);
	}

	[Fact]
	public void AWealthFlagOnAnOrdinaryBuildingAlsoConvertsShields() {
		// The original engine reads the flag off the produced improvement
		// (BLDG+0xec bit 19), so a mod that defines Wealth as a real building
		// keeps working.
		C7GameData.GameData gameData = MakeGame();
		Player player = MakePlayer(gameData);
		Building moddedWealth = MakeBuilding(gameData, "Modded Wealth", SaveBuilding.Flag.Capitalization);
		City city = MakeCity(gameData, player, commerce: 1, shields: 8);
		city.SetItemBeingProduced(moddedWealth);
		EngineStorage.InitializeGameDataForTests(gameData);

		Assert.Equal(2, city.CurrentCommerceYield().wealth);
	}

	// ---------- the production percentage (spec 14 §4.1 step 3) ----------

	private int ShieldsFor(int shields, params Building[] buildings) {
		C7GameData.GameData gameData = MakeGame();
		Player player = MakePlayer(gameData);
		City city = MakeCity(gameData, player, commerce: 1, shields: shields, buildings);
		EngineStorage.InitializeGameDataForTests(gameData);
		return city.CurrentProductionYield().useful;
	}

	[Theory]
	[InlineData("Factory", 2)]
	[InlineData("Manufacturing Plant", 2)]
	[InlineData("Coal Plant", 2)]
	[InlineData("Hydro Plant", 2)]
	[InlineData("Nuclear Plant", 4)]
	[InlineData("Solar Plant", 2)]
	[InlineData("Iron Works", 4)]
	public void ShippedRulesetCarriesTheProductionPercentages(string name, int production) {
		Building building = shipped.Buildings.Find(b => b.name == name);

		Assert.NotNull(building);
		Assert.Equal(production, building.production);
	}

	[Fact]
	public void OnlyThePowerPlantsReplaceOtherBuildings() {
		List<string> flagged = shipped.Buildings
			.Where(b => b.replacesOtherBuildings)
			.Select(b => b.name)
			.OrderBy(n => n)
			.ToList();

		Assert.Equal(
			new List<string> { "Coal Plant", "Hydro Plant", "Nuclear Plant", "Solar Plant" },
			flagged);
	}

	[Fact]
	public void OnlyTheSevenShippedBuildingsSetProduction() {
		List<string> flagged = shipped.Buildings
			.Where(b => b.production != 0)
			.Select(b => b.name)
			.OrderBy(n => n)
			.ToList();

		Assert.Equal(
			new List<string> {
				"Coal Plant", "Factory", "Hydro Plant", "Iron Works",
				"Manufacturing Plant", "Nuclear Plant", "Solar Plant",
			},
			flagged);
	}

	[Fact]
	public void ProductionBuildingsStackAdditivelyInQuarters() {
		// 4 shields with a Factory (2, i.e. +50 %) are 4 * 6/4 = 6.
		Assert.Equal(4, ShieldsFor(4));
		Assert.Equal(6, ShieldsFor(4, ShippedBuilding("Factory")));

		// The Manufacturing Plant does not replace the Factory, so both count:
		// 4 * (4+2+2)/4 = 8, i.e. 2x.
		Assert.Equal(8, ShieldsFor(4, ShippedBuilding("Factory"), ShippedBuilding("Manufacturing Plant")));

		// Factory + Nuclear Plant are 4 * (4+2+4)/4 = 10, i.e. 2.5x.
		Assert.Equal(10, ShieldsFor(4, ShippedBuilding("Factory"), ShippedBuilding("Nuclear Plant")));
	}

	[Fact]
	public void ReplacementPowerPlantsDoNotStackWithEachOther() {
		// Only the best replacement plant counts, so the three 2-point plants
		// add a single 2 rather than 6: 4 * (4+2+2)/4 = 8, not 4 * 12/4 = 12.
		Assert.Equal(8, ShieldsFor(4,
			ShippedBuilding("Factory"),
			ShippedBuilding("Coal Plant"),
			ShippedBuilding("Hydro Plant"),
			ShippedBuilding("Solar Plant")));

		// A replacement plant without its required building counts for nothing.
		Assert.Equal(4, ShieldsFor(4, ShippedBuilding("Nuclear Plant")));
	}

	[Fact]
	public void AnObsoleteProductionBuildingDoesNotMultiplyShields() {
		C7GameData.GameData gameData = MakeGame();
		Player player = MakePlayer(gameData);

		Building factory = ShippedBuilding("Factory");
		factory.renderedObsoleteBy = new Tech { id = ID.None("obsoleting-tech") };
		player.knownTechs.Add(factory.renderedObsoleteBy.id);

		City city = MakeCity(gameData, player, commerce: 1, shields: 4, factory);
		EngineStorage.InitializeGameDataForTests(gameData);

		Assert.True(factory.production > 0);
		Assert.Equal(4, city.CurrentProductionYield().useful);
	}

	[Fact]
	public void WealthConvertsTheShieldsLeftAfterTheProductionMultiplier() {
		// The original's step 6 reads +0x254, which is the net shields *after*
		// the building multiplier, so a Factory raises a Wealth city's gold from
		// 8/4 = 2 to 12/4 = 3.
		Assert.Equal(2, WealthGoldFor(8));

		C7GameData.GameData gameData = MakeGame();
		Player player = MakePlayer(gameData);
		City city = MakeCity(gameData, player, commerce: 1, shields: 8, ShippedBuilding("Factory"));
		city.SetItemBeingProduced(WealthInflow(fixture));
		EngineStorage.InitializeGameDataForTests(gameData);

		Assert.Equal(12, city.CurrentProductionYield().useful);
		Assert.Equal(3, city.CurrentCommerceYield().wealth);
	}

	// ---------- the BIQ import path ----------

	private static string ConquestsBiqPath =>
		Path.Combine(Civ3Location.GetCiv3Path(), "Conquests", "conquests.biq");

	// A Conquest that has a map, so the whole ImportBiq path can run. Its own
	// BLDG table is the only shipped one that sets the +50% luxury flag (on the
	// Amphitheatre) and carries the Wealth improvement.
	private const string ScenarioFileName = "1 Mesopotamia.biq";

	private static string ScenarioPath =>
		Path.Combine(Civ3Location.GetCiv3Path(), "Conquests", "Conquests", ScenarioFileName);

	private static Func<string, string> GetPediaIconsPath(string subfolder) {
		return (relativeModPath) => {
			if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
				relativeModPath = relativeModPath.Replace("\\conquests\\", "/Conquests/");
			}

			return Path.GetFullPath(Path.Combine(
				Civ3Location.GetCiv3Path(), subfolder, relativeModPath, "Text", "PediaIcons.txt"));
		};
	}

	[SkippableFact]
	public void ShippedConquestsBiq_MapsTheCommerceAndLuxuryFlags() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), SkipReason);

		BiqData biq = BiqData.LoadFile(ConquestsBiqPath);

		Assert.Contains(SaveBuilding.Flag.Plus50PercentCommerce,
			ImportCiv3.LoadBuildingFlags(biq.Bldg.First(b => b.Name == "Marketplace")));
		Assert.Contains(SaveBuilding.Flag.Plus50PercentCommerce,
			ImportCiv3.LoadBuildingFlags(biq.Bldg.First(b => b.Name == "Bank")));
		Assert.Contains(SaveBuilding.Flag.Plus50PercentCommerce,
			ImportCiv3.LoadBuildingFlags(biq.Bldg.First(b => b.Name == "Stock Exchange")));
		Assert.Contains(SaveBuilding.Flag.Capitalization,
			ImportCiv3.LoadBuildingFlags(biq.Bldg.First(b => b.Name == "Wealth")));

		// Nothing in the base file sets the luxury bit.
		Assert.DoesNotContain(SaveBuilding.Flag.Plus50PercentLuxury,
			ImportCiv3.LoadBuildingFlags(biq.Bldg.First(b => b.Name == "Marketplace")));
		Assert.DoesNotContain(biq.Bldg, b => b.Plus50PercentLuxury);

		// Economics is the only technology carrying 0x1000.
		Assert.Equal("Economics", biq.Tech.Single(t => t.DoublesWealthProduction).Name);
	}

	[SkippableFact]
	public void ShippedConquestsBiq_MapsTheProductionField() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), SkipReason);

		BiqData biq = BiqData.LoadFile(ConquestsBiqPath);

		Assert.Equal(2, biq.Bldg.First(b => b.Name == "Factory").Production);
		Assert.Equal(4, biq.Bldg.First(b => b.Name == "Nuclear Plant").Production);
		Assert.Equal(4, biq.Bldg.First(b => b.Name == "Iron Works").Production);

		Assert.Contains(SaveBuilding.Flag.ReplacesOtherBuildings,
			ImportCiv3.LoadBuildingFlags(biq.Bldg.First(b => b.Name == "Nuclear Plant")));
		Assert.DoesNotContain(SaveBuilding.Flag.ReplacesOtherBuildings,
			ImportCiv3.LoadBuildingFlags(biq.Bldg.First(b => b.Name == "Factory")));
		Assert.DoesNotContain(SaveBuilding.Flag.ReplacesOtherBuildings,
			ImportCiv3.LoadBuildingFlags(biq.Bldg.First(b => b.Name == "Manufacturing Plant")));
	}

	[SkippableFact]
	public void ImportBiqCarriesTheLuxuryFlagAndTheWealthInflow() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), SkipReason);

		EngineStorage.animationsEnabled = false;
		SaveGame save = ImportCiv3.ImportBiq(ScenarioPath, PathUtils.defaultBicPath, GetPediaIconsPath("Conquests/Conquests"));

		// The Mesopotamia Amphitheatre is the only shipped building with 0x8.
		SaveBuilding amphitheatre = save.Buildings.First(b => b.name == "Amphitheatre");
		Assert.Contains(SaveBuilding.Flag.Plus50PercentLuxury, amphitheatre.flags);
		Assert.DoesNotContain(SaveBuilding.Flag.Plus50PercentCommerce, amphitheatre.flags);
		Assert.True(new Building(amphitheatre, new C7GameData.GameData(1)).plus50PercentLuxury);

		// Wealth becomes an inflow, so its Capitalization flag is carried there.
		SaveInflow wealth = save.Inflows.First(i => i.name == "Wealth");
		Assert.True(wealth.capitalization);
		Assert.DoesNotContain(save.Buildings, b => b.name == "Wealth");

		// The scenario's Worker Housing carries BLDG.Production 2 and does not
		// replace other buildings, so it reaches the runtime building as a plain
		// additive +50 % multiplier.
		SaveBuilding workerHousing = save.Buildings.First(b => b.name == "Worker Housing");
		Assert.Equal(2, workerHousing.production);
		Assert.DoesNotContain(SaveBuilding.Flag.ReplacesOtherBuildings, workerHousing.flags);
		Building runtime = new(workerHousing, new C7GameData.GameData(1));
		Assert.Equal(2, runtime.production);
		Assert.False(runtime.replacesOtherBuildings);
	}
}
