using System.Collections.Generic;
using System.IO;
using System.Linq;
using C7Engine;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using Xunit;

namespace EngineTests.GameData.Victory;

/// The production half of the space race, read out of the original's
/// Leader_can_build_city_improvement (0x56a2a0). Civ3 offers a spaceship part
/// only to a civ that owns the improvement carrying BLDG.BuildSpaceshipParts
/// (the Apollo Program in the shipped rules), and it caps the copies per civ
/// at General.SpaceshipPartsNeeded[part] rather than at one copy per city.
/// The completion hook that counts a finished part lives in City.AddBuilding
/// and is covered by SpaceshipPartsTest.
public class SpaceshipPartProductionTest {

	private static SaveGame LoadShippedRuleset() {
		string path = Path.Combine(PathUtils.GameModesDir, "civ3", "ruleset.json");
		return SaveGame.LoadFromJSON(File.ReadAllText(path));
	}

	/// A game whose rules require `partsNeeded[i]` copies of part `i`, installed
	/// as the engine's game data because CanProduce reads the requirement from
	/// it, like it reads the great-wonder list.
	private static C7GameData.GameData MakeGame(params int[] partsNeeded) {
		C7GameData.GameData game = VictoryTestHelpers.MakeGame();
		game.victoryConditions = new VictoryConditions { SpaceshipPartsNeeded = partsNeeded.ToList() };
		EngineStorage.InitializeGameDataForTests(game);
		return game;
	}

	private static Player AddPlayer(C7GameData.GameData game) {
		Player rome = VictoryTestHelpers.MakePlayer("player-2", "Rome");
		game.players.Add(rome);
		return rome;
	}

	private static Building MakeBuilding(C7GameData.GameData game, string name, int part = -1, bool gate = false) {
		SaveBuilding data = new() { name = name, spaceshipPart = part };
		if (gate) {
			data.flags.Add(SaveBuilding.Flag.BuildSpaceshipParts);
		}
		return new Building(data, game);
	}

	[Fact]
	public void PartIsNotProducibleBeforeTheCivHasTheGateBuilding() {
		C7GameData.GameData game = MakeGame(1, 1, 1, 1, 1, 1, 1, 1, 1, 1);
		Player rome = AddPlayer(game);
		City city = VictoryTestHelpers.AddCity(game, rome, 1);
		Building thrusters = MakeBuilding(game, "SS Thrusters", part: 0);

		Assert.False(thrusters.CanProduce(city, []));
	}

	[Fact]
	public void PartIsProducibleOnceTheCivHasTheGateBuildingAnywhere() {
		C7GameData.GameData game = MakeGame(1, 1, 1, 1, 1, 1, 1, 1, 1, 1);
		Player rome = AddPlayer(game);
		City city = VictoryTestHelpers.AddCity(game, rome, 1);
		City other = VictoryTestHelpers.AddCity(game, rome, 1);
		Building apollo = MakeBuilding(game, "Apollo Program", gate: true);
		Building thrusters = MakeBuilding(game, "SS Thrusters", part: 0);

		// Apollo is built in another city of the same civ, which is what makes
		// this a per-civ gate rather than a per-city one.
		other.AddBuilding(apollo);

		Assert.True(thrusters.CanProduce(city, []));
	}

	[Fact]
	public void TheGateBuildingItselfDoesNotUnlockThePartsOfAnotherCiv() {
		C7GameData.GameData game = MakeGame(1, 1, 1, 1, 1, 1, 1, 1, 1, 1);
		Player rome = AddPlayer(game);
		Player egypt = VictoryTestHelpers.MakePlayer("player-3", "Egypt");
		game.players.Add(egypt);
		City romeCity = VictoryTestHelpers.AddCity(game, rome, 1);
		City egyptCity = VictoryTestHelpers.AddCity(game, egypt, 1);
		egyptCity.AddBuilding(MakeBuilding(game, "Apollo Program", gate: true));

		Assert.False(MakeBuilding(game, "SS Thrusters", part: 0).CanProduce(romeCity, []));
	}

	[Theory]
	[InlineData(0, true)]
	[InlineData(1, false)]
	public void TheShippedOneOfEachRequirementStopsAfterOneCopy(int built, bool producible) {
		C7GameData.GameData game = MakeGame(1);
		Player rome = AddPlayer(game);
		City city = VictoryTestHelpers.AddCity(game, rome, 1);
		city.AddBuilding(MakeBuilding(game, "Apollo Program", gate: true));
		Building thrusters = MakeBuilding(game, "SS Thrusters", part: 0);
		for (int i = 0; i < built; ++i) {
			city.AddBuilding(thrusters);
		}

		Assert.Equal(built, rome.spaceshipPartsBuilt.ElementAtOrDefault(0));
		Assert.Equal(producible, thrusters.CanProduce(city, []));
	}

	[Theory]
	[InlineData(0, true)]
	[InlineData(1, true)]
	[InlineData(2, false)]
	public void APartTheRulesRequireTwiceStopsAtTwoCopies(int built, bool producible) {
		C7GameData.GameData game = MakeGame(2);
		Player rome = AddPlayer(game);
		City city = VictoryTestHelpers.AddCity(game, rome, 1);
		city.AddBuilding(MakeBuilding(game, "Apollo Program", gate: true));
		Building thrusters = MakeBuilding(game, "SS Thrusters", part: 0);
		// The copies all go into the same city: Civ3 does not veto a part because
		// the city already holds one, only because the civ has enough.
		for (int i = 0; i < built; ++i) {
			city.AddBuilding(thrusters);
		}

		Assert.Equal(built, rome.spaceshipPartsBuilt.ElementAtOrDefault(0));
		Assert.Equal(producible, thrusters.CanProduce(city, []));
	}

