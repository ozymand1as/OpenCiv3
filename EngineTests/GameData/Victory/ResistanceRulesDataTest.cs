using System.Collections.Generic;
using System.IO;
using System.Linq;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using QueryCiv3;
using Xunit;

namespace EngineTests.GameData.Victory;

/// The shipped resistance data, measured from Civilization III Complete's
/// conquests.biq with a QueryCiv3 probe and asserted here in full: the CULT
/// table's resistance chances, the whole 8x8 government-pair
/// ResistanceModifier matrix, the difficulty MilitaryLaw values, and the
/// GAME-section numbers the city-loss path reads. Asserting the whole set
/// (not just the values some code path happened to touch) is deliberate: a
/// ruleset typo must not be able to pass silently.
public class ResistanceRulesDataTest : IClassFixture<SaveGameFixture> {

	private readonly SaveGameFixture fixture;

	public ResistanceRulesDataTest(SaveGameFixture fixture) {
		this.fixture = fixture;
	}

	private static readonly string[] CultureNames = {
		"in awe of", "admirers of", "impressed with", "unimpressed by",
		"dismissive of", "disdainful of",
	};
	private static readonly int[] CultureRatios = { 300, 200, 100, 75, 50, 33 };
	private static readonly int[] PropagandaChances = { 30, 25, 20, 10, 5, 3 };
	private static readonly int[] InitialResistance = { 40, 50, 60, 70, 80, 90 };
	private static readonly int[] ContinuedResistance = { 30, 40, 50, 60, 70, 80 };

	private static readonly string[] GovernmentNames = {
		"Anarchy", "Despotism", "Monarchy", "Communism",
		"Republic", "Democracy", "Fascism", "Feudalism",
	};
	private static readonly int[,] ResistanceModifiers = {
		{ 0, 5, 5, 5, 5, 5, 0, 0 },
		{ -5, 0, 5, 5, 5, 5, 0, 0 },
		{ -5, -5, 0, 5, 5, 5, 0, 0 },
		{ -5, -5, -5, 0, -5, 5, 0, 0 },
		{ -5, -5, 5, 5, 0, 5, 0, 0 },
		{ -5, -5, -5, 5, -5, 0, 0, 0 },
		{ -5, -5, 0, -10, -5, -5, 0, 0 },
		{ -5, -5, 5, 5, 0, 0, 5, 0 },
	};

	// The MIDDLE member of the same GOVT_GOVT triple - the propaganda modifier
	// that Initiate Propaganda's penalty total reads (spec 24 section 6.6). It is
	// asserted from the same two sources as the resistance matrix, because the two
	// columns are easy to confuse and a wrong column would still look plausible.
	private static readonly int[,] BriberyModifiers = {
		{ 0, 0, 0, 0, 0, 0, 0, 0 },
		{ 15, 0, -10, -15, -20, -30, 0, 0 },
		{ 20, 15, 0, -5, -10, -25, 0, 0 },
		{ 25, 20, 5, 0, -10, -20, 0, 0 },
		{ 30, 25, 10, 8, 0, -5, 0, 0 },
		{ 35, 30, 20, 10, 5, 0, 0, 0 },
		{ 0, 0, -5, -20, -10, -10, 0, 0 },
		{ 20, 15, 0, -10, -10, -20, -25, 0 },
	};

	private static void AssertCultureLevels(List<CultureLevel> levels) {
		Assert.Equal(CultureNames.Length, levels.Count);
		for (int i = 0; i < levels.Count; i++) {
			Assert.Equal(CultureNames[i], levels[i].name);
			Assert.Equal(CultureRatios[i], levels[i].cultureRatioPercentage);
			Assert.Equal(PropagandaChances[i], levels[i].chanceOfSuccessfulPropaganda);
			Assert.Equal(InitialResistance[i], levels[i].initialResistanceChance);
			Assert.Equal(ContinuedResistance[i], levels[i].continuedResistanceChance);
		}
	}

	private static void AssertGovernments(List<Government> governments) {
		Assert.Equal(GovernmentNames.Length, governments.Count);
		for (int row = 0; row < governments.Count; row++) {
			Assert.Equal(GovernmentNames[row], governments[row].name);
			Assert.Equal(GovernmentNames.Length, governments[row].resistanceModifiers.Count);
			for (int column = 0; column < GovernmentNames.Length; column++) {
				Assert.Equal(ResistanceModifiers[row, column],
					governments[row].ResistanceModifierAgainst(column));
			}

			// The propaganda modifier is a different column of the same triple, so it
			// is asserted here too - a wrong-column import would otherwise pass.
			Assert.Equal(GovernmentNames.Length, governments[row].briberyModifiers.Count);
			for (int column = 0; column < GovernmentNames.Length; column++) {
				Assert.Equal(BriberyModifiers[row, column],
					governments[row].BriberyModifierAgainst(column));
			}
		}
	}

	[Fact]
	public void ShippedRuleset_CarriesTheWholeResistanceDataSet() {
		SaveGame save = fixture.saveGame;

		AssertCultureLevels(save.CultureLevels);
		AssertGovernments(save.Governments);

		// Every shipped difficulty quells exactly one citizen per unit.
		Assert.Equal(8, save.Difficulties.Count);
		Assert.All(save.Difficulties, d => Assert.Equal(1, d.MilitaryLaw));

		// The GAME-section numbers the city-loss path reads (spec 25
		// section 4): shipped conquests.biq stores CityEliminationCount 1
		// with the elimination and regicide rules off, and a conquest
		// multiple of 100.
		Assert.Equal(1, save.VictoryConditions.CityEliminationCount);
		Assert.False(save.VictoryConditions.CityElimination);
		Assert.False(save.VictoryConditions.Regicide);
		Assert.False(save.VictoryConditions.MassRegicide);
		Assert.Equal(100, save.VictoryConditions.CityConquestPopulation);
	}

	[SkippableFact]
	public void ShippedBiq_CarriesTheWholeResistanceDataSet() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");

		BiqData biq = BiqData.LoadFile(PathUtils.defaultBicPath);

		AssertCultureLevels(ImportCiv3.BuildCultureLevels(biq));

		// Governments, through the same builder the importer uses.
		List<SaveTech> techs = Enumerable.Range(0, biq.Tech.Length)
			.Select(i => new SaveTech { id = ID.FromString("Tech-" + (i + 1)) })
			.ToList();
		AssertGovernments(ImportCiv3.BuildGovernments(biq, new ID.Factory(), techs));

		// DIFF.Quelled_Citizens (MilitaryLaw) is 1 for all eight levels.
		Assert.Equal(8, biq.Diff.Length);
		Assert.All(biq.Diff, d => Assert.Equal(1, d.MilitaryLaw));

		// The victory parameters, through the same converter the importer
		// uses (spec 25 section 6).
		VictoryConditions rules = ImportCiv3.VictoryConditionsFromBiqGame(
			biq.Game[0], ImportCiv3.SpaceshipPartsNeededFrom(biq.RuleSpaceship));
		Assert.Equal(1, rules.CityEliminationCount);
		Assert.False(rules.CityElimination);
		Assert.False(rules.Regicide);
		Assert.False(rules.MassRegicide);
		Assert.Equal(100, rules.CityConquestPopulation);
	}
}
