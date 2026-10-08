using System.IO;
using System.Linq;
using C7Engine;
using C7Engine.Lua;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using QueryCiv3;
using QueryCiv3.Biq;
using Xunit;

namespace EngineTests.GameData.Victory;

/// Pins the data the wonder-victory condition consumes: which buildings the
/// rules mark as Great Wonders, and the GAME flags that gate the condition.
/// Everything asserted here was measured out of the shipped conquests.biq and
/// the shipped scenarios with QueryCiv3.
public class WonderVictoryDataTest {

	private static string ScenarioPath(string fileName) {
		return Path.Combine(Civ3Location.GetCiv3Path(), "Conquests", "Conquests", fileName);
	}

	// The rules' Great-Wonder set, which the predicate walks. The predicate
	// needs no data the ruleset lacks, so this test is not Civ3-dependent: it
	// guards the rules file itself.
	[Fact]
	public void TheRulesetGreatWonderSet_IsTheTwentyNineShippedWonders() {
		SaveGame ruleset = GameMode.Load(PathUtils.GameModesDir, new GameMode.Config("civ3")).GetSave();
		var greatWonders = ruleset.Buildings
			.Where(b => b.greatWonderProperties != null)
			.Select(b => b.name)
			.ToList();
		var smallWonders = ruleset.Buildings
			.Where(b => b.isSmallWonder)
			.Select(b => b.name)
			.ToList();

		Assert.Equal(29, greatWonders.Count);
		Assert.Equal(11, smallWonders.Count);
		Assert.Empty(greatWonders.Intersect(smallWonders));

		// Named anchors, including the ones a wrong flag mask would swallow or
		// invent: the wonder-heavy fourth era and the Apollo Program, which is a
		// Small Wonder and must never satisfy the predicate.
		Assert.Contains("The Pyramids", greatWonders);
		Assert.Contains("The United Nations", greatWonders);
		Assert.Contains("Knights Templar", greatWonders);
		Assert.DoesNotContain("Apollo Program", greatWonders);
	}

	[SkippableFact]
	public void ShippedBiq_DefinesTheSameGreatAndSmallWondersAsTheRuleset() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");

		BiqData biq = BiqData.LoadFile(PathUtils.defaultBicPath);

		// A building is a Great Wonder when its improvement flag word holds bit
		// 0x4 and a Small Wonder when it holds 0x8; QueryCiv3 reads both from
		// the same flag byte, and the wonder predicate counts only the former.
		var biqGreatWonders = Enumerable.Range(0, biq.Bldg.Length)
			.Select(i => biq.Bldg[i])
			.Where(b => b.Wonder && !b.SmallWonder)
			.Select(b => b.Name)
			.OrderBy(n => n)
			.ToList();
		var biqSmallWonders = Enumerable.Range(0, biq.Bldg.Length)
			.Select(i => biq.Bldg[i])
			.Where(b => b.SmallWonder && !b.Wonder)
			.Select(b => b.Name)
			.OrderBy(n => n)
			.ToList();

		Assert.Equal(29, biqGreatWonders.Count);
		Assert.Equal(11, biqSmallWonders.Count);

		SaveGame ruleset = GameMode.Load(PathUtils.GameModesDir, new GameMode.Config("civ3")).GetSave();
		var rulesetGreatWonders = ruleset.Buildings
			.Where(b => b.greatWonderProperties != null && !b.isSmallWonder)
			.Select(b => b.name)
			.OrderBy(n => n)
			.ToList();
		var rulesetSmallWonders = ruleset.Buildings
			.Where(b => b.isSmallWonder && b.greatWonderProperties == null)
			.Select(b => b.name)
			.OrderBy(n => n)
			.ToList();

		Assert.Equal(biqGreatWonders, rulesetGreatWonders);
		Assert.Equal(biqSmallWonders, rulesetSmallWonders);
	}

	[SkippableFact]
	public void ShippedBiq_CarriesTheWonderAndVictoryPointGates() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");

		BiqData biq = BiqData.LoadFile(PathUtils.defaultBicPath);
		GAME game = biq.Game[0];

		// conquests.biq enables the five standard conditions through
		// DefaultVictoryConditions and stores every per-condition flag clear -
		// including the wonder gate (bit 0x10000) and the three type-8 gates
		// (0x2000, 0x4000, 0x20000). The shipped ruleset mirrors that with
		// allowWonderVictory false.
		Assert.Equal(1, game.DefaultVictoryConditions);
		Assert.False(game.DominationVictory);
		Assert.False(game.SpaceRaceVictory);
		Assert.False(game.DiplomaticVictory);
		Assert.False(game.ConquestVictory);
		Assert.False(game.CulturalVictory);
		Assert.False(game.WonderVictory);
		Assert.False(game.VictoryLocations);
		Assert.False(game.CaptureTheFlag);
		Assert.False(game.ReverseCaptureTheFlag);

		VictoryConditions conditions = ImportCiv3.VictoryConditionsFromBiqGame(
			game, ImportCiv3.SpaceshipPartsNeededFrom(biq.RuleSpaceship));
		Assert.True(conditions.AllowDominationVictory);
		Assert.True(conditions.AllowSpaceRaceVictory);
		Assert.True(conditions.AllowDiplomaticVictory);
		Assert.True(conditions.AllowConquestVictory);
		Assert.True(conditions.AllowCulturalVictory);
		Assert.False(conditions.AllowWonderVictory);
		Assert.False(conditions.VictoryLocations);

		// "1 Mesopotamia.biq" is a shipped scenario that clears the default set
		// and sets the wonder gate together with the victory-point-scoring gate,
		// so both halves of the import are pinned against real data.
		string path = ScenarioPath("1 Mesopotamia.biq");
		Skip.If(!File.Exists(path), "Mesopotamia scenario not present.");
		BiqData mesopotamia = BiqData.LoadFile(path);
		Assert.Equal(0, mesopotamia.Game[0].DefaultVictoryConditions);
		Assert.True(mesopotamia.Game[0].WonderVictory);
		Assert.True(mesopotamia.Game[0].VictoryLocations);
		Assert.False(mesopotamia.Game[0].DominationVictory);
		Assert.False(mesopotamia.Game[0].ConquestVictory);

		VictoryConditions mesopotamiaConditions = ImportCiv3.VictoryConditionsFromBiqGame(
			mesopotamia.Game[0], ImportCiv3.SpaceshipPartsNeededFrom(mesopotamia.RuleSpaceship));
		Assert.True(mesopotamiaConditions.AllowWonderVictory);
		Assert.True(mesopotamiaConditions.VictoryLocations);
		Assert.False(mesopotamiaConditions.AllowDominationVictory);
		Assert.False(mesopotamiaConditions.AllowConquestVictory);
	}
}