	[Fact]
	public void APartBeingBuiltByAnotherCityCountsTowardTheCivLimit() {
		C7GameData.GameData game = MakeGame(1);
		Player rome = AddPlayer(game);
		City city = VictoryTestHelpers.AddCity(game, rome, 1);
		City other = VictoryTestHelpers.AddCity(game, rome, 1);
		other.AddBuilding(MakeBuilding(game, "Apollo Program", gate: true));
		Building thrusters = MakeBuilding(game, "SS Thrusters", part: 0);
		other.SetItemBeingProduced(thrusters);

		// The original counts the parts a civ is already producing, so the
		// single copy this ruleset requires is spoken for.
		Assert.False(thrusters.CanProduce(city, []));
	}

	[Fact]
	public void AnOrdinaryBuildingIsStillVetoedByTheCopyItsCityAlreadyHas() {
		C7GameData.GameData game = MakeGame(1);
		Player rome = AddPlayer(game);
		City city = VictoryTestHelpers.AddCity(game, rome, 1);
		Building granary = MakeBuilding(game, "Granary");

		Assert.True(granary.CanProduce(city, []));
		city.AddBuilding(granary);
		Assert.False(granary.CanProduce(city, []));
	}

	[Fact]
	public void TheGateFlagReachesTheRuntimeBuilding() {
		C7GameData.GameData game = MakeGame(1);

		Assert.True(MakeBuilding(game, "Apollo Program", gate: true).buildsSpaceshipParts);
		Assert.False(MakeBuilding(game, "Granary").buildsSpaceshipParts);
	}

	[Fact]
	public void SpaceshipPartsNeededForDefaultsToTheShippedOneOfEach() {
		VictoryConditions shipped = new();

		for (int part = 0; part < 10; ++part) {
			Assert.Equal(1, shipped.SpaceshipPartsNeededFor(part));
		}

		Assert.Equal(3, new VictoryConditions { SpaceshipPartsNeeded = [3] }.SpaceshipPartsNeededFor(0));
		// A ruleset that lists fewer requirements than the parts it defines
		// keeps the shipped default rather than making the part impossible.
		Assert.Equal(1, new VictoryConditions { SpaceshipPartsNeeded = [2] }.SpaceshipPartsNeededFor(5));
	}

	[Fact]
	public void TheShippedRulesetGivesTheGateFlagToApolloOnly() {
		SaveGame ruleset = LoadShippedRuleset();

		List<string> gated = ruleset.Buildings
			.Where(building => building.flags.Contains(SaveBuilding.Flag.BuildSpaceshipParts))
			.Select(building => building.name)
			.ToList();
		Assert.Equal(new List<string> { "Apollo Program" }, gated);

		// None of the ten parts carries the gate flag, and every one of them
		// carries a part index, so the flag cannot be smuggled onto a part.
		List<SaveBuilding> parts = ruleset.Buildings.Where(building => building.spaceshipPart >= 0).ToList();
		Assert.Equal(Enumerable.Range(0, 10), parts.Select(building => building.spaceshipPart).OrderBy(i => i));
		Assert.All(parts, part => Assert.DoesNotContain(SaveBuilding.Flag.BuildSpaceshipParts, part.flags));
	}

	[Fact]
	public void TheShippedRulesetGateFlagReachesTheRuntimeBuilding() {
		SaveBuilding apolloData = LoadShippedRuleset().Buildings.Single(building => building.name == "Apollo Program");

		Assert.Contains(SaveBuilding.Flag.BuildSpaceshipParts, apolloData.flags);
		Assert.True(new Building(apolloData, MakeGame(1)).buildsSpaceshipParts);
	}
}

/// The same gate, but with the runtime Buildings a real game gets, so the
/// ruleset's flag, the part's required technology and its required resource
/// are all resolved exactly as they are in play.
public class SpaceshipPartRulesetGateTest : IClassFixture<SaveGameFixture> {
	private readonly SaveGameFixture fixture;

	public SpaceshipPartRulesetGateTest(SaveGameFixture fixture) {
		this.fixture = fixture;
	}

	[Fact]
	public void TheShippedRulesetGatesTheShippedPartsOnTheShippedApollo() {
		C7GameData.GameData game = fixture.saveGame.ToGameData(fixture.behaviors);
		Building apollo = game.Buildings.Single(building => building.name == "Apollo Program");
		Building thrusters = game.Buildings.Single(building => building.name == "SS Thrusters");
		Assert.True(apollo.buildsSpaceshipParts);
		Assert.False(thrusters.buildsSpaceshipParts);

		Player rome = AddPlayer(game);
		City city = VictoryTestHelpers.AddCity(game, rome, 1);
		City other = VictoryTestHelpers.AddCity(game, rome, 1);
		rome.knownTechs.Add(thrusters.requiredTech.id);
		HashSet<Resource> resources = [game.Resources.Single(resource => resource.Key == "Aluminum")];

		Assert.False(thrusters.CanProduce(city, resources));

		other.AddBuilding(apollo);
		Assert.True(thrusters.CanProduce(city, resources));
	}

	private static Player AddPlayer(C7GameData.GameData game) {
		Player rome = VictoryTestHelpers.MakePlayer("player-2", "Rome");
		game.players.Add(rome);
		return rome;
	}
}
