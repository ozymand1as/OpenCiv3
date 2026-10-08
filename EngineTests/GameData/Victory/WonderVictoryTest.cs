using System.Collections.Generic;
using System.Linq;
using C7Engine;
using C7GameData;
using C7GameData.Save;
using Xunit;

namespace EngineTests.GameData.Victory;

public class WonderVictoryTest {

	public WonderVictoryTest() {
		// Engine message queue is static; parallelization is disabled repo-wide (XunitSettings.cs)
		EngineStorage.messagesToUI.Clear();
	}

	private const int TurnLimit = 540;

	/// A game with the given civilizations (and their score averages), the civil
	/// condition flags, and the great-wonder bookkeeping the condition reads.
	private static C7GameData.GameData MakeGame(VictoryConditions conditions, params (Player player, int score)[] entries) {
		C7GameData.GameData game = VictoryTestHelpers.MakeGame(turn: 1);
		game.timeOptions = new TimeOptions { turnLimit = TurnLimit };
		game.victoryConditions = conditions;
		foreach ((Player player, int score) in entries) {
			game.players.Add(player);
			VictoryTestHelpers.AddHistory(game, player, score);
		}
		return game;
	}

	private static void AddGreatWonder(C7GameData.GameData game, string name) {
		game.Buildings.Add(new Building(new SaveBuilding { name = name, greatWonderProperties = new() }, game));
	}

	private static void AddSmallWonder(C7GameData.GameData game, string name) {
		game.Buildings.Add(new Building(new SaveBuilding { name = name, isSmallWonder = true }, game));
	}

	private static List<MsgVictory> VictoryMessages() =>
		EngineStorage.messagesToUI.OfType<MsgVictory>().ToList();

	// ---------------------------------------------------------------- the predicate

	[Fact]
	public void HasVictory_WhenEveryGreatWonderIsBuilt_IsSatisfied() {
		Player rome = VictoryTestHelpers.MakePlayer("player-2", "Rome");
		C7GameData.GameData game = MakeGame(new VictoryConditions(), (rome, 10));
		AddGreatWonder(game, "The Pyramids");
		AddGreatWonder(game, "The Great Library");
		AddSmallWonder(game, "Apollo Program");
		game.GreatWondersBuilt.Add("The Pyramids");
		game.GreatWondersBuilt.Add("The Great Library");
		// A Small Wonder is not part of the predicate and must not be required.
		game.GreatWondersBuilt.Add("Apollo Program");

		var victory = new WonderVictory();
		VictoryStatus status = victory.Evaluate(rome, game);

		Assert.Equal(2, status.GreatWondersTotal);
		Assert.Equal(2, status.GreatWondersBuilt);
		Assert.True(victory.HasVictory(status));
	}

	[Fact]
	public void HasVictory_WithOneGreatWonderUnbuilt_IsNotSatisfied() {
		Player rome = VictoryTestHelpers.MakePlayer("player-2", "Rome");
		C7GameData.GameData game = MakeGame(new VictoryConditions(), (rome, 10));
		AddGreatWonder(game, "The Pyramids");
		AddGreatWonder(game, "The Great Library");
		game.GreatWondersBuilt.Add("The Pyramids");
		game.GreatWondersBuilt.Add("Apollo Program"); // a small wonder, not a substitute

		var victory = new WonderVictory();
		VictoryStatus status = victory.Evaluate(rome, game);

		Assert.Equal(2, status.GreatWondersTotal);
		Assert.Equal(1, status.GreatWondersBuilt);
		Assert.False(victory.HasVictory(status));
	}

	[Fact]
	public void Evaluate_CountsAWonderBuiltByAnyCivilization() {
		Player rome = VictoryTestHelpers.MakePlayer("player-2", "Rome");
		Player greece = VictoryTestHelpers.MakePlayer("player-3", "Greece");
		C7GameData.GameData game = MakeGame(new VictoryConditions(), (rome, 10), (greece, 10));
		AddGreatWonder(game, "The Pyramids");
		game.GreatWondersBuilt.Add("The Pyramids");

		// The condition is player-independent: Rome built it, and Greece still
		// satisfies the predicate.
		Assert.True(new WonderVictory().HasVictory(new WonderVictory().Evaluate(greece, game)));
	}

	[Fact]
	public void HasVictory_WithNoGreatWondersInTheRules_IsSatisfied() {
		// Section 2.7's predicate is "all Great Wonders have been built" and the
		// original walks the rules' improvement list without requiring it to be
		// non-empty, so a ruleset that defines no Great Wonder satisfies the
		// condition. The shipped rules always define 29, and the shipped BIQ
		// leaves the gate clear, so this only bites a scenario that turns the
		// gate on and defines no Great Wonder.
		Player rome = VictoryTestHelpers.MakePlayer("player-2", "Rome");
		C7GameData.GameData game = MakeGame(new VictoryConditions(), (rome, 10));
		AddSmallWonder(game, "Apollo Program");

		var victory = new WonderVictory();
		VictoryStatus status = victory.Evaluate(rome, game);

		Assert.Equal(0, status.GreatWondersTotal);
		Assert.True(victory.HasVictory(status));
	}

