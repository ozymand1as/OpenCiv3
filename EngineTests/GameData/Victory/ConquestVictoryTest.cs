using C7Engine;
using C7GameData;
using Xunit;

namespace EngineTests.GameData.Victory;

public class ConquestVictoryTest {

	private static Player AddPlayer(C7GameData.GameData game, string id, string civ, Alliance alliance = null, bool defeated = false) {
		Player player = VictoryTestHelpers.MakePlayer(id, civ, defeated: defeated);
		player.alliance = alliance;
		game.players.Add(player);
		return player;
	}

	[Fact]
	public void HasVictory_OneSurvivorWins() {
		var game = VictoryTestHelpers.MakeGame();
		Player rome = AddPlayer(game, "player-2", "Rome");
		AddPlayer(game, "player-3", "Greece", defeated: true);
		AddPlayer(game, "player-4", "Egypt", defeated: true);

		var victory = new ConquestVictory();
		VictoryStatus status = victory.Evaluate(rome, game);

		Assert.Equal(1, status.SurvivingCivs);
		Assert.True(victory.HasVictory(status));
	}

	[Fact]
	public void HasVictory_EverySurvivorInOneAllianceWins() {
		var game = VictoryTestHelpers.MakeGame();
		Alliance alliance = new Alliance(1, "Team");
		Player rome = AddPlayer(game, "player-2", "Rome", alliance);
		AddPlayer(game, "player-3", "Greece", alliance);
		AddPlayer(game, "player-4", "Egypt", alliance);

		var victory = new ConquestVictory();
		VictoryStatus status = victory.Evaluate(rome, game);

		Assert.Equal(3, status.SurvivingCivs);
		Assert.Equal(3, status.AllianceSize);
		Assert.True(victory.HasVictory(status));
	}

	[Fact]
	public void HasVictory_OnlyPartOfTheSurvivorsAllied_DoesNotWin() {
		var game = VictoryTestHelpers.MakeGame();
		Alliance alliance = new Alliance(1, "Team");
		Player rome = AddPlayer(game, "player-2", "Rome", alliance);
		AddPlayer(game, "player-3", "Greece", alliance);
		AddPlayer(game, "player-4", "Egypt");

		var victory = new ConquestVictory();
		VictoryStatus status = victory.Evaluate(rome, game);

		Assert.Equal(3, status.SurvivingCivs);
		Assert.Equal(2, status.AllianceSize);
		Assert.False(victory.HasVictory(status));
	}

	[Fact]
	public void HasVictory_TwoUnaffiliatedSurvivors_DoesNotWin() {
		var game = VictoryTestHelpers.MakeGame();
		Player rome = AddPlayer(game, "player-2", "Rome");
		AddPlayer(game, "player-3", "Greece");

		var victory = new ConquestVictory();
		VictoryStatus status = victory.Evaluate(rome, game);

		Assert.Equal(2, status.SurvivingCivs);
		Assert.Equal(1, status.AllianceSize);
		Assert.False(victory.HasVictory(status));
	}

	[Fact]
	public void HasVictory_BarbariansAreNotSurvivors() {
		var game = VictoryTestHelpers.MakeGame();
		game.players.Add(VictoryTestHelpers.MakePlayer("player-0", "Barbarians", barbarian: true));
		Player rome = AddPlayer(game, "player-2", "Rome");

		var victory = new ConquestVictory();
		VictoryStatus status = victory.Evaluate(rome, game);

		Assert.Equal(1, status.SurvivingCivs);
		Assert.True(victory.HasVictory(status));
	}
}
