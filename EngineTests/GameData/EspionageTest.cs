using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using C7Engine;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using QueryCiv3;
using QueryCiv3.Biq;
using Xunit;

namespace EngineTests.GameData;

// A Random that always returns the same value, so mission rolls are
// deterministic in tests.
internal class FixedRandom : Random {
	private readonly int value;

	public FixedRandom(int value) {
		this.value = value;
	}

	public override int Next(int maxValue) => maxValue <= 0 ? 0 : value % maxValue;
	public override int Next(int minValue, int maxValue) => maxValue <= minValue ? minValue : minValue + value % (maxValue - minValue);
}

// A Random that returns a fixed sequence, for tests that need the mission roll
// and the follow-up roll to differ.
internal class SequenceRandom : Random {
	private readonly Queue<int> values;

	public SequenceRandom(params int[] values) {
		this.values = new Queue<int>(values);
	}

	public override int Next(int maxValue) => maxValue <= 0 ? 0 : values.Dequeue() % maxValue;
	public override int Next(int minValue, int maxValue) => maxValue <= minValue ? minValue : minValue + values.Dequeue() % (maxValue - minValue);
}

// Builds the minimal game state an espionage test needs. The rule tables are
// the values measured out of the shipped conquests.biq.
internal static class EspionageTestGame {
	public static C7GameData.GameData NewGameData() {
		C7GameData.GameData gameData = new(1);
		gameData.map.numTilesWide = 100;
		gameData.map.numTilesTall = 100;
		gameData.map.techRate = 240;
		gameData.rules = new Rules { MaximumLevel1CitySize = 6, MaximumLevel2CitySize = 12 };
		gameData.gameDifficulty = new Difficulty { AiCostFactor = 1, HumanCostFactor = 1 };
		gameData.espionageMissions = Missions();
		gameData.cultureLevels = CultureLevels();
		EngineStorage.InitializeGameDataForTests(gameData);
		return gameData;
	}

	public static List<EspionageMission> Missions() {
		return new List<EspionageMission> {
			new() { name = "Build an Embassy", diplomatAllowed = true, baseCost = 20 },
			new() { name = "Investigate City", diplomatAllowed = true, spyAllowed = true, baseCost = 10 },
			new() { name = "Steal Technology", diplomatAllowed = true, spyAllowed = true, baseCost = 10 },
			new() { name = "Steal World Map", spyAllowed = true, baseCost = 1 },
			new() { name = "Plant Spy", spyAllowed = true, baseCost = 60 },
			new() { name = "Steal Plans", spyAllowed = true, baseCost = 10 },
			new() { name = "Initiate Propaganda", spyAllowed = true, baseCost = 100 },
			new() { name = "Sabotage Production", spyAllowed = true, baseCost = 10 },
			new() { name = "Expose Enemy Spy", spyAllowed = true, baseCost = 80 },
		};
	}

	public static List<CultureLevel> CultureLevels() {
		return new List<CultureLevel> {
			new() { name = "in awe of", chanceOfSuccessfulPropaganda = 30, cultureRatioPercentage = 300 },
			new() { name = "admirers of", chanceOfSuccessfulPropaganda = 25, cultureRatioPercentage = 200 },
			new() { name = "impressed with", chanceOfSuccessfulPropaganda = 20, cultureRatioPercentage = 100 },
			new() { name = "unimpressed by", chanceOfSuccessfulPropaganda = 10, cultureRatioPercentage = 75 },
			new() { name = "dismissive of", chanceOfSuccessfulPropaganda = 5, cultureRatioPercentage = 50 },
			new() { name = "disdainful of", chanceOfSuccessfulPropaganda = 3, cultureRatioPercentage = 33 },
		};
	}

	// Writing is the only shipped technology whose advance flag bit 0 is set,
	// so it is what unlocks the diplomat agent.
	public static Tech AddWriting(C7GameData.GameData gameData, Player player) {
		Tech writing = new() { id = gameData.ids.CreateID("tech"), Name = "Writing", EnablesDiplomats = true };
		gameData.techs.Add(writing);
		player.knownTechs.Add(writing.id);
		return writing;
	}

	// The Intelligence Agency is the only shipped wonder carrying the "allows
	// spy missions" building flag, so it is what unlocks the spy agent.
	public static Building NewIntelligenceAgency(C7GameData.GameData gameData) {
		return new Building(new SaveBuilding {
			name = "Intelligence Agency",
			flags = { SaveBuilding.Flag.AllowsSpyMissions },
		}, gameData);
	}

	public static Player NewPlayer(C7GameData.GameData gameData, string key, int gold) {
		return new Player {
			id = gameData.ids.CreateID(key),
			civilization = new Civilization(),
			government = new Government { diplomatsAre = 1, spiesAre = 1 },
			gold = gold,
		};
	}

	public static Tile NewTile(C7GameData.GameData gameData, int x, int y) {
		return new Tile(ID.None("tile")) {
			XCoordinate = x,
			YCoordinate = y,
			map = gameData.map,
		};
	}

	public static City NewCity(C7GameData.GameData gameData, Player owner, Tile location, string name, int population, Civilization nationality) {
		City city = new(location, owner, name, gameData.ids.CreateID("city"));
		for (int i = 0; i < population; i++) {
			city.residents.Add(new CityResident { nationality = nationality });
		}
		owner.cities.Add(city);
		return city;
	}

	public static void Meet(Player a, Player b) {
		a.EnsureRelationshipExists(b);
	}
}

// The mission table and the espionage flags have to come out of the shipped
// conquests.biq with the values re/specs/24_espionage.md records.
public class EspionageImportTest {
	[SkippableFact]
	public void ImportedMissionTableHasNineMissionsWithShippedValues() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");

		BiqData biq = BiqData.LoadFile(PathUtils.defaultBicPath);
		List<EspionageMission> missions = ImportCiv3.BuildEspionageMissions(biq);

		Assert.Equal(9, missions.Count);

