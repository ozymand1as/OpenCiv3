using System.Collections.Generic;
using System.Linq;
using C7Engine;
using C7Engine.Lua;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using Xunit;

namespace EngineTests.GameData.Victory;

public class VictoryRegistrationTest {

	private static C7GameData.GameData MakeGame(VictoryConditions conditions) {
		return new C7GameData.GameData {
			turn = 1,
			history = new Dictionary<string, List<HistTurnRecord>>(),
			victoryConditions = conditions,
			timeOptions = new TimeOptions { turnLimit = 540 },
		};
	}

	[Fact]
	public void ConvertVictoryConditions_RegistersTheEnabledConditionsInTheOriginalsOrder() {
		var game = MakeGame(new VictoryConditions {
			AllowDominationVictory = true,
			AllowSpaceRaceVictory = true,
			AllowDiplomaticVictory = true,
			AllowConquestVictory = true,
			AllowCulturalVictory = true,
		});

		SaveGame.ConvertVictoryConditions(game);

		// Score is registered first so the status screen can render it, but it
		// is a tie-break rather than a condition. The rest follow the order in
		// which the original awards them: conquest, space race, domination,
		// cultural, diplomatic, then the turn limit.
		Assert.Equal(
			[
				typeof(ScoreVictory),
				typeof(ConquestVictory),
				typeof(SpaceRaceVictory),
				typeof(DominationVictory),
				typeof(CulturalVictory),
				typeof(DiplomaticVictory),
				typeof(TimeLimitVictory),
			],
			game.victories.Select(v => v.GetType()));
	}

	[Fact]
	public void ConvertVictoryConditions_SkipsConditionsTheRulesDisable() {
		var game = MakeGame(new VictoryConditions { AllowDominationVictory = true });

		SaveGame.ConvertVictoryConditions(game);

		Assert.Equal(
			[typeof(ScoreVictory), typeof(DominationVictory), typeof(TimeLimitVictory)],
			game.victories.Select(v => v.GetType()));
	}

	[Fact]
	public void ConvertVictoryConditions_WithNoConditions_OnlyScoresAndEndsAtTheTurnLimit() {
		var game = MakeGame(new VictoryConditions());

		SaveGame.ConvertVictoryConditions(game);

		Assert.Equal(
			[typeof(ScoreVictory), typeof(TimeLimitVictory)],
			game.victories.Select(v => v.GetType()));
	}

	[Fact]
	public void ShippedRuleset_EnablesTheFiveStandardConditionsWithCiv3Parameters() {
		SaveGame ruleset = GameMode.Load(PathUtils.GameModesDir, new GameMode.Config("civ3")).GetSave();
		VictoryConditions conditions = ruleset.VictoryConditions;

		Assert.True(conditions.AllowDominationVictory);
		Assert.True(conditions.AllowSpaceRaceVictory);
		Assert.True(conditions.AllowDiplomaticVictory);
		Assert.True(conditions.AllowConquestVictory);
		Assert.True(conditions.AllowCulturalVictory);
		Assert.False(conditions.AllowWonderVictory);

		Assert.Equal(66, conditions.DominationTerrain);
		Assert.Equal(66, conditions.DominationPopulation);
		Assert.Equal(20000, conditions.OneCityCultureWin);
		Assert.Equal(100000, conditions.AllCitiesCultureWin);
		Assert.Equal(50000, conditions.VictoryPointLimit);
		Assert.Equal(10, conditions.WonderCost);
		Assert.Equal(5, conditions.DefeatingOpposingUnitCost);
		Assert.Equal(5, conditions.AdvancementCost);
		Assert.Equal(100, conditions.CityConquestPopulation);
		Assert.Equal(25, conditions.VictoryPointScoring);
		Assert.Equal(1000, conditions.CapturingSpecialUnit);
		Assert.Equal(Enumerable.Repeat(1, 10), conditions.SpaceshipPartsNeeded);
	}
}
