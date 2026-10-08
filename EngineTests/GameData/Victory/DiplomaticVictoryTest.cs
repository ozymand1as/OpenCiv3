using C7Engine;
using C7GameData;
using Xunit;

namespace EngineTests.GameData.Victory;

public class DiplomaticVictoryTest {

	[Theory]
	// A strict majority of the survivors: alliance_size > survivors / 2, with
	// truncating integer division.
	[InlineData(2, 3, true)]
	[InlineData(1, 3, false)]
	[InlineData(2, 4, false)]
	[InlineData(3, 4, true)]
	[InlineData(1, 1, true)]
	[InlineData(1, 2, false)]
	public void HasVictory_RequiresAStrictMajorityOfSurvivors(int allianceSize, int survivors, bool expected) {
		var victory = new DiplomaticVictory();
		var status = new VictoryStatus { AllianceSize = allianceSize, SurvivingCivs = survivors };

		Assert.Equal(expected, victory.HasVictory(status));
	}

	private static Player AddPlayer(C7GameData.GameData game, string id, string civ, Alliance alliance = null) {
		Player player = VictoryTestHelpers.MakePlayer(id, civ);
		player.alliance = alliance;
		game.players.Add(player);
		return player;
	}

	[Fact]
	public void Evaluate_CountsEverySurvivorSharingTheAlliance() {
		var game = VictoryTestHelpers.MakeGame();
		Alliance alliance = new Alliance(1, "Team");
		Player rome = AddPlayer(game, "player-2", "Rome", alliance);
		AddPlayer(game, "player-3", "Greece", alliance);
		AddPlayer(game, "player-4", "Egypt");

		var victory = new DiplomaticVictory();
		VictoryStatus status = victory.Evaluate(rome, game);

		Assert.Equal(3, status.SurvivingCivs);
		Assert.Equal(2, status.AllianceSize);
		Assert.True(victory.HasVictory(status));
	}

	[Fact]
	public void Evaluate_UnalignedPlayersDoNotShareAnAlliance() {
		var game = VictoryTestHelpers.MakeGame();
		Player rome = AddPlayer(game, "player-2", "Rome");
		AddPlayer(game, "player-3", "Greece");
		AddPlayer(game, "player-4", "Egypt");

		var victory = new DiplomaticVictory();
		VictoryStatus status = victory.Evaluate(rome, game);

		Assert.Equal(1, status.AllianceSize);
		Assert.False(victory.HasVictory(status));
	}

	[Fact]
	public void Evaluate_DefeatedAlliesDoNotCount() {
		var game = VictoryTestHelpers.MakeGame();
		Alliance alliance = new Alliance(1, "Team");
		Player rome = AddPlayer(game, "player-2", "Rome", alliance);
		AddPlayer(game, "player-3", "Greece", alliance).defeated = true;
		AddPlayer(game, "player-4", "Egypt");
		AddPlayer(game, "player-5", "Babylon", alliance);
		AddPlayer(game, "player-6", "Persia");

		var victory = new DiplomaticVictory();
		VictoryStatus status = victory.Evaluate(rome, game);

		// Four survivors: Rome, Babylon, Egypt and Persia. The alliance holds
		// two of them, which is not a strict majority; the defeated Greek ally
		// would make it three of five if it were counted.
		Assert.Equal(4, status.SurvivingCivs);
		Assert.Equal(2, status.AllianceSize);
		Assert.False(victory.HasVictory(status));
	}
}
