using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using C7Engine;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using QueryCiv3;
using QueryCiv3.Biq;
using Xunit;

namespace EngineTests.GameData;

/// <summary>
/// The BIQ-import half of 18_terrain_improvement.md sections 6 and 7: the
/// shipped conquests.biq values the import maps, and the ImportCiv3 code that
/// maps them (BLDG.Pollution, the two pollution building flags and the unit's
/// ClearPollution order).
///
/// These read the shipped BIQ rather than the Lua ruleset, which the other
/// pollution tests cover. They are skipped only when no Civ3 install is present
/// (the same visible skip the rest of the Civ3-dependent suite uses); with
/// CIV3_HOME set they run for real.
/// </summary>
public class PollutionBiqImportTest {
	private const string SkipReason = "No Civ3 install found.";

	// The shipped rules file. It has no map, so it cannot go through ImportBiq
	// (which builds a SaveGame from the BIQ's tiles); it is read with QueryCiv3
	// directly to pin the values, and its mapping helpers are exercised here.
	private static string ConquestsBiqPath =>
		Path.Combine(Civ3Location.GetCiv3Path(), "Conquests", "conquests.biq");

	// A shipped Conquest with a map, so the full ImportBiq path can run. Its
	// own BLDG table carries a polluting building (Worker Housing, pollution 1)
	// and its Worker carries the ClearPollution order.
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
	public void ShippedConquestsBiq_CarriesThePollutionRules() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), SkipReason);

		BiqData biq = BiqData.LoadFile(ConquestsBiqPath);

		// BLDG.Pollution, as measured out of the shipped file.
		Assert.Equal(4, biq.Bldg.First(b => b.Name == "Iron Works").Pollution);
		Assert.Equal(2, biq.Bldg.First(b => b.Name == "Factory").Pollution);
		Assert.Equal(2, biq.Bldg.First(b => b.Name == "Coal Plant").Pollution);
		Assert.Equal(2, biq.Bldg.First(b => b.Name == "Manufacturing Plant").Pollution);
		Assert.Equal(2, biq.Bldg.First(b => b.Name == "Offshore Platform").Pollution);
		Assert.Equal(1, biq.Bldg.First(b => b.Name == "Research Lab").Pollution);
		Assert.Equal(1, biq.Bldg.First(b => b.Name == "Airport").Pollution);
		Assert.Equal(1, biq.Bldg.First(b => b.Name == "Commercial Dock").Pollution);

		// Mass Transit System is the only Removes_Population_Pollution building
		// and Recycling Center the only Reduces_Buildings_Pollution one.
		Assert.True(biq.Bldg.First(b => b.Name == "Mass Transit System").RemovesPopulationPollution);
		Assert.True(biq.Bldg.First(b => b.Name == "Recycling Center").ReducesBuildingPollution);
		Assert.DoesNotContain(biq.Bldg, b => b.Pollution != 0 && b.RemovesPopulationPollution);
		Assert.DoesNotContain(biq.Bldg, b => b.Pollution != 0 && b.ReducesBuildingPollution);

		Assert.Equal(12, biq.Rule[0].MaximumLevel2CitySize);
	}

	[SkippableFact]
	public void ShippedConquestsBiq_MapsThePollutionBuildingFlags() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), SkipReason);

		// conquests.biq has no map, so it cannot go through ImportBiq, and no
		// shipped scenario's BLDG table sets either flag. The mapping from the
		// BLDG bits to the SaveBuilding flags is therefore pinned here through
		// the same helper ImportBuildings uses, against the shipped records.
		BiqData biq = BiqData.LoadFile(ConquestsBiqPath);

		Assert.Contains(SaveBuilding.Flag.RemovesPopulationPollution,
			ImportCiv3.LoadBuildingFlags(biq.Bldg.First(b => b.Name == "Mass Transit System")));
		Assert.DoesNotContain(SaveBuilding.Flag.ReducesBuildingPollution,
			ImportCiv3.LoadBuildingFlags(biq.Bldg.First(b => b.Name == "Mass Transit System")));

		Assert.Contains(SaveBuilding.Flag.ReducesBuildingPollution,
			ImportCiv3.LoadBuildingFlags(biq.Bldg.First(b => b.Name == "Recycling Center")));
		Assert.DoesNotContain(SaveBuilding.Flag.RemovesPopulationPollution,
			ImportCiv3.LoadBuildingFlags(biq.Bldg.First(b => b.Name == "Recycling Center")));

		// A polluting building without either flag must not acquire one.
		Assert.DoesNotContain(SaveBuilding.Flag.RemovesPopulationPollution,
			ImportCiv3.LoadBuildingFlags(biq.Bldg.First(b => b.Name == "Factory")));
		Assert.DoesNotContain(SaveBuilding.Flag.ReducesBuildingPollution,
			ImportCiv3.LoadBuildingFlags(biq.Bldg.First(b => b.Name == "Factory")));
	}

	[SkippableFact]
	public void ShippedConquestsBiq_MapsTheWorkersClearPollutionOrder() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), SkipReason);

		BiqData biq = BiqData.LoadFile(ConquestsBiqPath);
		PRTO worker = biq.Prto.First(u => u.Name == "Worker");
		Assert.True(worker.ClearPollution);

		Assert.Contains(TerraformKey.ClearDamage, ImportCiv3.GetUnitTerraforms(worker));
	}

	[SkippableFact]
	public void ImportBiq_CarriesTheBuildingPollutionAndTheWorkersClearDamageOrder() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), SkipReason);

		// A scenario with a map runs the whole ImportBiq path: its own BLDG
		// table carries Worker Housing (pollution 1, measured out of the file)
		// and its Worker carries ClearPollution, so the two field assignments
		// this covers are the ones ImportBuildings and ImportUnitPrototypes make.
		EngineStorage.animationsEnabled = false;
		SaveGame save = ImportCiv3.ImportBiq(ScenarioPath, PathUtils.defaultBicPath, GetPediaIconsPath("Conquests/Conquests"));

		// BLDG.Pollution reaches the SaveBuilding.
		Assert.Equal(1, save.Buildings.First(b => b.name == "Worker Housing").pollution);
		Assert.Equal(0, save.Buildings.First(b => b.name == "Barracks").pollution);

		// The Worker's ClearPollution order becomes the Clear Damage terraform,
		// and a unit without the order does not get it.
		SaveTerraform clearDamage = save.TerraForms.First(t => t.Name == "Clear Damage");
		SaveUnitPrototype worker = save.UnitPrototypes.First(u => u.name == "Worker");
		Assert.Contains(clearDamage.Id, worker.terraformActions);

		SaveUnitPrototype warrior = save.UnitPrototypes.First(u => u.name == "Warrior");
		Assert.DoesNotContain(clearDamage.Id, warrior.terraformActions);
	}
}