		string[] expectedNames = {
			"Build an Embassy", "Investigate City", "Steal Technology", "Steal World Map",
			"Plant Spy", "Steal Plans", "Initiate Propaganda", "Sabotage Production", "Expose Enemy Spy",
		};
		bool[] expectedDiplomat = { true, true, true, false, false, false, false, false, false };
		bool[] expectedSpy = { false, true, true, true, true, true, true, true, true };
		int[] expectedCosts = { 20, 10, 10, 1, 60, 10, 100, 10, 80 };

		for (int i = 0; i < 9; i++) {
			Assert.Equal(expectedNames[i], missions[i].name);
			Assert.Equal(expectedDiplomat[i], missions[i].diplomatAllowed);
			Assert.Equal(expectedSpy[i], missions[i].spyAllowed);
			Assert.Equal(expectedCosts[i], missions[i].baseCost);
		}
	}

	[SkippableFact]
	public void ImportedCultureLevelsMatchShippedValues() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");

		BiqData biq = BiqData.LoadFile(PathUtils.defaultBicPath);
		List<CultureLevel> levels = ImportCiv3.BuildCultureLevels(biq);

		Assert.Equal(6, levels.Count);
		int[] chances = { 30, 25, 20, 10, 5, 3 };
		int[] ratios = { 300, 200, 100, 75, 50, 33 };
		for (int i = 0; i < 6; i++) {
			Assert.Equal(chances[i], levels[i].chanceOfSuccessfulPropaganda);
			Assert.Equal(ratios[i], levels[i].cultureRatioPercentage);
		}
	}

	[SkippableFact]
	public void ShippedBiqCarriesTheMeasuredEspionageFlags() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");

		BiqData biq = BiqData.LoadFile(PathUtils.defaultBicPath);

		// Only Democracy is immune to a mission, and only to Initiate Propaganda.
		Assert.Equal(6, biq.Govt.Single(g => g.Name == "Democracy").ImmuneTo);
		Assert.All(biq.Govt.Where(g => g.Name != "Democracy"), g => Assert.Equal(-1, g.ImmuneTo));

		// Communism and Fascism are the only governments with the spy bonus.
		Assert.Equal(2, biq.Govt.Single(g => g.Name == "Communism").SpiesAre);
		Assert.Equal(2, biq.Govt.Single(g => g.Name == "Fascism").SpiesAre);
		Assert.All(biq.Govt.Where(g => g.Name != "Communism" && g.Name != "Fascism"), g => Assert.Equal(1, g.SpiesAre));

		// Anarchy is the transition government; Despotism is the default one.
		Assert.True(biq.Govt.Single(g => g.Name == "Anarchy").TransitionType == 1);
		Assert.True(biq.Govt.Single(g => g.Name == "Despotism").DefaultType == 1);

		// Writing is the only technology that enables diplomats.
		Assert.Equal("Writing", biq.Tech.Single(t => t.EnablesDiplomats).Name);

		// Intelligence Agency allows spy missions; Courthouse resists bribery.
		Assert.Equal("Intelligence Agency", biq.Bldg.Single(b => b.AllowsSpyMissions).Name);
		Assert.Equal("Courthouse", biq.Bldg.Single(b => b.ResistantToBribery).Name);
	}
}

// The Lua ruleset is the rules source for a game that has no BIQ, so it has to
// carry the same espionage data. This test never skips.
public class EspionageRulesetTest {
	private static SaveGame LoadRuleset() {
		string path = Path.Combine(PathUtils.GameModesDir, "civ3", "ruleset.json");
		return SaveGame.LoadFromJSON(File.ReadAllText(path));
	}

	[Fact]
	public void RulesetJsonProvidesTheNineMissionTable() {
		SaveGame save = LoadRuleset();

		Assert.Equal(9, save.EspionageMissions.Count);
		Assert.Equal("Build an Embassy", save.EspionageMissions[0].name);
		Assert.True(save.EspionageMissions[0].diplomatAllowed);
		Assert.False(save.EspionageMissions[0].spyAllowed);
		Assert.Equal(20, save.EspionageMissions[0].baseCost);

		Assert.Equal("Steal Technology", save.EspionageMissions[2].name);
		Assert.True(save.EspionageMissions[2].diplomatAllowed);
		Assert.True(save.EspionageMissions[2].spyAllowed);
		Assert.Equal(10, save.EspionageMissions[2].baseCost);

		Assert.Equal("Expose Enemy Spy", save.EspionageMissions[8].name);
		Assert.False(save.EspionageMissions[8].diplomatAllowed);
		Assert.True(save.EspionageMissions[8].spyAllowed);
		Assert.Equal(80, save.EspionageMissions[8].baseCost);
	}

	[Fact]
	public void RulesetJsonProvidesTheEspionageFlags() {
		SaveGame save = LoadRuleset();

		Assert.Equal(6, save.Governments.Single(g => g.name == "Democracy").immuneTo);
		Assert.Equal(-1, save.Governments.Single(g => g.name == "Anarchy").immuneTo);
		Assert.Equal(2, save.Governments.Single(g => g.name == "Communism").spiesAre);
		Assert.True(save.Governments.Single(g => g.name == "Anarchy").transitionType);

		Assert.Contains(SaveTech.Flag.EnablesDiplomats, save.Techs.Single(t => t.Name == "Writing").flags);
		Assert.Contains(SaveBuilding.Flag.AllowsSpyMissions, save.Buildings.Single(b => b.name == "Intelligence Agency").flags);
		Assert.Contains(SaveBuilding.Flag.ResistantToBribery, save.Buildings.Single(b => b.name == "Courthouse").flags);

		Assert.Equal(6, save.CultureLevels.Count);
		Assert.Equal(30, save.CultureLevels[0].chanceOfSuccessfulPropaganda);
		Assert.Equal(3, save.CultureLevels[5].chanceOfSuccessfulPropaganda);
	}
}

// The rules themselves: who may spy, what a mission costs, its odds and what it
// does to game state.
public class EspionageTest {
	private const int StealTechnology = Espionage.StealTechnology;