	// ---------------------------------------------------------------- the tie-break (section 5.4)

	[Fact]
	public void ChooseWinner_UnalliedCandidates_AwardsTheHighestScoreAverage() {
		Player rome = VictoryTestHelpers.MakePlayer("player-2", "Rome");
		Player greece = VictoryTestHelpers.MakePlayer("player-3", "Greece");
		// The higher score is on the second candidate, so the default
		// first-in-scan winner would be wrong.
		C7GameData.GameData game = MakeGame(new VictoryConditions(), (rome, 10), (greece, 30));

		Player winner = new WonderVictory().ChooseWinner([rome, greece], game);

		Assert.Same(greece, winner);
	}

	[Fact]
	public void ChooseWinner_UnalliedCandidatesWithTheSameScore_KeepsTheFirstInScanOrder() {
		Player rome = VictoryTestHelpers.MakePlayer("player-2", "Rome");
		Player greece = VictoryTestHelpers.MakePlayer("player-3", "Greece");
		greece.isHuman = true;
		C7GameData.GameData game = MakeGame(new VictoryConditions(), (rome, 25), (greece, 25));

		// The human preference is the alliance branch's rule, not a blanket one.
		Player winner = new WonderVictory().ChooseWinner([rome, greece], game);

		Assert.Same(rome, winner);
	}

	[Fact]
	public void ChooseWinner_WithVictoryPointScoringEnabled_StillUsesTheScoreWhileVictoryPointsAreUnmodelled() {
		Player rome = VictoryTestHelpers.MakePlayer("player-2", "Rome");
		Player greece = VictoryTestHelpers.MakePlayer("player-3", "Greece");
		// With the 0x2000 gate set the primary metric is the victory-point total,
		// which is zero for everyone, so section 5.4's "tie broken by the other
		// metric" makes the score decide - again for the second candidate.
		C7GameData.GameData game = MakeGame(new VictoryConditions { VictoryLocations = true }, (rome, 10), (greece, 30));

		Player winner = new WonderVictory().ChooseWinner([rome, greece], game);

		Assert.Same(greece, winner);
	}

	[Fact]
	public void ChooseWinner_AnAlliancePoolsItsMembersScoresAndBeatsAHigherScoringRival() {
		Player rome = VictoryTestHelpers.MakePlayer("player-2", "Rome");
		Player egypt = VictoryTestHelpers.MakePlayer("player-4", "Egypt");
		Player greece = VictoryTestHelpers.MakePlayer("player-3", "Greece");
		Alliance alliance = new Alliance(1, "Team");
		rome.alliance = alliance;
		egypt.alliance = alliance;
		egypt.isHuman = true;
		C7GameData.GameData game = MakeGame(new VictoryConditions(), (rome, 10), (egypt, 10), (greece, 15));

		// Greece has the best score of any one civilization, but the alliance
		// pools 20 and wins; its human member represents it.
		Player winner = new WonderVictory().ChooseWinner([rome, egypt, greece], game);

		Assert.Same(egypt, winner);
	}

	[Fact]
	public void ChooseWinner_AnAllianceWithoutAHuman_UsesItsFirstMember() {
		Player rome = VictoryTestHelpers.MakePlayer("player-2", "Rome");
		Player egypt = VictoryTestHelpers.MakePlayer("player-4", "Egypt");
		Player greece = VictoryTestHelpers.MakePlayer("player-3", "Greece");
		Alliance alliance = new Alliance(1, "Team");
		rome.alliance = alliance;
		egypt.alliance = alliance;
		C7GameData.GameData game = MakeGame(new VictoryConditions(), (rome, 10), (egypt, 10), (greece, 15));

		// The winning alliance has no human member, so its first member wins -
		// even though the scan starts with Greece, who is not in the alliance at
		// all and has the best score of any one civilization.
		Player winner = new WonderVictory().ChooseWinner([greece, rome, egypt], game);

		Assert.Same(rome, winner);
	}

	// ---------------------------------------------------------------- the flag gate

	[Fact]
	public void CheckVictory_WithTheWonderFlagClear_DoesNotEndTheGame() {
		Player rome = VictoryTestHelpers.MakePlayer("player-2", "Rome");
		C7GameData.GameData game = MakeGame(new VictoryConditions(), (rome, 10));
		AddGreatWonder(game, "The Pyramids");
		game.GreatWondersBuilt.Add("The Pyramids");
		// Register exactly what the rules allow: nothing, since every flag is
		// clear.
		SaveGame.ConvertVictoryConditions(game);

		TurnHandling.CheckVictory(game);

		Assert.Null(game.winner);
		Assert.False(game.gameOver);
		Assert.Empty(VictoryMessages());
	}

