using System.Collections.Generic;
using System.Linq;
using C7Engine;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using Xunit;

namespace EngineTests.GameData.Victory;

public class CheckVictoryTest : IClassFixture<SaveGameFixture> {

	private readonly SaveGameFixture fixture;

	public CheckVictoryTest(SaveGameFixture fixture) {
		this.fixture = fixture;
		// Engine message queue is static; parallelization is disabled repo-wide (XunitSettings.cs)
		EngineStorage.messagesToUI.Clear();
	}

	private const int TurnLimit = 540;

	private static Player MakePlayer(string id, string civ, bool barbarian = false, bool defeated = false) {
		return new Player {
			id = ID.FromString(id),
			civilization = new Civilization(civ) { isBarbarian = barbarian },
			defeated = defeated,
		};
	}

	/// Builds a minimal GameData with score + time-limit victories registered (as SaveGame.ConvertVictoryConditions does),
	/// and a single history record per non-barbarian player carrying the given score.
	private static C7GameData.GameData MakeGame(int turn, params (Player player, int score)[] entries) {
		var difficulty = new Difficulty();
		var gd = new C7GameData.GameData {
			turn = turn,
			difficulties = new List<Difficulty> { difficulty },
			gameDifficulty = difficulty,
			history = new Dictionary<string, List<HistTurnRecord>>(),
		};
		foreach (var (player, score) in entries) {
			gd.players.Add(player);
			if (!player.isBarbarians)
				gd.history[player.id.ToString()] = new List<HistTurnRecord> { new HistTurnRecord { Score = score } };
		}
		gd.victories.Add(new ScoreVictory());
		gd.victories.Add(new TimeLimitVictory(TurnLimit));
		return gd;
	}

	private static List<MsgVictory> VictoryMessages() =>
		EngineStorage.messagesToUI.OfType<MsgVictory>().ToList();

	// ---------------------------------------------------------------- turn-limit boundary

	[Theory]
	[InlineData(TurnLimit - 1, false)]
	[InlineData(TurnLimit, true)]
	[InlineData(TurnLimit + 1, true)]
	public void CheckVictory_OnlyEndsGameOnceTurnLimitIsReached(int turn, bool expectGameOver) {
		var rome = MakePlayer("player-2", "Rome");
		var greece = MakePlayer("player-3", "Greece");
		var gd = MakeGame(turn, (rome, 10), (greece, 20));

		TurnHandling.CheckVictory(gd);

		Assert.Equal(expectGameOver, gd.winner != null);
		Assert.Equal(expectGameOver, gd.gameOver);
		Assert.Equal(expectGameOver ? 1 : 0, VictoryMessages().Count);
	}

	// ---------------------------------------------------------------- winner selection

	[Fact]
	public void CheckVictory_AtLimit_HighestScoreWins_AndExactlyOneMessageIsSent() {
		var rome = MakePlayer("player-2", "Rome");
		var greece = MakePlayer("player-3", "Greece");
		var egypt = MakePlayer("player-4", "Egypt");
		var gd = MakeGame(TurnLimit, (rome, 10), (greece, 30), (egypt, 20));

		TurnHandling.CheckVictory(gd);

		Assert.Same(greece, gd.winner);
		Assert.True(gd.gameOver);

		var msgs = VictoryMessages();
		Assert.Single(msgs);
		Assert.Same(greece, msgs[0].winner);
		Assert.True(gd.gameOver);
	}

	[Fact]
	public void CheckVictory_AtLimit_TiedScores_FirstPlayerInListWins() {
		// Documents current tie-break behaviour (stable OrderByDescending => list order).
		// If ties should be broken some other way (human first? most cities?), change this test.
		var rome = MakePlayer("player-2", "Rome");
		var greece = MakePlayer("player-3", "Greece");
		var gd = MakeGame(TurnLimit, (rome, 25), (greece, 25));

		TurnHandling.CheckVictory(gd);

		Assert.Same(rome, gd.winner);
	}

