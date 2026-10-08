using System.Collections.Generic;
using System.Linq;
using C7Engine;
using C7GameData;
using Xunit;

namespace EngineTests.GameData.Victory;

/// The type-8 victory-point condition itself (spec 25 section 2.8): the
/// inclusive limit test, the alliance sum, and the status rows.
public class VictoryPointVictoryTest {

	[Theory]
	[InlineData(50000, 49999, false)]
	[InlineData(50000, 50000, true)]  // reaching the limit exactly counts
	[InlineData(50000, 50001, true)]
	[InlineData(0, 0, true)]
	public void HasVictory_IsInclusive(int limit, int total, bool expected) {
		var victory = new VictoryPointVictory(limit);

		Assert.Equal(expected, victory.HasVictory(new VictoryStatus { VictoryPoints = total, VictoryPointLimit = limit }));
	}

	[Fact]
	public void Evaluate_ReportsThePlayersOwnTotal() {
		var victory = new VictoryPointVictory(limit: 50000);
		C7GameData.GameData game = VictoryTestHelpers.MakeGame();
		Player rome = VictoryTestHelpers.MakePlayer("player-2", "Rome");
		game.players.Add(rome);
		rome.victoryPoints = 120;

		VictoryStatus status = victory.Evaluate(rome, game);

		Assert.Equal(120, status.VictoryPoints);
		Assert.Equal(50000, status.VictoryPointLimit);
		Assert.False(victory.HasVictory(status));
	}

	[Fact]
	public void Evaluate_SumsTheTotalsOfAnAlliance() {
		// Section 2.8: alliance games sum the members' totals.
		var victory = new VictoryPointVictory(limit: 50);
		C7GameData.GameData game = VictoryTestHelpers.MakeGame();
		Player rome = VictoryTestHelpers.MakePlayer("player-2", "Rome");
		Player egypt = VictoryTestHelpers.MakePlayer("player-4", "Egypt");
		Player greece = VictoryTestHelpers.MakePlayer("player-3", "Greece");
		game.players.AddRange([rome, egypt, greece]);

		Alliance alliance = new Alliance(1, "Team");
		rome.alliance = alliance;
		egypt.alliance = alliance;
		rome.victoryPoints = 30;
		egypt.victoryPoints = 20;
		greece.victoryPoints = 40;

		VictoryStatus allied = victory.Evaluate(rome, game);
		VictoryStatus rival = victory.Evaluate(greece, game);

		Assert.Equal(50, allied.VictoryPoints);
		Assert.True(victory.HasVictory(allied));
		Assert.Equal(40, rival.VictoryPoints);
		Assert.False(victory.HasVictory(rival));
	}

	[Fact]
	public void GenerateStatusRows_ShowsTheLimitAndTheCurrentTotal() {
		var victory = new VictoryPointVictory(limit: 50000);
		Player rome = VictoryTestHelpers.MakePlayer("player-2", "Rome");
		var status = new VictoryStatus { Player = rome, VictoryPoints = 400, VictoryPointLimit = 50000 };

		List<string[]> rows = victory.GenerateStatusRows(status, []).ToList();

		Assert.Equal("Limit", rows[0][2]);
		Assert.Equal("50000", rows[0][3]);
		Assert.Equal("Current victory points:", rows[1][2]);
		Assert.Equal("400", rows[1][3]);
	}

	[Fact]
	public void VictoryPointVictory_HasTheStatusScreenHeader() {
		Assert.Equal("Victory Points", new VictoryPointVictory(50000).Header());
	}

	[Theory]
	// Section 2.8: the gate is the whole 0x26000 group, so any one of the three
	// flags turns the victory-point system on.
	[InlineData(true, false, false, true)]
	[InlineData(false, true, false, true)]
	[InlineData(false, false, true, true)]
	[InlineData(false, false, false, false)]
	public void VictoryConditions_VictoryPointsEnabled_IsTheWholeGroup(
			bool victoryLocations, bool captureTheFlag, bool reverseCaptureTheFlag, bool expected) {
		var rules = new VictoryConditions {
			VictoryLocations = victoryLocations,
			CaptureTheFlag = captureTheFlag,
			ReverseCaptureTheFlag = reverseCaptureTheFlag,
		};

		Assert.Equal(expected, rules.VictoryPointsEnabled);
	}
}
