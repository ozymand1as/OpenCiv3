using System.IO;
using System.Linq;
using System.Threading.Tasks;
using C7Engine;
using C7Engine.Lua;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using QueryCiv3;
using QueryCiv3.Biq;
using Xunit;

namespace EngineTests.GameData.Victory;

/// Exercises the BIQ and SAV import paths for the victory data against the
/// shipped Civilization III files. The base conquests.biq sets
/// GAME.DefaultVictoryConditions while leaving the per-condition flags clear,
/// and the scenarios clear it and set the flags individually.
public class ImportVictoryConditionsTest {

	private static string ScenarioPath(string fileName) {
		return Path.Combine(Civ3Location.GetCiv3Path(), "Conquests", "Conquests", fileName);
	}

	[SkippableFact]
	public void ShippedRules_UseTheDefaultVictoryConditions() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");

		BiqData biq = BiqData.LoadFile(PathUtils.defaultBicPath);
		VictoryConditions conditions = ImportCiv3.VictoryConditionsFromBiqGame(
			biq.Game[0], ImportCiv3.SpaceshipPartsNeededFrom(biq.RuleSpaceship));

		// The shipped conquests.biq stores DefaultVictoryConditions = 1 and
		// leaves all five per-condition flags clear, so reading those flags
		// literally would leave every standard condition disabled.
		Assert.True(conditions.AllowDominationVictory);
		Assert.True(conditions.AllowSpaceRaceVictory);
		Assert.True(conditions.AllowDiplomaticVictory);
		Assert.True(conditions.AllowConquestVictory);
		Assert.True(conditions.AllowCulturalVictory);

		// Wonder victory is not part of the standard set.
		Assert.False(conditions.AllowWonderVictory);
	}

	[SkippableFact]
	public void ShippedRules_CarryTheSpaceshipPartRequirements() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");

		BiqData biq = BiqData.LoadFile(PathUtils.defaultBicPath);
		VictoryConditions conditions = ImportCiv3.VictoryConditionsFromBiqGame(
			biq.Game[0], ImportCiv3.SpaceshipPartsNeededFrom(biq.RuleSpaceship));

		Assert.Equal(10, biq.Rule[0].NumberOfSpaceshipParts);
		Assert.Equal(biq.RuleSpaceship[0], conditions.SpaceshipPartsNeeded);
	}

	[SkippableFact]
	public void ScenarioWithoutTheDefaultVictoryConditions_ReadsTheExplicitFlags() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");
		string path = ScenarioPath("4 Middle Ages.biq");
		Skip.If(!File.Exists(path), "Middle Ages scenario not present.");

		BiqData biq = BiqData.LoadFile(path);
		VictoryConditions conditions = ImportCiv3.VictoryConditionsFromBiqGame(
			biq.Game[0], ImportCiv3.SpaceshipPartsNeededFrom(biq.RuleSpaceship));

		// This scenario clears DefaultVictoryConditions and enables domination
		// and conquest explicitly, so those two are the only ones allowed.
		Assert.True(conditions.AllowDominationVictory);
		Assert.True(conditions.AllowConquestVictory);
		Assert.False(conditions.AllowSpaceRaceVictory);
		Assert.False(conditions.AllowDiplomaticVictory);
		Assert.False(conditions.AllowCulturalVictory);
	}

	[SkippableFact]
	public void ShippedRules_CarryTheWholeVictoryPointParameterSet() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");

		// Measured from the shipped conquests.biq: every numeric parameter the
		// victory-point system reads, and all three flags of the 0x26000 group
		// (the shipped rules leave the group off).
		BiqData biq = BiqData.LoadFile(PathUtils.defaultBicPath);
		QueryCiv3.Biq.GAME game = biq.Game[0];

		Assert.Equal(50000, game.VictoryPointLimit);
		Assert.Equal(25, game.VictoryPointScoring);
		Assert.Equal(1000, game.CapturingSpecialUnit);
		Assert.Equal(10, game.WonderCost);
		Assert.Equal(5, game.DefeatingOpposingUnitCost);
		Assert.Equal(5, game.AdvancementCost);
		Assert.Equal(100, game.CityConquestPopulation);

		Assert.False(game.VictoryLocations);
		Assert.False(game.CaptureTheFlag);
		Assert.False(game.ReverseCaptureTheFlag);

		VictoryConditions conditions = ImportCiv3.VictoryConditionsFromBiqGame(
			game, ImportCiv3.SpaceshipPartsNeededFrom(biq.RuleSpaceship));
		Assert.Equal(50000, conditions.VictoryPointLimit);
		Assert.Equal(25, conditions.VictoryPointScoring);
		Assert.Equal(1000, conditions.CapturingSpecialUnit);
		Assert.Equal(10, conditions.WonderCost);
		Assert.Equal(5, conditions.DefeatingOpposingUnitCost);
		Assert.Equal(5, conditions.AdvancementCost);
		Assert.Equal(100, conditions.CityConquestPopulation);
		Assert.False(conditions.VictoryPointsEnabled);
	}

	[SkippableFact]
	public void ShippedRuleset_CarriesTheSameVictoryPointParametersAsTheBiq() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");

		// A freshly generated (non-BIQ) game takes its victory parameters from
		// ruleset.json, so the ruleset must not drift from the shipped BIQ.
		BiqData biq = BiqData.LoadFile(PathUtils.defaultBicPath);
		VictoryConditions fromBiq = ImportCiv3.VictoryConditionsFromBiqGame(
			biq.Game[0], ImportCiv3.SpaceshipPartsNeededFrom(biq.RuleSpaceship));
		SaveGame ruleset = GameMode.Load(PathUtils.GameModesDir, new GameMode.Config("civ3")).GetSave();
		VictoryConditions fromRuleset = ruleset.VictoryConditions;

		Assert.Equal(fromBiq.VictoryPointLimit, fromRuleset.VictoryPointLimit);
		Assert.Equal(fromBiq.VictoryPointScoring, fromRuleset.VictoryPointScoring);
		Assert.Equal(fromBiq.CapturingSpecialUnit, fromRuleset.CapturingSpecialUnit);
		Assert.Equal(fromBiq.WonderCost, fromRuleset.WonderCost);
		Assert.Equal(fromBiq.DefeatingOpposingUnitCost, fromRuleset.DefeatingOpposingUnitCost);
		Assert.Equal(fromBiq.AdvancementCost, fromRuleset.AdvancementCost);
		Assert.Equal(fromBiq.CityConquestPopulation, fromRuleset.CityConquestPopulation);
		Assert.Equal(fromBiq.VictoryPointsEnabled, fromRuleset.VictoryPointsEnabled);
	}

	[SkippableFact]
	public async Task ImportSav_ReadsTheSpaceshipPartsEachLeaderHasBuilt() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");

		// Fetch the shared save rather than assuming another test already did, so a
		// cold checkout exercises this instead of silently skipping it.
		string savePath = await SampleSaves.TryEnsureSampleSave();
		Skip.If(savePath == null, "Sample save could not be downloaded.");

		SaveGame save = ImportCiv3.ImportSav(savePath, PathUtils.defaultBicPath, _ => PathUtils.defaultPediaIconsPath);

		Assert.NotEmpty(save.Players);
		Assert.All(save.Players, player => Assert.Equal(10, player.spaceshipPartsBuilt.Count));
		Assert.Equal(Enumerable.Repeat(1, 10), save.VictoryConditions.SpaceshipPartsNeeded);
	}
}
