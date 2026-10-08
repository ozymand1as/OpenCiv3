using C7Engine;
using C7GameData;
using Xunit;

namespace EngineTests.GameData.Victory;

public class DominationVictoryTest {

	/// Builds a world of 100 counted tiles and 100 population split between two
	/// civilizations, so the 66% thresholds are easy to reason about.
	private static (C7GameData.GameData game, Player player, Player rival) MakeWorld(
		int playerTiles, int rivalTiles, int playerPopulation, int rivalPopulation) {
		var game = VictoryTestHelpers.MakeGame();
		Player player = VictoryTestHelpers.MakePlayer("player-2", "Rome");
		Player rival = VictoryTestHelpers.MakePlayer("player-3", "Greece");
		game.players.Add(player);
		game.players.Add(rival);

		City playerCity = VictoryTestHelpers.AddCity(game, player, playerPopulation);
		City rivalCity = VictoryTestHelpers.AddCity(game, rival, rivalPopulation);

		for (int i = 0; i < playerTiles; ++i) {
			VictoryTestHelpers.AddOwnedTile(game, VictoryTestHelpers.Land(), playerCity);
		}
		for (int i = 0; i < rivalTiles; ++i) {
			VictoryTestHelpers.AddOwnedTile(game, VictoryTestHelpers.Land(), rivalCity);
		}

		return (game, player, rival);
	}

	[Theory]
	[InlineData(67, 67, true)]
	[InlineData(66, 67, false)] // exactly 66% of the tiles is not "more than 66%"
	[InlineData(67, 66, false)] // exactly 66% of the population is not enough either
	[InlineData(33, 99, false)]
	public void HasVictory_RequiresStrictlyMoreThanBothPercentages(int playerTiles, int playerPopulation, bool expected) {
		var (game, player, _) = MakeWorld(playerTiles, 100 - playerTiles, playerPopulation, 100 - playerPopulation);
		var victory = new DominationVictory(66, 66);

		VictoryStatus status = victory.Evaluate(player, game);

		Assert.Equal(expected, victory.HasVictory(status));
	}

	[Fact]
	public void Evaluate_CountsCoastAsLandButExcludesSea() {
		var game = VictoryTestHelpers.MakeGame();
		Player player = VictoryTestHelpers.MakePlayer("player-2", "Rome");
		game.players.Add(player);
		City city = VictoryTestHelpers.AddCity(game, player, 1);

		for (int i = 0; i < 66; ++i) {
			VictoryTestHelpers.AddOwnedTile(game, VictoryTestHelpers.Land(), city);
		}
		VictoryTestHelpers.AddOwnedTile(game, VictoryTestHelpers.Coast(), city);
		for (int i = 0; i < 100; ++i) {
			VictoryTestHelpers.AddUnownedTile(game, VictoryTestHelpers.Sea());
		}

		VictoryStatus status = new DominationVictory(66, 66).Evaluate(player, game);

		Assert.Equal(67, status.TotalTiles);
		Assert.Equal(67, status.OwnedTiles);
	}

	[Fact]
	public void Evaluate_WithAnAlliance_SumsTheMembersTilesAndPopulation() {
		var game = VictoryTestHelpers.MakeGame();
		Player rome = VictoryTestHelpers.MakePlayer("player-2", "Rome");
		Player greece = VictoryTestHelpers.MakePlayer("player-3", "Greece");
		Player egypt = VictoryTestHelpers.MakePlayer("player-4", "Egypt");
		Alliance alliance = new Alliance(1, "Team");
		rome.alliance = alliance;
		greece.alliance = alliance;
		game.players.AddRange([rome, greece, egypt]);

		City romeCity = VictoryTestHelpers.AddCity(game, rome, 33);
		City greeceCity = VictoryTestHelpers.AddCity(game, greece, 34);
		City egyptCity = VictoryTestHelpers.AddCity(game, egypt, 33);

		foreach (City city in new[] { romeCity, greeceCity, egyptCity }) {
			for (int i = 0; i < 100; ++i) {
				VictoryTestHelpers.AddOwnedTile(game, VictoryTestHelpers.Land(), city);
			}
		}

		// Rome alone owns 100 of 300 tiles and 33% of the population, but the
		// alliance owns 200 tiles (more than 66% of 300 => 198) and 67% of the
		// population.
		VictoryStatus romeStatus = new DominationVictory(66, 66).Evaluate(rome, game);
		VictoryStatus greeceStatus = new DominationVictory(66, 66).Evaluate(greece, game);
		VictoryStatus egyptStatus = new DominationVictory(66, 66).Evaluate(egypt, game);

		Assert.Equal(200, romeStatus.OwnedTiles);
		Assert.Equal(67, romeStatus.OwnedPopulation);
		Assert.True(new DominationVictory(66, 66).HasVictory(romeStatus));
		Assert.True(new DominationVictory(66, 66).HasVictory(greeceStatus));
		Assert.False(new DominationVictory(66, 66).HasVictory(egyptStatus));
	}

	[Fact]
	public void HasVictory_NoTilesOnTheMap_DoesNotAwardVictory() {
		var game = VictoryTestHelpers.MakeGame();
		Player player = VictoryTestHelpers.MakePlayer("player-2", "Rome");
		game.players.Add(player);
		VictoryTestHelpers.AddCity(game, player, 5);

		var victory = new DominationVictory(66, 66);
		VictoryStatus status = victory.Evaluate(player, game);

		Assert.False(victory.HasVictory(status));
	}
}
