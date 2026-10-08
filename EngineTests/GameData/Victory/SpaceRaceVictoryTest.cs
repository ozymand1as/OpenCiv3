using System.Collections.Generic;
using C7Engine;
using C7GameData;
using Xunit;

namespace EngineTests.GameData.Victory;

public class SpaceRaceVictoryTest {

	[Theory]
	[InlineData(new[] { 1, 1 }, new[] { 1, 1 }, true)]
	[InlineData(new[] { 1, 1 }, new[] { 1, 0 }, false)]
	[InlineData(new[] { 1, 1 }, new[] { 0, 0 }, false)]
	// Part counts above the requirement still count as complete.
	[InlineData(new[] { 2, 1 }, new[] { 3, 1 }, true)]
	[InlineData(new[] { 2, 1 }, new[] { 1, 1 }, false)]
	public void HasVictory_RequiresEveryPart(int[] needed, int[] built, bool expected) {
		Player player = VictoryTestHelpers.MakePlayer("player-2", "Rome");
		player.spaceshipPartsBuilt = new List<int>(built);
		var victory = new SpaceRaceVictory(needed);

		VictoryStatus status = victory.Evaluate(player, VictoryTestHelpers.MakeGame());

		Assert.Equal(expected, victory.HasVictory(status));
	}

	[Fact]
	public void HasVictory_NoPartsRequired_DoesNotAwardVictory() {
		Player player = VictoryTestHelpers.MakePlayer("player-2", "Rome");
		var victory = new SpaceRaceVictory(new List<int>());

		VictoryStatus status = victory.Evaluate(player, VictoryTestHelpers.MakeGame());

		Assert.False(victory.HasVictory(status));
	}

	[Fact]
	public void Evaluate_CountsOnlyTheCompletedPartsAndIgnoresExtraEntries() {
		Player player = VictoryTestHelpers.MakePlayer("player-2", "Rome");
		player.spaceshipPartsBuilt = new List<int> { 1, 0, 5 };
		var victory = new SpaceRaceVictory(new List<int> { 1, 1 });

		VictoryStatus status = victory.Evaluate(player, VictoryTestHelpers.MakeGame());

		Assert.Equal(1, status.SpaceshipPartsBuilt);
		Assert.Equal(2, status.SpaceshipPartsRequired);
		Assert.False(victory.HasVictory(status));
	}
}