	// `agentsAvailable` unlocks both agents, so a test that is about something
	// else (a cost, an effect) does not have to restate the eligibility gates.
	// Tests that are about the gates pass false and unlock one agent at a time.
	private static (C7GameData.GameData gameData, Player actor, Player target, City actorCity, City targetCity) SetupPair(int targetPopulation = 6, int distance = 8, int ownNationals = 0, bool agentsAvailable = true) {
		C7GameData.GameData gameData = EspionageTestGame.NewGameData();
		Player actor = EspionageTestGame.NewPlayer(gameData, "actor", 100000);
		Player target = EspionageTestGame.NewPlayer(gameData, "target", 100000);
		EspionageTestGame.Meet(actor, target);

		Tile actorTile = EspionageTestGame.NewTile(gameData, 0, 0);
		Tile targetTile = EspionageTestGame.NewTile(gameData, distance, distance);
		City actorCity = EspionageTestGame.NewCity(gameData, actor, actorTile, "Capital", 1, actor.civilization);
		actorCity.capital = true;
		if (agentsAvailable) {
			EspionageTestGame.AddWriting(gameData, actor);
			actorCity.AddBuilding(EspionageTestGame.NewIntelligenceAgency(gameData));
		}
		City targetCity = EspionageTestGame.NewCity(gameData, target, targetTile, "Target", targetPopulation - ownNationals, target.civilization);
		for (int i = 0; i < ownNationals; i++) {
			targetCity.residents.Add(new CityResident { nationality = actor.civilization });
		}
		return (gameData, actor, target, actorCity, targetCity);
	}

	private static PlayerRelationship Relationship(Player from, Player to) {
		return from.playerRelationships[to.id];
	}

	// ---------------------------------------------------------------------
	// Availability predicates (spec 3.1, 3.2)
	// ---------------------------------------------------------------------

	[Fact]
	public void DiplomatIsAvailableWithCapitalEnablingTechAndPlayableGovernment() {
		(C7GameData.GameData gameData, Player actor, _, _, _) = SetupPair(agentsAvailable: false);
		EspionageTestGame.AddWriting(gameData, actor);

		Assert.True(Espionage.DiplomatIsAvailable(gameData, actor));
	}

	[Fact]
	public void DiplomatIsUnavailableWithoutACapital() {
		(C7GameData.GameData gameData, Player actor, _, City actorCity, _) = SetupPair(agentsAvailable: false);
		EspionageTestGame.AddWriting(gameData, actor);
		actorCity.capital = false;

		Assert.False(Espionage.DiplomatIsAvailable(gameData, actor));
	}

	[Fact]
	public void DiplomatIsUnavailableWithoutATechnologyThatEnablesDiplomats() {
		(C7GameData.GameData gameData, Player actor, _, _, _) = SetupPair(agentsAvailable: false);
		Tech bronzeWorking = new() { id = gameData.ids.CreateID("tech"), Name = "Bronze Working" };
		gameData.techs.Add(bronzeWorking);
		actor.knownTechs.Add(bronzeWorking.id);

		Assert.False(Espionage.DiplomatIsAvailable(gameData, actor));
	}

	[Fact]
	public void DiplomatIsUnavailableInTheTransitionGovernment() {
		(C7GameData.GameData gameData, Player actor, _, _, _) = SetupPair(agentsAvailable: false);
		EspionageTestGame.AddWriting(gameData, actor);

		// Anarchy is the shipped transition government.
		actor.government = new Government { transitionType = true };
		Assert.False(Espionage.DiplomatIsAvailable(gameData, actor));

		// Despotism, the default government, does not block espionage.
		actor.government = new Government { defaultType = true };
		Assert.True(Espionage.DiplomatIsAvailable(gameData, actor));
	}

	[Fact]
	public void SpyIsAvailableWithASpyWonder() {
		(C7GameData.GameData gameData, Player actor, _, City actorCity, _) = SetupPair(agentsAvailable: false);
		actorCity.AddBuilding(EspionageTestGame.NewIntelligenceAgency(gameData));

		Assert.True(Espionage.SpyIsAvailable(gameData, actor));
	}

	[Fact]
	public void SpyIsUnavailableWithoutASpyWonder() {
		(C7GameData.GameData gameData, Player actor, _, City actorCity, _) = SetupPair(agentsAvailable: false);
		Building courthouse = new(new SaveBuilding { name = "Courthouse" }, gameData);
		actorCity.AddBuilding(courthouse);

		Assert.False(Espionage.SpyIsAvailable(gameData, actor));
	}

	[Fact]
	public void SpyIsUnavailableInTheTransitionGovernment() {
		(C7GameData.GameData gameData, Player actor, _, City actorCity, _) = SetupPair(agentsAvailable: false);
		actorCity.AddBuilding(EspionageTestGame.NewIntelligenceAgency(gameData));
		actor.government = new Government { transitionType = true };

		Assert.False(Espionage.SpyIsAvailable(gameData, actor));
	}

	[Fact]
	public void MissionAvailabilityFollowsTheAgentAndPerCivPrerequisites() {
		(C7GameData.GameData gameData, Player actor, Player target, _, City targetCity) = SetupPair();

		// A diplomat's investigate-city needs an embassy with the target.
		Assert.False(Espionage.MissionIsAvailable(gameData, actor, target, Espionage.InvestigateCity, EspionageAgent.Diplomat));
		Relationship(actor, target).embassyEstablished = true;
		Assert.True(Espionage.MissionIsAvailable(gameData, actor, target, Espionage.InvestigateCity, EspionageAgent.Diplomat));

		// A spy's steal-world-map needs an agent planted in the target.
		Assert.False(Espionage.MissionIsAvailable(gameData, actor, target, Espionage.StealWorldMap, EspionageAgent.Spy));
		Relationship(actor, target).agentPlanted = true;
		Assert.True(Espionage.MissionIsAvailable(gameData, actor, target, Espionage.StealWorldMap, EspionageAgent.Spy));

		// The same mission is refused when the other agent kind runs it.
		Assert.False(Espionage.MissionIsAvailable(gameData, actor, target, Espionage.StealWorldMap, EspionageAgent.Diplomat));

		// Nothing is offered against our own civ, or for an unknown mission id.
		Assert.False(Espionage.MissionIsAvailable(gameData, actor, actor, Espionage.InvestigateCity, EspionageAgent.Diplomat));
		Assert.False(Espionage.MissionIsAvailable(gameData, actor, target, 42, EspionageAgent.Spy));
		Assert.NotNull(targetCity);
	}

