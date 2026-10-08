using System.IO;
using System.Linq;
using System.Threading.Tasks;
using C7Engine;
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
