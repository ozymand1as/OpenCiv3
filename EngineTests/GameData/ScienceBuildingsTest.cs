using System.Collections.Generic;
using System.Linq;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using Xunit;

namespace EngineTests.GameData;

// Guards the shipped ruleset's research-building flags. A typo in
// C7/Lua/civ3/ruleset.json would otherwise do nothing at all, since an unknown
// flag on the wrong building is simply never consulted.
public class ScienceBuildingsTest : IClassFixture<SaveGameFixture> {
	private readonly SaveGameFixture fixture;

	public ScienceBuildingsTest(SaveGameFixture fixture) {
		this.fixture = fixture;
	}

	public static IEnumerable<object[]> ResearchBuildings() {
		return new[] {
			new object[] { "Library", SaveBuilding.Flag.Plus50PercentResearch },
			new object[] { "University", SaveBuilding.Flag.Plus50PercentResearch },
			new object[] { "Research Lab", SaveBuilding.Flag.Plus50PercentResearch },
			new object[] { "Copernicus' Observatory", SaveBuilding.Flag.DoublesResearchOutput },
			new object[] { "Newton's University", SaveBuilding.Flag.DoublesResearchOutput },
			new object[] { "SETI program", SaveBuilding.Flag.DoublesResearchOutput },
		};
	}

	[Theory]
	[MemberData(nameof(ResearchBuildings))]
	public void ShippedRulesetMarksTheResearchBuildings(string name, SaveBuilding.Flag flag) {
		SaveBuilding building = fixture.saveGame.Buildings.Find(b => b.name == name);

		Assert.NotNull(building);
		Assert.Contains(flag, building.flags);
	}

	[Theory]
	[MemberData(nameof(ResearchBuildings))]
	public void ResearchFlagsReachTheRuntimeBuilding(string name, SaveBuilding.Flag flag) {
		Building building = fixture.saveGame.ToGameData(fixture.behaviors).Buildings.Find(b => b.name == name);

		Assert.NotNull(building);
		if (flag == SaveBuilding.Flag.Plus50PercentResearch) {
			Assert.True(building.plus50PercentResearch);
		} else {
			Assert.True(building.doublesResearchOutput);
		}
	}

	[Fact]
	public void OnlyTheSixResearchBuildingsCarryResearchFlags() {
		List<string> flagged = fixture.saveGame.Buildings
			.Where(b => b.flags.Contains(SaveBuilding.Flag.Plus50PercentResearch)
				|| b.flags.Contains(SaveBuilding.Flag.DoublesResearchOutput))
			.Select(b => b.name)
			.OrderBy(n => n)
			.ToList();

		Assert.Equal(
			new List<string> {
				"Copernicus' Observatory", "Library", "Newton's University",
				"Research Lab", "SETI program", "University",
			},
			flagged);
	}

	[Fact]
	public void NoBuildingCarriesBothResearchFlags() {
		Assert.DoesNotContain(
			fixture.saveGame.Buildings,
			b => b.flags.Contains(SaveBuilding.Flag.Plus50PercentResearch)
				&& b.flags.Contains(SaveBuilding.Flag.DoublesResearchOutput));
	}
}