	[Fact]
	public void MissionAvailabilityRequiresTheActingAgentsPredicate() {
		// Every per-civ prerequisite is met, but the civ has neither Writing
		// (the diplomat agent) nor the Intelligence Agency (the spy agent), so
		// neither agent may be offered a mission at all.
		(C7GameData.GameData gameData, Player actor, Player target, City actorCity, City targetCity) = SetupPair(agentsAvailable: false);
		Relationship(actor, target).embassyEstablished = true;
		Relationship(actor, target).agentPlanted = true;

		Assert.False(Espionage.MissionIsAvailable(gameData, actor, target, Espionage.InvestigateCity, EspionageAgent.Diplomat));
		Assert.False(Espionage.MissionIsAvailable(gameData, actor, target, Espionage.StealWorldMap, EspionageAgent.Spy));

		// The Intelligence Agency unlocks the spy agent alone; the diplomat
		// mission stays refused.
		actorCity.AddBuilding(EspionageTestGame.NewIntelligenceAgency(gameData));
		Assert.True(Espionage.MissionIsAvailable(gameData, actor, target, Espionage.StealWorldMap, EspionageAgent.Spy));
		Assert.False(Espionage.MissionIsAvailable(gameData, actor, target, Espionage.InvestigateCity, EspionageAgent.Diplomat));

		// Writing unlocks the diplomat agent.
		EspionageTestGame.AddWriting(gameData, actor);
		Assert.True(Espionage.MissionIsAvailable(gameData, actor, target, Espionage.InvestigateCity, EspionageAgent.Diplomat));

		Assert.NotNull(targetCity);
	}

	[Fact]
	public void RunMissionIsRefusedWithoutTheActingAgentsPredicate() {
		// The run path composes the same gate: a spy mission without the
		// Intelligence Agency never starts and never spends gold.
		(C7GameData.GameData gameData, Player actor, Player target, _, City targetCity) = SetupPair(agentsAvailable: false);
		Relationship(actor, target).agentPlanted = true;
		C7GameData.GameData.rng = new FixedRandom(0);

		EspionageMissionResult result = Espionage.RunMission(gameData, actor, target, targetCity, Espionage.StealWorldMap, EspionageAgent.Spy, EspionageSafetyLevel.Carefully);

		Assert.False(result.ran);
		Assert.False(result.succeeded);
		Assert.Equal(100000, actor.gold);
	}

	[Fact]
	public void EmbassyMissionIsRefusedAtWar() {
		(C7GameData.GameData gameData, Player actor, Player target, _, _) = SetupPair();

		Assert.True(Espionage.MissionIsAvailable(gameData, actor, target, Espionage.BuildEmbassy, EspionageAgent.Diplomat));

		PlayerRelationship.DeclareWar(actor, target, false, 0);
		Assert.False(Espionage.MissionIsAvailable(gameData, actor, target, Espionage.BuildEmbassy, EspionageAgent.Diplomat));
	}

	[Fact]
	public void PropagandaIsRefusedByAnImmuneGovernment() {
		(C7GameData.GameData gameData, Player actor, Player target, _, _) = SetupPair();
		Relationship(actor, target).agentPlanted = true;

		Assert.True(Espionage.MissionIsAvailable(gameData, actor, target, Espionage.InitiatePropaganda, EspionageAgent.Spy));

		// The shipped Democracy is immune to mission 6.
		target.government = new Government { immuneTo = 6 };
		Assert.False(Espionage.MissionIsAvailable(gameData, actor, target, Espionage.InitiatePropaganda, EspionageAgent.Spy));
	}

	// ---------------------------------------------------------------------
	// Cost (spec 4.1, 4.2, 4.3)
	// ---------------------------------------------------------------------

	[Fact]
	public void StealTechnologyCostMatchesTheWorkedExample() {
		(C7GameData.GameData gameData, Player actor, Player target, _, City targetCity) = SetupPair(targetPopulation: 6, distance: 8);
		for (int i = 0; i < 30; i++) {
			target.knownTechs.Add(gameData.ids.CreateID("tech"));
		}

		// Base = 30 * 240 * 10 / 100 = 720, scaler = 1 * (720 + 8), carefully
		// multiplies by 150%: 1092 gold.
		Assert.Equal(1092, Espionage.MissionCost(gameData, actor, target, targetCity, StealTechnology, EspionageSafetyLevel.Carefully));
		Assert.Equal(728, Espionage.MissionCost(gameData, actor, target, targetCity, StealTechnology, EspionageSafetyLevel.Immediately));
		Assert.Equal(1456, Espionage.MissionCost(gameData, actor, target, targetCity, StealTechnology, EspionageSafetyLevel.Safely));
	}

	[Fact]
	public void SharedScalerUsesTheTownAndCityThresholds() {
		(C7GameData.GameData gameData, Player actor, Player target, _, City targetCity) = SetupPair(targetPopulation: 6, distance: 10);
		targetCity.residents.Clear();
		for (int i = 0; i < 6; i++) {
			targetCity.residents.Add(new CityResident { nationality = target.civilization });
		}

		// Plant Spy has a flat base of 60, so the scaler is visible directly.
		// Tier 0 up to population 6, tier 1 up to 12, tier 2 above.
		Assert.Equal(70, ScaledPlantSpyCost(gameData, actor, target, targetCity, 6));
		Assert.Equal(80, ScaledPlantSpyCost(gameData, actor, target, targetCity, 7));
		Assert.Equal(80, ScaledPlantSpyCost(gameData, actor, target, targetCity, 12));
		Assert.Equal(90, ScaledPlantSpyCost(gameData, actor, target, targetCity, 13));
	}