	[Fact]
	public void CheckVictory_AtLimit_BarbariansAreNeverWinnersAndDoNotNeedHistory() {
		var barbs = MakePlayer("player-0", "Barbarians", barbarian: true);
		var rome = MakePlayer("player-2", "Rome");
		var gd = MakeGame(TurnLimit, (barbs, 999), (rome, 1));

		TurnHandling.CheckVictory(gd);

		Assert.Same(rome, gd.winner);
	}

	[Fact]
	public void CheckVictory_AtLimit_DefeatedPlayerWithTopScoreDoesNotWin() {
		// EXPECTED-TO-FAIL on caf614a: CheckVictory never looks at Player.defeated, and TimeLimitVictory
		// reports "victory" for every player, so a defeated civ with the best (frozen) score can be declared winner.
		var dead = MakePlayer("player-4", "Carthage", defeated: true);
		var rome = MakePlayer("player-2", "Rome");
		var gd = MakeGame(TurnLimit, (dead, 50), (rome, 10));

		TurnHandling.CheckVictory(gd);

		Assert.Same(rome, gd.winner);
	}

	// ---------------------------------------------------------------- game-over state

	[Fact]
	public void CheckVictory_AfterGameOver_DoesNotDeclareOrSendAgain() {
		var rome = MakePlayer("player-2", "Rome");
		var greece = MakePlayer("player-3", "Greece");
		var gd = MakeGame(TurnLimit, (rome, 10), (greece, 20));

		TurnHandling.CheckVictory(gd);
		Player firstWinner = gd.winner;

		// "Continue playing" for a few turns; scores would now favour Rome if anything re-evaluated
		gd.turn += 3;
		gd.history[rome.id.ToString()].Add(new HistTurnRecord { Score = 500 });
		TurnHandling.CheckVictory(gd);

		Assert.Same(firstWinner, gd.winner);
		Assert.Single(VictoryMessages());
	}

	[Fact]
	public void UpdateHistory_AfterGameOver_DoesNotAddRecords() {
		// The popup promises "No further score will be entered".
		var rome = MakePlayer("player-2", "Rome");
		var gd = MakeGame(TurnLimit, (rome, 10));
		gd.winner = rome;
		gd.gameOver = true;

		rome.UpdateHistory(gd);

		Assert.Single(gd.history[rome.id.ToString()]);
	}

	// ---------------------------------------------------------------- persistence

	[Fact]
	public void GameOverState_SurvivesSaveAndLoad() {
		C7GameData.GameData gd = SaveGameFixture.HydrateSaveGame(fixture.saveGame);
		gd.winner = gd.players.First(p => !p.isBarbarians);
		gd.gameOver = true;

		SaveGame save = SaveGame.FromGameData(gd);
		Assert.NotNull(save.Winner);
		Assert.True(save.GameOver);

		C7GameData.GameData reloaded = save.ToGameData(fixture.behaviors);
		Assert.NotNull(reloaded.winner);
		Assert.True(reloaded.gameOver);
	}

	[Fact]
	public void LoadedGame_RegistersScoreAndTimeLimitVictories_UsingConfiguredTurnLimit() {
		C7GameData.GameData gd = SaveGameFixture.HydrateSaveGame(fixture.saveGame);

		Assert.Contains(gd.victories, v => v is ScoreVictory);
		Assert.Contains(gd.victories, v => v is TimeLimitVictory);

		// Not yet ending at turn 0 with the default 540-turn limit
		var status = gd.victories.OfType<TimeLimitVictory>().First().Evaluate(gd.players.First(), gd);
		Assert.False(gd.victories.OfType<TimeLimitVictory>().First().HasVictory(status));
	}

	// ---------------------------------------------------------------- condition ordering and gating

	private static C7GameData.GameData MakeRegisteredGame(int turn, VictoryConditions conditions) {
		var difficulty = new Difficulty();
		return new C7GameData.GameData {
			turn = turn,
			difficulties = new List<Difficulty> { difficulty },
			gameDifficulty = difficulty,
			history = new Dictionary<string, List<HistTurnRecord>>(),
			victoryConditions = conditions,
			timeOptions = new TimeOptions { turnLimit = TurnLimit },
		};
	}

