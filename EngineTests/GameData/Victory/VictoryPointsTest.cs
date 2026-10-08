using System.Collections.Generic;
using System.Linq;
using C7Engine;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using Xunit;

namespace EngineTests.GameData.Victory;

/// The Conquests victory-point awards (spec 25 section 4). The engine
/// implements the three categories it can already observe - advances, unit
/// kills and Great Wonders - and these tests pin each award's arithmetic,
/// its integer-division boundary and the rules gate.
public class VictoryPointsTest : IClassFixture<SaveGameFixture> {

	private readonly SaveGameFixture fixture;

	public VictoryPointsTest(SaveGameFixture fixture) {
		this.fixture = fixture;
	}

	private static VictoryConditions Rules(bool enabled = true, int advancementCost = 5,
			int unitKillCost = 5, int wonderCost = 10, int limit = 50000) {
		return new VictoryConditions {
			// Any one of the 0x26000 group turns the whole system on.
			VictoryLocations = enabled,
			AdvancementCost = advancementCost,
			DefeatingOpposingUnitCost = unitKillCost,
			WonderCost = wonderCost,
			VictoryPointLimit = limit,
		};
	}

	private static C7GameData.GameData MakeGame(VictoryConditions rules, int turn = 1) {
		C7GameData.GameData game = VictoryTestHelpers.MakeGame(turn);
		game.victoryConditions = rules;
		// UpdateHistory recomputes the score, which needs a difficulty and the
		// turn-to-year map.
		var difficulty = new Difficulty();
		game.difficulties = new List<Difficulty> { difficulty };
		game.gameDifficulty = difficulty;
		game.timeOptions = new TimeOptions();
		return game;
	}

	private static Tech MakeTech(int cost) => new Tech { id = ID.None("tech"), Cost = cost };

	private static UnitPrototype MakeUnitPrototype(int shieldCost) =>
		new UnitPrototype { name = "unit", shieldCost = shieldCost };

	private static MapUnit MakeUnit(Player owner, int shieldCost) {
		return new MapUnit(ID.None("unit")) {
			owner = owner,
			unitType = MakeUnitPrototype(shieldCost),
		};
	}

	private static Building MakeWonder(C7GameData.GameData game, int shieldCost, bool great) {
		return new Building(new SaveBuilding {
			name = "wonder",
			shieldCost = shieldCost,
			greatWonderProperties = great ? new SaveBuilding.GreatWonderProperties() : null,
		}, game);
	}

	// ---------------------------------------------------------------- the formulas

	[Fact]
	public void ForAdvance_IsTheTechCostTimesTheAdvancementCost() {
		// The shipped rules use AdvancementCost 5, and Bronze Working costs 3.
		Assert.Equal(15, VictoryPoints.ForAdvance(MakeTech(3), Rules()));
		Assert.Equal(1800, VictoryPoints.ForAdvance(MakeTech(360), Rules()));
	}

	[Theory]
	[InlineData(0, 5, 0)]   // a free technology awards nothing
	[InlineData(3, 0, 0)]   // the rules turn the award off
	[InlineData(3, 5, 15)]
	public void ForAdvance_RespectsZeroCosts(int techCost, int advancementCost, int expected) {
		Assert.Equal(expected, VictoryPoints.ForAdvance(MakeTech(techCost), Rules(advancementCost: advancementCost)));
	}

	[Theory]
	// The shipped rules use DefeatingOpposingUnitCost 5. Civ3 divides the unit's
	// value by ten before multiplying, so a 19-shield unit is worth the same as
	// a 10-shield one, and a unit below ten shields is worth nothing.
	[InlineData(10, 5)]
	[InlineData(19, 5)]
	[InlineData(20, 10)]
	[InlineData(110, 55)]
	[InlineData(9, 0)]
	public void ForUnitKill_DividesTheUnitValueByTenBeforeMultiplying(int unitValue, int expected) {
		Assert.Equal(expected, VictoryPoints.ForUnitKill(MakeUnitPrototype(unitValue), Rules()));
	}

	[Fact]
	public void ForUnitKill_IsZeroWhenTheRulesDisableIt() {
		Assert.Equal(0, VictoryPoints.ForUnitKill(MakeUnitPrototype(110), Rules(unitKillCost: 0)));
	}

	[Theory]
	// Civ3 stores a building's cost at one tenth of its shield cost, so the
	// shipped rules' WonderCost of 10 makes a Great Wonder worth its full shield
	// cost: the 400-shield Pyramids are worth 400 victory points.
	[InlineData(400, 10, 400)]
	[InlineData(1000, 10, 1000)]
	[InlineData(35, 10, 30)] // 35/10 = 3 (truncating), x10
	[InlineData(9, 10, 0)]
	[InlineData(400, 0, 0)]
	public void ForGreatWonder_DividesTheShieldCostByTenBeforeMultiplying(int shieldCost, int wonderCost, int expected) {
		C7GameData.GameData game = MakeGame(Rules(wonderCost: wonderCost));
		Assert.Equal(expected, VictoryPoints.ForGreatWonder(MakeWonder(game, shieldCost, great: true), Rules(wonderCost: wonderCost)));
	}

	// ---------------------------------------------------------------- the player entry points