	private static int ScaledPlantSpyCost(C7GameData.GameData gameData, Player actor, Player target, City targetCity, int population) {
		targetCity.residents.Clear();
		for (int i = 0; i < population; i++) {
			targetCity.residents.Add(new CityResident { nationality = target.civilization });
		}
		return Espionage.MissionCost(gameData, actor, target, targetCity, Espionage.PlantSpy, EspionageSafetyLevel.Immediately);
	}

	[Fact]
	public void SharedScalerMakesMissionsCheaperWithOwnNationals() {
		(C7GameData.GameData gameData, Player actor, Player target, _, City targetCity) = SetupPair(targetPopulation: 6, distance: 10);
		targetCity.residents.Clear();
		for (int i = 0; i < 6; i++) {
			targetCity.residents.Add(new CityResident { nationality = target.civilization });
		}
		Assert.Equal(70, Espionage.MissionCost(gameData, actor, target, targetCity, Espionage.PlantSpy, EspionageSafetyLevel.Immediately));

		targetCity.residents.Clear();
		for (int i = 0; i < 6; i++) {
			targetCity.residents.Add(new CityResident { nationality = actor.civilization });
		}
		Assert.Equal(37, Espionage.MissionCost(gameData, actor, target, targetCity, Espionage.PlantSpy, EspionageSafetyLevel.Immediately));
	}

	[Fact]
	public void MissionBaseCostsMatchThePerMissionFormulas() {
		(C7GameData.GameData gameData, Player actor, Player target, City actorCity, City targetCity) = SetupPair(targetPopulation: 6);
		for (int i = 0; i < 30; i++) {
			target.knownTechs.Add(gameData.ids.CreateID("tech"));
		}
		for (int i = 0; i < 4; i++) {
			target.units.Add(new MapUnit());
		}
		targetCity.SetStoredShields(15);
		actorCity.perPlayerCulture[actor] = 1000;
		targetCity.perPlayerCulture[target] = 500;
		actor.gold = 2000;
		target.gold = 100;

		Assert.Equal(26, Espionage.MissionBaseCost(gameData, actor, target, targetCity, Espionage.BuildEmbassy));
		Assert.Equal(60, Espionage.MissionBaseCost(gameData, actor, target, targetCity, Espionage.InvestigateCity));
		Assert.Equal(720, Espionage.MissionBaseCost(gameData, actor, target, targetCity, StealTechnology));
		Assert.Equal(100, Espionage.MissionBaseCost(gameData, actor, target, targetCity, Espionage.StealWorldMap));
		Assert.Equal(60, Espionage.MissionBaseCost(gameData, actor, target, targetCity, Espionage.PlantSpy));
		Assert.Equal(40, Espionage.MissionBaseCost(gameData, actor, target, targetCity, Espionage.StealPlans));
		Assert.Equal(915, Espionage.MissionBaseCost(gameData, actor, target, targetCity, Espionage.InitiatePropaganda));
		Assert.Equal(150, Espionage.MissionBaseCost(gameData, actor, target, targetCity, Espionage.SabotageProduction));
		Assert.Equal(80, Espionage.MissionBaseCost(gameData, actor, target, targetCity, Espionage.ExposeEnemySpy));
	}

	[Fact]
	public void OnlyPostureMissionsScaleWithTheSafetyLevel() {
		(C7GameData.GameData gameData, Player actor, Player target, _, City targetCity) = SetupPair(targetPopulation: 6, distance: 0);
		for (int i = 0; i < 4; i++) {
			target.units.Add(new MapUnit());
		}

		// Steal Plans offers a posture: 100% / 150% / 200%.
		int immediate = Espionage.MissionCost(gameData, actor, target, targetCity, Espionage.StealPlans, EspionageSafetyLevel.Immediately);
		int carefully = Espionage.MissionCost(gameData, actor, target, targetCity, Espionage.StealPlans, EspionageSafetyLevel.Carefully);
		int safely = Espionage.MissionCost(gameData, actor, target, targetCity, Espionage.StealPlans, EspionageSafetyLevel.Safely);
		Assert.Equal(immediate * 3 / 2, carefully);
		Assert.Equal(immediate * 2, safely);

		// Plant Spy does not: the safety level is ignored.
		Assert.Equal(
			Espionage.MissionCost(gameData, actor, target, targetCity, Espionage.PlantSpy, EspionageSafetyLevel.Immediately),
			Espionage.MissionCost(gameData, actor, target, targetCity, Espionage.PlantSpy, EspionageSafetyLevel.Safely));
	}

	// ---------------------------------------------------------------------
	// Odds (spec 5.1, 6.6)
	// ---------------------------------------------------------------------

	[Fact]
	public void PostureMissionOddsFollowTheAgentAndSafetyLevel() {
		(C7GameData.GameData gameData, Player actor, Player target, _, _) = SetupPair();

		Assert.Equal(40, Espionage.SuccessChance(gameData, actor, target, StealTechnology, EspionageAgent.Diplomat, EspionageSafetyLevel.Immediately));
		Assert.Equal(60, Espionage.SuccessChance(gameData, actor, target, StealTechnology, EspionageAgent.Diplomat, EspionageSafetyLevel.Carefully));
		Assert.Equal(70, Espionage.SuccessChance(gameData, actor, target, StealTechnology, EspionageAgent.Diplomat, EspionageSafetyLevel.Safely));

		// A spy gets a flat +15 on the posture missions.
		Assert.Equal(55, Espionage.SuccessChance(gameData, actor, target, StealTechnology, EspionageAgent.Spy, EspionageSafetyLevel.Immediately));
		Assert.Equal(75, Espionage.SuccessChance(gameData, actor, target, StealTechnology, EspionageAgent.Spy, EspionageSafetyLevel.Carefully));
		Assert.Equal(85, Espionage.SuccessChance(gameData, actor, target, StealTechnology, EspionageAgent.Spy, EspionageSafetyLevel.Safely));
	}