	/// Gives Rome a 20k-culture city and, when `domination` is set, control of
	/// 67 of the world's 100 counted tiles and 67 of its 101 citizens.
	private static void GiveRomeACulturalAndDominatingEmpire(C7GameData.GameData game, Player rome, Player greece, Player egypt, bool domination) {
		City romeCity = VictoryTestHelpers.AddCity(game, rome, domination ? 67 : 1, 20000);
		City greeceCity = VictoryTestHelpers.AddCity(game, greece, domination ? 33 : 1);
		VictoryTestHelpers.AddCity(game, egypt, 1);

		if (!domination) {
			return;
		}

		for (int i = 0; i < 67; ++i) {
			VictoryTestHelpers.AddOwnedTile(game, VictoryTestHelpers.Land(), romeCity);
		}
		for (int i = 0; i < 33; ++i) {
			VictoryTestHelpers.AddOwnedTile(game, VictoryTestHelpers.Land(), greeceCity);
		}
	}

	private static (C7GameData.GameData game, Player rome, Player greece, Player egypt) MakeContestedGame(
		VictoryConditions conditions, bool domination = true) {
		var game = MakeRegisteredGame(1, conditions);
		Player rome = MakePlayer("player-2", "Rome");
		Player greece = MakePlayer("player-3", "Greece");
		Player egypt = MakePlayer("player-4", "Egypt");
		game.players.AddRange([rome, greece, egypt]);
		VictoryTestHelpers.AddHistory(game, rome, 10);
		VictoryTestHelpers.AddHistory(game, greece, 20);
		VictoryTestHelpers.AddHistory(game, egypt, 30);
		GiveRomeACulturalAndDominatingEmpire(game, rome, greece, egypt, domination);
		SaveGame.ConvertVictoryConditions(game);
		return (game, rome, greece, egypt);
	}

	[Fact]
	public void CheckVictory_WhenDominationAndCulturalBothHold_AwardsDomination() {
		var (game, rome, _, _) = MakeContestedGame(new VictoryConditions {
			AllowDominationVictory = true,
			AllowCulturalVictory = true,
		});

		TurnHandling.CheckVictory(game);

		Assert.Same(rome, game.winner);
		Assert.IsType<DominationVictory>(VictoryMessages().Single().victory);
	}

	[Fact]
	public void CheckVictory_WhenConquestAndDominationBothHold_AwardsConquest() {
		var (game, rome, greece, egypt) = MakeContestedGame(new VictoryConditions {
			AllowConquestVictory = true,
			AllowDominationVictory = true,
		});
		greece.defeated = true;
		egypt.defeated = true;

		TurnHandling.CheckVictory(game);

		Assert.Same(rome, game.winner);
		Assert.IsType<ConquestVictory>(VictoryMessages().Single().victory);
	}

	[Fact]
	public void CheckVictory_DominationDisabled_CulturalIsAwardedInstead() {
		var (game, rome, _, _) = MakeContestedGame(new VictoryConditions { AllowCulturalVictory = true });

		TurnHandling.CheckVictory(game);

		Assert.Same(rome, game.winner);
		Assert.IsType<CulturalVictory>(VictoryMessages().Single().victory);
	}

	[Fact]
	public void CheckVictory_EveryConditionDisabled_DoesNotEndTheGame() {
		// Rome holds both a 20k city and a dominating empire, but the rules turn
		// every condition off.
		var (game, _, _, _) = MakeContestedGame(new VictoryConditions());

		TurnHandling.CheckVictory(game);

		Assert.Null(game.winner);
		Assert.False(game.gameOver);
		Assert.Empty(VictoryMessages());
	}

	// ---------------------------------------------------------------- victory points

