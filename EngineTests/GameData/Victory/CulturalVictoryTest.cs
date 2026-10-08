using C7Engine;
using C7GameData;
using Xunit;

namespace EngineTests.GameData.Victory;

public class CulturalVictoryTest {

	/// Adds `total` culture to a civilization spread over several cities, each
	/// kept below the one-city threshold so only the civilization-wide check
	/// can fire.
	private static void AddCultureSpread(C7GameData.GameData game, Player player, int total) {
		int remaining = total;
		while (remaining > 0) {
			int chunk = System.Math.Min(10000, remaining);
			VictoryTestHelpers.AddCity(game, player, 1, chunk);
			remaining -= chunk;
		}
	}

	[Theory]
	[InlineData(20000, true)]
	[InlineData(19999, false)]
	[InlineData(25000, true)]
	public void HasVictory_OneCityAtTheThresholdWins(int cityCulture, bool expected) {
		var game = VictoryTestHelpers.MakeGame();
		game.map.numTilesWide = 100;
		game.map.numTilesTall = 100;
		Player rome = VictoryTestHelpers.MakePlayer("player-2", "Rome");
		Player greece = VictoryTestHelpers.MakePlayer("player-3", "Greece");
		game.players.AddRange([rome, greece]);
		VictoryTestHelpers.AddCity(game, rome, 2, cityCulture);
		VictoryTestHelpers.AddCity(game, greece, 2);

		var victory = new CulturalVictory(20000, 100000);
		VictoryStatus status = victory.Evaluate(rome, game);

		Assert.Equal(cityCulture, status.TopCityCulture);
		Assert.Equal(expected, victory.HasVictory(status));
	}

	[Theory]
	// The civilization culture total must reach the map-scaled requirement
	// (100000 on a standard 100x100 map) and be at least twice the best rival.
	[InlineData(100000, 50000, true)]
	[InlineData(100000, 50001, false)]
	[InlineData(99999, 0, false)]
	public void HasVictory_WholeCivilizationNeedsTheRequirementAndTwiceTheBestRival(int romeCulture, int greeceCulture, bool expected) {
		var game = VictoryTestHelpers.MakeGame();
		game.map.numTilesWide = 100;
		game.map.numTilesTall = 100;
		Player rome = VictoryTestHelpers.MakePlayer("player-2", "Rome");
		Player greece = VictoryTestHelpers.MakePlayer("player-3", "Greece");
		game.players.AddRange([rome, greece]);
		AddCultureSpread(game, rome, romeCulture);
		AddCultureSpread(game, greece, greeceCulture);

		var victory = new CulturalVictory(20000, 100000);
		VictoryStatus status = victory.Evaluate(rome, game);

		Assert.Equal(100000, status.CultureRequirement);
		Assert.Equal(romeCulture, status.CivilizationCulture);
		Assert.Equal(expected, victory.HasVictory(status));
	}

	[Fact]
	public void HasVictory_WholeCivilizationNoRivals_NeedsOnlyTheRequirement() {
		var game = VictoryTestHelpers.MakeGame();
		game.map.numTilesWide = 100;
		game.map.numTilesTall = 100;
		Player rome = VictoryTestHelpers.MakePlayer("player-2", "Rome");
		game.players.Add(rome);
		AddCultureSpread(game, rome, 100000);

		var victory = new CulturalVictory(20000, 100000);
		VictoryStatus status = victory.Evaluate(rome, game);

		Assert.True(victory.HasVictory(status));
	}

	[Theory]
	[InlineData(100, 100, 100000)]
	[InlineData(130, 130, 130000)]
	[InlineData(160, 160, 160000)]
	[InlineData(80, 80, 80000)]
	[InlineData(60, 60, 60000)]
	// Clamped to the range Civ3 uses.
	[InlineData(4, 4, 5000)]
	[InlineData(1000, 1000, 500000)]
	public void AllCitiesCultureRequirement_ScalesWithTheMapSize(int width, int height, int expected) {
		Assert.Equal(expected, CulturalVictory.AllCitiesCultureRequirement(100000, width, height));
	}

	[Fact]
	public void AllCitiesCultureRequirement_ZeroBaseUsesTheCiv3Default() {
		Assert.Equal(CulturalVictory.AllCitiesCultureRequirement(100000, 100, 100),
			CulturalVictory.AllCitiesCultureRequirement(0, 100, 100));
	}
}