	[Fact]
	public void FixedChanceMissionsUseTheirOwnStartingValue() {
		(C7GameData.GameData gameData, Player actor, Player target, _, _) = SetupPair();

		Assert.Equal(100, Espionage.SuccessChance(gameData, actor, target, Espionage.BuildEmbassy, EspionageAgent.Diplomat, EspionageSafetyLevel.Carefully));
		Assert.Equal(100, Espionage.SuccessChance(gameData, actor, target, Espionage.InvestigateCity, EspionageAgent.Spy, EspionageSafetyLevel.Carefully));
		Assert.Equal(50, Espionage.SuccessChance(gameData, actor, target, Espionage.PlantSpy, EspionageAgent.Spy, EspionageSafetyLevel.Carefully));
		Assert.Equal(50, Espionage.SuccessChance(gameData, actor, target, Espionage.PlantSpy, EspionageAgent.Diplomat, EspionageSafetyLevel.Carefully));
	}

	[Fact]
	public void GovernmentExperienceBonusChangesTheOddsAndIsClamped() {
		(C7GameData.GameData gameData, Player actor, Player target, _, _) = SetupPair();

		// Communism and Fascism give spies +10.
		actor.government = new Government { spiesAre = 2 };
		Assert.Equal(95, Espionage.SuccessChance(gameData, actor, target, StealTechnology, EspionageAgent.Spy, EspionageSafetyLevel.Safely));

		// Level 0 is a -30 penalty.
		actor.government = new Government { diplomatsAre = 0 };
		Assert.Equal(30, Espionage.SuccessChance(gameData, actor, target, StealTechnology, EspionageAgent.Diplomat, EspionageSafetyLevel.Carefully));

		// The result is clamped to 0..100.
		actor.government = new Government { diplomatsAre = 0 };
		Assert.Equal(10, Espionage.SuccessChance(gameData, actor, target, StealTechnology, EspionageAgent.Diplomat, EspionageSafetyLevel.Immediately));
		actor.government = new Government { spiesAre = 3 };
		Assert.Equal(100, Espionage.SuccessChance(gameData, actor, target, StealTechnology, EspionageAgent.Spy, EspionageSafetyLevel.Safely));
	}

	[Fact]
	public void PropagandaChanceUsesTheCultureLevelThresholds() {
		(C7GameData.GameData gameData, Player actor, Player target, City actorCity, City targetCity) = SetupPair();
		targetCity.perPlayerCulture[target] = 1000;

		Assert.Equal(30, PropagandaChanceFor(gameData, actor, actorCity, target, targetCity, 3000));
		Assert.Equal(25, PropagandaChanceFor(gameData, actor, actorCity, target, targetCity, 2500));
		Assert.Equal(20, PropagandaChanceFor(gameData, actor, actorCity, target, targetCity, 1000));
		Assert.Equal(10, PropagandaChanceFor(gameData, actor, actorCity, target, targetCity, 990));
		Assert.Equal(3, PropagandaChanceFor(gameData, actor, actorCity, target, targetCity, 330));
		// Below every threshold the last (most disdainful) level is the default.
		Assert.Equal(3, PropagandaChanceFor(gameData, actor, actorCity, target, targetCity, 300));
	}

	private static int PropagandaChanceFor(C7GameData.GameData gameData, Player actor, City actorCity, Player target, City targetCity, int actorCulture) {
		actorCity.perPlayerCulture[actor] = actorCulture;
		return Espionage.PropagandaChance(gameData, actor, target);
	}

	// ---------------------------------------------------------------------
	// Effects (spec 6, 7)
	// ---------------------------------------------------------------------

	[Fact]
	public void EstablishEmbassySetsBothFlagsAndPaysTheCost() {
		(C7GameData.GameData gameData, Player actor, Player target, _, City targetCity) = SetupPair();
		C7GameData.GameData.rng = new FixedRandom(0);

		EspionageMissionResult result = Espionage.RunMission(gameData, actor, target, targetCity, Espionage.BuildEmbassy, EspionageAgent.Diplomat);

		Assert.True(result.ran);
		Assert.True(result.succeeded);
		Assert.True(Relationship(actor, target).embassyEstablished);
		Assert.True(Relationship(target, actor).embassyEstablished);
		Assert.Equal(100000 - result.costPaid, actor.gold);
		Assert.True(result.costPaid > 0);
	}

	[Fact]
	public void InvestigateCityRevealsTheCityTile() {
		(C7GameData.GameData gameData, Player actor, Player target, _, City targetCity) = SetupPair();
		Relationship(actor, target).embassyEstablished = true;
		C7GameData.GameData.rng = new FixedRandom(0);

		Assert.False(actor.tileKnowledge.isTileKnown(targetCity.location));
		EspionageMissionResult result = Espionage.RunMission(gameData, actor, target, targetCity, Espionage.InvestigateCity, EspionageAgent.Diplomat);

		Assert.True(result.succeeded);
		Assert.True(actor.tileKnowledge.isTileKnown(targetCity.location));
	}

	[Fact]
	public void PlantSpySuccessPlantsAnAgent() {
		(C7GameData.GameData gameData, Player actor, Player target, _, City targetCity) = SetupPair();
		C7GameData.GameData.rng = new FixedRandom(0);

		EspionageMissionResult result = Espionage.RunMission(gameData, actor, target, targetCity, Espionage.PlantSpy, EspionageAgent.Spy);

		Assert.True(result.succeeded);
		Assert.False(result.agentCaught);
		Assert.True(Relationship(actor, target).agentPlanted);
		Assert.False(Relationship(actor, target).plantSpyAttemptWasCaught);
	}

	[Fact]
	public void PlantSpyFailureLatchesTheTargetAndCountsTheCaughtSpy() {
		(C7GameData.GameData gameData, Player actor, Player target, _, City targetCity) = SetupPair();
		C7GameData.GameData.rng = new FixedRandom(99);

		EspionageMissionResult result = Espionage.RunMission(gameData, actor, target, targetCity, Espionage.PlantSpy, EspionageAgent.Spy);

		Assert.False(result.succeeded);
		Assert.True(result.agentCaught);
		Assert.True(Relationship(actor, target).plantSpyAttemptWasCaught);
		Assert.False(Relationship(actor, target).agentPlanted);
		Assert.Equal(1, Relationship(target, actor).caughtSpyCount);
		// The cost is paid before the roll, so a failed mission still costs gold.
		Assert.Equal(100000 - result.costPaid, actor.gold);
	}