	[Fact]
	public void CheckVictory_WhenVictoryPointsAndConquestBothHold_AwardsTheVictoryPoints() {
		// Section 2.0: type 8 is evaluated before conquest. Rome is the last
		// civilization standing and has also reached the victory-point limit, so
		// both conditions hold and only the first one is awarded.
		var game = MakeRegisteredGame(1, new VictoryConditions {
			VictoryLocations = true,
			VictoryPointLimit = 100,
			AllowConquestVictory = true,
		});
		Player rome = MakePlayer("player-2", "Rome");
		Player greece = MakePlayer("player-3", "Greece", defeated: true);
		game.players.AddRange([rome, greece]);
		VictoryTestHelpers.AddHistory(game, rome, 10);
		rome.victoryPoints = 100;
		SaveGame.ConvertVictoryConditions(game);

		TurnHandling.CheckVictory(game);

		Assert.Same(rome, game.winner);
		Assert.IsType<VictoryPointVictory>(VictoryMessages().Single().victory);
	}

	[Fact]
	public void CheckVictory_WithTheVictoryPointGroupOff_AwardsConquestInstead() {
		// The same state without the 0x26000 group: the type-8 condition is not
		// registered, so conquest is the first satisfied condition.
		var game = MakeRegisteredGame(1, new VictoryConditions {
			VictoryPointLimit = 100,
			AllowConquestVictory = true,
		});
		Player rome = MakePlayer("player-2", "Rome");
		Player greece = MakePlayer("player-3", "Greece", defeated: true);
		game.players.AddRange([rome, greece]);
		VictoryTestHelpers.AddHistory(game, rome, 10);
		rome.victoryPoints = 100;
		SaveGame.ConvertVictoryConditions(game);

		TurnHandling.CheckVictory(game);

		Assert.Same(rome, game.winner);
		Assert.IsType<ConquestVictory>(VictoryMessages().Single().victory);
	}

	[Fact]
	public void CheckVictory_VictoryPointsBelowTheLimit_DoNotEndTheGame() {
		var game = MakeRegisteredGame(1, new VictoryConditions {
			VictoryLocations = true,
			VictoryPointLimit = 100,
		});
		Player rome = MakePlayer("player-2", "Rome");
		Player greece = MakePlayer("player-3", "Greece");
		game.players.AddRange([rome, greece]);
		VictoryTestHelpers.AddHistory(game, rome, 10);
		VictoryTestHelpers.AddHistory(game, greece, 20);
		rome.victoryPoints = 99;
		SaveGame.ConvertVictoryConditions(game);

		TurnHandling.CheckVictory(game);

		Assert.Null(game.winner);
		Assert.False(game.gameOver);
	}

	[Fact]
	public void CheckVictory_AtLimit_WithVictoryPointsEnabled_HighestVictoryPointsBeatAHigherScore() {
		// Section 5.4: with the victory-point group on and a player at one or
		// more points, the turn-limit winner is the highest victory-point total,
		// not the highest score.
		var game = MakeRegisteredGame(TurnLimit, new VictoryConditions { VictoryLocations = true });
		Player rome = MakePlayer("player-2", "Rome");
		Player greece = MakePlayer("player-3", "Greece");
		game.players.AddRange([rome, greece]);
		VictoryTestHelpers.AddHistory(game, rome, 10);
		VictoryTestHelpers.AddHistory(game, greece, 90);
		rome.victoryPoints = 50;
		greece.victoryPoints = 10;
		SaveGame.ConvertVictoryConditions(game);

		TurnHandling.CheckVictory(game);

		Assert.Same(rome, game.winner);
	}

	[Fact]
	public void CheckVictory_AtLimit_WithVictoryPointsEnabled_TiesAreBrokenByScore() {
		var game = MakeRegisteredGame(TurnLimit, new VictoryConditions { VictoryLocations = true });
		Player rome = MakePlayer("player-2", "Rome");
		Player greece = MakePlayer("player-3", "Greece");
		game.players.AddRange([rome, greece]);
		VictoryTestHelpers.AddHistory(game, rome, 10);
		VictoryTestHelpers.AddHistory(game, greece, 90);
		rome.victoryPoints = 50;
		greece.victoryPoints = 50;
		SaveGame.ConvertVictoryConditions(game);

		TurnHandling.CheckVictory(game);

		Assert.Same(greece, game.winner);
	}