	[Fact]
	public void AwardVictoryPointsForAdvance_AddsToTheTotal() {
		C7GameData.GameData game = MakeGame(Rules(), turn: 1);
		Player rome = VictoryTestHelpers.MakePlayer("player-2", "Rome");

		rome.AwardVictoryPointsForAdvance(game, MakeTech(3));
		rome.AwardVictoryPointsForAdvance(game, MakeTech(4));

		Assert.Equal(35, rome.victoryPoints);
	}

	[Fact]
	public void AwardVictoryPointsForAdvance_IsSilentOnTurnZero() {
		// Civ3 guards this award with `0 < p_current_turn_no`, so a technology
		// granted while the game is being set up awards nothing.
		C7GameData.GameData game = MakeGame(Rules(), turn: 0);
		Player rome = VictoryTestHelpers.MakePlayer("player-2", "Rome");

		rome.AwardVictoryPointsForAdvance(game, MakeTech(3));

		Assert.Equal(0, rome.victoryPoints);
	}

	[Fact]
	public void AwardVictoryPointsForAdvance_IsSilentWhenTheRulesDisableVictoryPoints() {
		C7GameData.GameData game = MakeGame(Rules(enabled: false));
		Player rome = VictoryTestHelpers.MakePlayer("player-2", "Rome");

		rome.AwardVictoryPointsForAdvance(game, MakeTech(3));

		Assert.Equal(0, rome.victoryPoints);
	}

	[Fact]
	public void AwardVictoryPointsForUnitKill_CreditsTheKillerForAnEnemyUnit() {
		C7GameData.GameData game = MakeGame(Rules());
		Player rome = VictoryTestHelpers.MakePlayer("player-2", "Rome");
		Player greece = VictoryTestHelpers.MakePlayer("player-3", "Greece");

		rome.AwardVictoryPointsForUnitKill(game, MakeUnit(greece, 110));

		Assert.Equal(55, rome.victoryPoints);
		Assert.Equal(0, greece.victoryPoints);
	}

	[Fact]
	public void AwardVictoryPointsForUnitKill_IgnoresOwnUnitsAndBarbarians() {
		C7GameData.GameData game = MakeGame(Rules());
		Player rome = VictoryTestHelpers.MakePlayer("player-2", "Rome");
		Player barbs = VictoryTestHelpers.MakePlayer("player-0", "Barbarians", barbarian: true);

		// A unit destroyed by its own side is not a kill.
		rome.AwardVictoryPointsForUnitKill(game, MakeUnit(rome, 110));
		// Civ3 passes the killer's player slot to its death handler and ignores
		// slot 0, which is the barbarian/neutral slot.
		barbs.AwardVictoryPointsForUnitKill(game, MakeUnit(rome, 110));

		Assert.Equal(0, rome.victoryPoints);
		Assert.Equal(0, barbs.victoryPoints);
	}

	[Fact]
	public void AwardVictoryPointsForUnitKill_IsSilentWhenTheRulesDisableVictoryPoints() {
		C7GameData.GameData game = MakeGame(Rules(enabled: false));
		Player rome = VictoryTestHelpers.MakePlayer("player-2", "Rome");
		Player greece = VictoryTestHelpers.MakePlayer("player-3", "Greece");

		rome.AwardVictoryPointsForUnitKill(game, MakeUnit(greece, 110));

		Assert.Equal(0, rome.victoryPoints);
	}

	[Fact]
	public void AwardVictoryPointsForGreatWonder_AwardsGreatWondersButNotSmallOnes() {
		C7GameData.GameData game = MakeGame(Rules());
		Player rome = VictoryTestHelpers.MakePlayer("player-2", "Rome");

		rome.AwardVictoryPointsForGreatWonder(game, MakeWonder(game, 400, great: true));
		rome.AwardVictoryPointsForGreatWonder(game, MakeWonder(game, 400, great: false));

		Assert.Equal(400, rome.victoryPoints);
	}

	[Fact]
	public void AwardVictoryPointsForGreatWonder_IsSilentWhenTheRulesDisableVictoryPoints() {
		C7GameData.GameData game = MakeGame(Rules(enabled: false));
		Player rome = VictoryTestHelpers.MakePlayer("player-2", "Rome");

		rome.AwardVictoryPointsForGreatWonder(game, MakeWonder(game, 400, great: true));

		Assert.Equal(0, rome.victoryPoints);
	}

	// ---------------------------------------------------------------- history and persistence

	[Fact]
	public void UpdateHistory_RecordsTheRunningVictoryPointTotal() {
		C7GameData.GameData game = MakeGame(Rules());
		Player rome = VictoryTestHelpers.MakePlayer("player-2", "Rome");
		game.players.Add(rome);
		game.history[rome.id.ToString()] = new List<HistTurnRecord>();
		rome.victoryPoints = 1234;

		rome.UpdateHistory(game);

		Assert.Equal(1234, game.history[rome.id.ToString()].Single().VP);
	}

	[Fact]
	public void VictoryPoints_SurviveASaveRoundTrip() {
		C7GameData.GameData game = SaveGameFixture.HydrateSaveGame(fixture.saveGame);
		Player rome = game.players.First(p => !p.isBarbarians);
		rome.victoryPoints = 12345;

		SaveGame save = SaveGame.FromGameData(game);

		Assert.Equal(12345, save.Players.First(p => p.id == rome.id).victoryPoints.Value);

		C7GameData.GameData reloaded = save.ToGameData(fixture.behaviors);
		Assert.Equal(12345, reloaded.players.First(p => p.id == rome.id).victoryPoints);
	}
}