	[Fact]
	public void StealTechnologyGrantsTheFirstStealableTechnology() {
		(C7GameData.GameData gameData, Player actor, Player target, _, City targetCity) = SetupPair();
		Relationship(actor, target).agentPlanted = true;

		Tech hidden = new() { id = gameData.ids.CreateID("tech"), Name = "Hidden Tech", EraCivilopediaName = "Hidden" };
		Tech first = new() { id = gameData.ids.CreateID("tech"), Name = "Pottery", EraCivilopediaName = "ERAS_Ancient_Times" };
		Tech second = new() { id = gameData.ids.CreateID("tech"), Name = "Bronze Working", EraCivilopediaName = "ERAS_Ancient_Times" };
		gameData.techs.AddRange(new[] { hidden, first, second });
		target.knownTechs.UnionWith(new[] { hidden.id, first.id, second.id });
		C7GameData.GameData.rng = new FixedRandom(0);

		EspionageMissionResult result = Espionage.RunMission(gameData, actor, target, targetCity, StealTechnology, EspionageAgent.Spy, EspionageSafetyLevel.Carefully);

		Assert.True(result.succeeded);
		Assert.Equal(first, result.stolenTech);
		Assert.Contains(first.id, actor.knownTechs);
		Assert.DoesNotContain(second.id, actor.knownTechs);
		Assert.False(result.agentCaught);
	}

	[Fact]
	public void StealTechnologySecondRollFailureIsHarmless() {
		(C7GameData.GameData gameData, Player actor, Player target, _, City targetCity) = SetupPair();
		Relationship(actor, target).agentPlanted = true;
		Tech tech = new() { id = gameData.ids.CreateID("tech"), Name = "Pottery", EraCivilopediaName = "ERAS_Ancient_Times" };
		gameData.techs.Add(tech);
		target.knownTechs.Add(tech.id);
		// The mission roll passes, the 80% follow-up roll fails.
		C7GameData.GameData.rng = new SequenceRandom(0, 99);

		EspionageMissionResult result = Espionage.RunMission(gameData, actor, target, targetCity, StealTechnology, EspionageAgent.Spy, EspionageSafetyLevel.Carefully);

		Assert.False(result.succeeded);
		Assert.True(result.harmlessFailure);
		Assert.False(result.agentCaught);
		Assert.DoesNotContain(tech.id, actor.knownTechs);
		Assert.True(Relationship(actor, target).agentPlanted);
	}

	[Fact]
	public void StealTechnologySecondRollBoundaryIsEighty() {
		(C7GameData.GameData gameData, Player actor, Player target, _, City targetCity) = SetupPair();
		Relationship(actor, target).agentPlanted = true;
		Tech tech = new() { id = gameData.ids.CreateID("tech"), Name = "Pottery", EraCivilopediaName = "ERAS_Ancient_Times" };
		gameData.techs.Add(tech);
		target.knownTechs.Add(tech.id);

		// A follow-up roll of 79 passes (rand_int(100) < 80), 80 does not.
		C7GameData.GameData.rng = new SequenceRandom(0, 79);
		Assert.True(Espionage.RunMission(gameData, actor, target, targetCity, StealTechnology, EspionageAgent.Spy, EspionageSafetyLevel.Carefully).succeeded);

		actor.knownTechs.Clear();
		C7GameData.GameData.rng = new SequenceRandom(0, 80);
		EspionageMissionResult result = Espionage.RunMission(gameData, actor, target, targetCity, StealTechnology, EspionageAgent.Spy, EspionageSafetyLevel.Carefully);
		Assert.False(result.succeeded);
		Assert.True(result.harmlessFailure);
		Assert.DoesNotContain(tech.id, actor.knownTechs);
	}

	[Fact]
	public void StealTechnologyFailedRollIsCaught() {
		(C7GameData.GameData gameData, Player actor, Player target, _, City targetCity) = SetupPair();
		Relationship(actor, target).agentPlanted = true;
		C7GameData.GameData.rng = new FixedRandom(99);

		EspionageMissionResult result = Espionage.RunMission(gameData, actor, target, targetCity, StealTechnology, EspionageAgent.Spy, EspionageSafetyLevel.Carefully);

		Assert.False(result.succeeded);
		Assert.True(result.agentCaught);
		Assert.False(Relationship(actor, target).agentPlanted);
		Assert.Equal(1, Relationship(target, actor).caughtSpyCount);
	}

	[Fact]
	public void SabotageProductionHalvesStoredShieldsUpToTheItemCost() {
		(C7GameData.GameData gameData, Player actor, Player target, _, City targetCity) = SetupPair();
		Relationship(actor, target).agentPlanted = true;
		targetCity.itemBeingProduced = new UnitPrototype { shieldCost = 100 };
		targetCity.SetStoredShields(12);
		C7GameData.GameData.rng = new FixedRandom(50);

		EspionageMissionResult result = Espionage.RunMission(gameData, actor, target, targetCity, Espionage.SabotageProduction, EspionageAgent.Spy, EspionageSafetyLevel.Carefully);

		Assert.True(result.succeeded);
		Assert.False(result.agentCaught);
		Assert.Equal(6, targetCity.shieldsStored);

		// The stored shields never exceed the cost of the item in production.
		targetCity.itemBeingProduced = new UnitPrototype { shieldCost = 5 };
		targetCity.SetStoredShields(40);
		C7GameData.GameData.rng = new FixedRandom(50);
		Espionage.RunMission(gameData, actor, target, targetCity, Espionage.SabotageProduction, EspionageAgent.Spy, EspionageSafetyLevel.Carefully);
		Assert.Equal(5, targetCity.shieldsStored);
	}