	[Fact]
	public void CheckVictory_AtLimit_WithVictoryPointsEnabledButNobodyHasAny_HighestScoreWins() {
		// Civ3 only switches to the victory-point metric when some player has at
		// least one point.
		var game = MakeRegisteredGame(TurnLimit, new VictoryConditions { VictoryLocations = true });
		Player rome = MakePlayer("player-2", "Rome");
		Player greece = MakePlayer("player-3", "Greece");
		game.players.AddRange([rome, greece]);
		VictoryTestHelpers.AddHistory(game, rome, 10);
		VictoryTestHelpers.AddHistory(game, greece, 90);
		SaveGame.ConvertVictoryConditions(game);

		TurnHandling.CheckVictory(game);

		Assert.Same(greece, game.winner);
	}

	// ---------------------------------------------------------------- condition-major awarding and ties

	[Fact]
	public void CheckVictory_DifferentConditionsSatisfiedByDifferentPlayers_AwardsTheHigherPriorityCondition() {
		// Greece is one city away from a cultural win; Rome and Egypt share an
		// alliance, so both satisfy the diplomatic condition. Rome has the
		// highest score, which is what the old player-major selection looked at,
		// but cultural comes before diplomatic and only the first satisfied
		// condition is awarded (spec section 2.0).
		var game = MakeRegisteredGame(1, new VictoryConditions {
			AllowCulturalVictory = true,
			AllowDiplomaticVictory = true,
		});
		Player rome = MakePlayer("player-2", "Rome");
		Player greece = MakePlayer("player-3", "Greece");
		Player egypt = MakePlayer("player-4", "Egypt");
		game.players.AddRange([rome, greece, egypt]);
		VictoryTestHelpers.AddHistory(game, rome, 90);
		VictoryTestHelpers.AddHistory(game, greece, 10);
		VictoryTestHelpers.AddHistory(game, egypt, 50);

		Alliance alliance = new Alliance(1, "Team");
		rome.alliance = alliance;
		egypt.alliance = alliance;

		VictoryTestHelpers.AddCity(game, rome, 1);
		VictoryTestHelpers.AddCity(game, egypt, 1);
		VictoryTestHelpers.AddCity(game, greece, 1, 20000);

		SaveGame.ConvertVictoryConditions(game);

		TurnHandling.CheckVictory(game);

		Assert.Same(greece, game.winner);
		Assert.IsType<CulturalVictory>(VictoryMessages().Single().victory);
	}

	[Fact]
	public void CheckVictory_WhenAnAllianceWinsByConquest_TheHumanMemberWinsRegardlessOfScore() {
		// Rome and Egypt are allied and Greece has been eliminated, so every
		// survivor satisfies conquest. Section 5.4 awards it to the human member
		// of the surviving alliance, not to the highest score.
		var game = MakeRegisteredGame(1, new VictoryConditions { AllowConquestVictory = true });
		Player rome = MakePlayer("player-2", "Rome");
		rome.isHuman = true;
		Player egypt = MakePlayer("player-4", "Egypt");
		Player greece = MakePlayer("player-3", "Greece", defeated: true);
		game.players.AddRange([rome, egypt, greece]);
		VictoryTestHelpers.AddHistory(game, rome, 10);
		VictoryTestHelpers.AddHistory(game, egypt, 90);

		Alliance alliance = new Alliance(1, "Team");
		rome.alliance = alliance;
		egypt.alliance = alliance;

		SaveGame.ConvertVictoryConditions(game);

		TurnHandling.CheckVictory(game);

		Assert.Same(rome, game.winner);
		Assert.IsType<ConquestVictory>(VictoryMessages().Single().victory);
	}
}