	[Fact]
	public void CheckVictory_WithTheWonderFlagSet_EndsTheGameOnceEveryGreatWonderIsBuilt() {
		Player rome = VictoryTestHelpers.MakePlayer("player-2", "Rome");
		C7GameData.GameData game = MakeGame(new VictoryConditions { AllowWonderVictory = true }, (rome, 10));
		AddGreatWonder(game, "The Pyramids");
		game.GreatWondersBuilt.Add("The Pyramids");
		SaveGame.ConvertVictoryConditions(game);

		TurnHandling.CheckVictory(game);

		Assert.Same(rome, game.winner);
		Assert.True(game.gameOver);
		var msg = Assert.Single(VictoryMessages());
		Assert.IsType<WonderVictory>(msg.victory);
	}

	[Fact]
	public void CheckVictory_WithTheWonderFlagSet_LeavesTheGameRunningWhileAWonderIsUnbuilt() {
		Player rome = VictoryTestHelpers.MakePlayer("player-2", "Rome");
		C7GameData.GameData game = MakeGame(new VictoryConditions { AllowWonderVictory = true }, (rome, 10));
		AddGreatWonder(game, "The Pyramids");
		AddGreatWonder(game, "The Great Library");
		game.GreatWondersBuilt.Add("The Pyramids");
		SaveGame.ConvertVictoryConditions(game);

		TurnHandling.CheckVictory(game);

		Assert.Null(game.winner);
		Assert.False(game.gameOver);
	}

	// ---------------------------------------------------------------- the fixed order (section 2.0)

	[Fact]
	public void CheckVictory_AllWondersBuilt_BeatsDominationInTheSameTurn() {
		Player rome = VictoryTestHelpers.MakePlayer("player-2", "Rome");
		Player greece = VictoryTestHelpers.MakePlayer("player-3", "Greece");
		C7GameData.GameData game = MakeGame(
			new VictoryConditions { AllowWonderVictory = true, AllowDominationVictory = true },
			(rome, 10), (greece, 10));

		// Rome owns every counted tile and all of the world's population, so it
		// satisfies domination; every great wonder being built satisfies the
		// wonder condition for both civilizations. Wonder victory is the earlier
		// condition in the original's order, so it is the one awarded.
		City city = VictoryTestHelpers.AddCity(game, rome, 2);
		VictoryTestHelpers.AddOwnedTile(game, VictoryTestHelpers.Land(), city);
		VictoryTestHelpers.AddOwnedTile(game, VictoryTestHelpers.Land(), city);
		AddGreatWonder(game, "The Pyramids");
		game.GreatWondersBuilt.Add("The Pyramids");
		SaveGame.ConvertVictoryConditions(game);

		TurnHandling.CheckVictory(game);

		var msg = Assert.Single(VictoryMessages());
		Assert.IsType<WonderVictory>(msg.victory);
		Assert.Same(rome, game.winner);
	}

	[Fact]
	public void CheckVictory_AllWondersBuilt_LosesToSpaceRaceInTheSameTurn() {
		Player rome = VictoryTestHelpers.MakePlayer("player-2", "Rome");
		Player greece = VictoryTestHelpers.MakePlayer("player-3", "Greece");
		C7GameData.GameData game = MakeGame(
			new VictoryConditions { AllowWonderVictory = true, AllowSpaceRaceVictory = true },
			(rome, 10), (greece, 30));

		// Both civilizations satisfy the wonder condition, and Rome also has
		// every spaceship part. Space race comes first in the original's order,
		// so it is awarded - which also decides the winner against the wonder
		// tie-break's higher-scoring Greece.
		rome.spaceshipPartsBuilt = Enumerable.Repeat(1, 10).ToList();
		AddGreatWonder(game, "The Pyramids");
		game.GreatWondersBuilt.Add("The Pyramids");
		SaveGame.ConvertVictoryConditions(game);

		TurnHandling.CheckVictory(game);

		var msg = Assert.Single(VictoryMessages());
		Assert.IsType<SpaceRaceVictory>(msg.victory);
		Assert.Same(rome, game.winner);
	}

	[Fact]
	public void CheckVictory_AllWondersBuilt_TwoSimultaneousWinners_AwardsTheHigherScoreAverage() {
		Player rome = VictoryTestHelpers.MakePlayer("player-2", "Rome");
		Player greece = VictoryTestHelpers.MakePlayer("player-3", "Greece");
		C7GameData.GameData game = MakeGame(new VictoryConditions { AllowWonderVictory = true }, (rome, 10), (greece, 30));
		AddGreatWonder(game, "The Pyramids");
		game.GreatWondersBuilt.Add("The Pyramids");
		SaveGame.ConvertVictoryConditions(game);

		TurnHandling.CheckVictory(game);

		Assert.Same(greece, game.winner);
		var msg = Assert.Single(VictoryMessages());
		Assert.IsType<WonderVictory>(msg.victory);
	}
}
