using System.Collections.Generic;
using System.Linq;
using C7Engine;
using C7Engine.Lua;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using Xunit;

namespace EngineTests.GameData.Victory;

/// The space-race condition reads Player.spaceshipPartsBuilt, so these tests
/// cover the two ways that list is filled - importing a BIQ/SAV, and completing
/// a spaceship-part building in game - plus the shipped ruleset that has to
/// model the ten parts as buildings for a newly generated game.
public class SpaceshipPartsTest {

	[Fact]
	public void SpaceshipPartsNeededFrom_CopiesTheRuleQuantities() {
		Assert.Equal(new List<int> { 2, 3 }, ImportCiv3.SpaceshipPartsNeededFrom([new[] { 2, 3 }]));
	}

	[Fact]
	public void SpaceshipPartsNeededFrom_WithoutASpaceship_IsEmpty() {
		Assert.Empty(ImportCiv3.SpaceshipPartsNeededFrom(null));
		Assert.Empty(ImportCiv3.SpaceshipPartsNeededFrom([]));
		Assert.Empty(ImportCiv3.SpaceshipPartsNeededFrom([null]));
	}

	[Fact]
	public void SpaceshipPartsBuiltFrom_KeepsTheSavedPartCounts() {
		Assert.Equal(new List<int> { 0, 1, 2 }, ImportCiv3.SpaceshipPartsBuiltFrom(new short[] { 0, 1, 2 }));
		Assert.Empty(ImportCiv3.SpaceshipPartsBuiltFrom(null));
	}

	[Fact]
	public void CompletingASpaceshipPartBuilding_AddsItToTheOwnersShip() {
		var game = VictoryTestHelpers.MakeGame();
		Player rome = VictoryTestHelpers.MakePlayer("player-2", "Rome");
		game.players.Add(rome);
		City city = VictoryTestHelpers.AddCity(game, rome, 1);

		city.AddBuilding(MakePartBuilding(game, "SS Thrusters", 0));
		city.AddBuilding(MakePartBuilding(game, "SS Engine", 1));
		// A part can be completed more than once when the rules ask for a
		// quantity above one.
		city.AddBuilding(MakePartBuilding(game, "SS Thrusters", 0));

		Assert.Equal(new List<int> { 2, 1 }, rome.spaceshipPartsBuilt);
	}

	[Fact]
	public void CompletingAnOrdinaryBuilding_DoesNotAddAPart() {
		var game = VictoryTestHelpers.MakeGame();
		Player rome = VictoryTestHelpers.MakePlayer("player-2", "Rome");
		game.players.Add(rome);
		City city = VictoryTestHelpers.AddCity(game, rome, 1);

		city.AddBuilding(new Building(new SaveBuilding { name = "Granary" }, game));

		Assert.Empty(rome.spaceshipPartsBuilt);
	}

	[Fact]
	public void ShippedRuleset_ModelsTheTenSpaceshipPartsAsBuildings() {
		SaveGame ruleset = GameMode.Load(PathUtils.GameModesDir, new GameMode.Config("civ3")).GetSave();

		List<int> parts = ruleset.Buildings
			.Where(building => building.spaceshipPart >= 0)
			.OrderBy(building => building.spaceshipPart)
			.Select(building => building.spaceshipPart)
			.ToList();

		Assert.Equal(Enumerable.Range(0, 10), parts);
	}

	private static Building MakePartBuilding(C7GameData.GameData game, string name, int part) {
		return new Building(new SaveBuilding { name = name, spaceshipPart = part }, game);
	}
}