	[Fact]
	public void SabotageProductionWithNothingInProductionHalvesTheBox() {
		(C7GameData.GameData gameData, Player actor, Player target, _, City targetCity) = SetupPair();
		Relationship(actor, target).agentPlanted = true;
		// A city with no item in production has no item cost to cap against, so
		// the box is halved rather than emptied.
		targetCity.itemBeingProduced = null;
		targetCity.SetStoredShields(12);
		C7GameData.GameData.rng = new FixedRandom(50);

		EspionageMissionResult result = Espionage.RunMission(gameData, actor, target, targetCity, Espionage.SabotageProduction, EspionageAgent.Spy, EspionageSafetyLevel.Carefully);

		Assert.True(result.succeeded);
		Assert.Equal(6, targetCity.shieldsStored);
	}

	[Fact]
	public void ExposeEnemySpyRemovesTheEnemyAgent() {
		(C7GameData.GameData gameData, Player actor, Player target, _, City targetCity) = SetupPair();
		Relationship(actor, target).agentPlanted = true;
		Relationship(target, actor).agentPlanted = true;
		C7GameData.GameData.rng = new FixedRandom(50);

		EspionageMissionResult result = Espionage.RunMission(gameData, actor, target, targetCity, Espionage.ExposeEnemySpy, EspionageAgent.Spy, EspionageSafetyLevel.Carefully);

		Assert.True(result.succeeded);
		Assert.False(result.agentCaught);
		Assert.False(Relationship(target, actor).agentPlanted);
		Assert.True(Relationship(actor, target).agentPlanted);
	}

	[Fact]
	public void ExposeEnemySpyWithNoEnemyAgentIsCaught() {
		(C7GameData.GameData gameData, Player actor, Player target, _, City targetCity) = SetupPair();
		Relationship(actor, target).agentPlanted = true;
		C7GameData.GameData.rng = new FixedRandom(50);

		EspionageMissionResult result = Espionage.RunMission(gameData, actor, target, targetCity, Espionage.ExposeEnemySpy, EspionageAgent.Spy, EspionageSafetyLevel.Carefully);

		Assert.False(result.succeeded);
		Assert.True(result.harmlessFailure);
		Assert.True(result.agentCaught);
		Assert.False(Relationship(actor, target).agentPlanted);
		Assert.Equal(1, Relationship(target, actor).caughtSpyCount);
	}

	[Fact]
	public void CatchingASpyMakesANonHumanTargetDeclareWar() {
		(C7GameData.GameData gameData, Player actor, Player target, _, City targetCity) = SetupPair();
		Relationship(actor, target).agentPlanted = true;
		// The mission roll fails, so the agent is caught.
		C7GameData.GameData.rng = new FixedRandom(99);

		Assert.True(PlayerRelationship.AtPeace(actor, target));
		EspionageMissionResult result = Espionage.RunMission(gameData, actor, target, targetCity, StealTechnology, EspionageAgent.Spy, EspionageSafetyLevel.Carefully);

		Assert.False(result.succeeded);
		Assert.True(result.agentCaught);
		// The civ that caught the spy declares war on the spy's owner.
		Assert.True(PlayerRelationship.AtWar(actor, target));
		Assert.Equal(1, Relationship(actor, target).warDeclarationCount);
	}

	[Fact]
	public void CatchingASpyDoesNotMakeAHumanTargetDeclareWar() {
		(C7GameData.GameData gameData, Player actor, Player target, _, City targetCity) = SetupPair();
		target.isHuman = true;
		Relationship(actor, target).agentPlanted = true;
		C7GameData.GameData.rng = new FixedRandom(99);

		EspionageMissionResult result = Espionage.RunMission(gameData, actor, target, targetCity, StealTechnology, EspionageAgent.Spy, EspionageSafetyLevel.Carefully);

		Assert.True(result.agentCaught);
		Assert.True(PlayerRelationship.AtPeace(actor, target));
		Assert.Equal(0, Relationship(actor, target).warDeclarationCount);
	}

	[Fact]
	public void CatchingASpyDoesNotRedeclareWarOnACivAlreadyAtWar() {
		(C7GameData.GameData gameData, Player actor, Player target, _, City targetCity) = SetupPair();
		Relationship(actor, target).agentPlanted = true;
		PlayerRelationship.DeclareWar(actor, target, false, 0);
		C7GameData.GameData.rng = new FixedRandom(99);

		EspionageMissionResult result = Espionage.RunMission(gameData, actor, target, targetCity, StealTechnology, EspionageAgent.Spy, EspionageSafetyLevel.Carefully);

		Assert.True(result.agentCaught);
		// The two are already at war, so catching the spy is not a new war
		// declaration: the catcher must not declare war on the actor a second
		// time, and must not overwrite the existing war's bookkeeping.
		Assert.True(PlayerRelationship.AtWar(actor, target));
		Assert.Equal(0, Relationship(actor, target).warDeclarationCount);
		Assert.Equal(1, Relationship(target, actor).warDeclarationCount);
	}

	[Fact]
	public void UnavailableMissionDoesNotSpendGoldOrChangeState() {
		(C7GameData.GameData gameData, Player actor, Player target, _, City targetCity) = SetupPair();
		C7GameData.GameData.rng = new FixedRandom(0);

		// A spy mission with no planted agent is unavailable.
		EspionageMissionResult result = Espionage.RunMission(gameData, actor, target, targetCity, Espionage.StealWorldMap, EspionageAgent.Spy, EspionageSafetyLevel.Carefully);

		Assert.False(result.ran);
		Assert.Equal(100000, actor.gold);
		Assert.False(Relationship(actor, target).agentPlanted);
	}

	[Fact]
	public void MsgRunEspionageMissionAppliesTheMission() {
		(C7GameData.GameData gameData, Player actor, Player target, _, City targetCity) = SetupPair();
		C7GameData.GameData.rng = new FixedRandom(0);

		new MsgRunEspionageMission(actor, target, targetCity, Espionage.BuildEmbassy, EspionageAgent.Diplomat).process();

		Assert.True(Relationship(actor, target).embassyEstablished);
		Assert.True(Relationship(target, actor).embassyEstablished);
	}
}
