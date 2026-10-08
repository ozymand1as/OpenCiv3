using System;
using C7GameData;
using Xunit;

namespace EngineTests.GameData;

/// <summary>
/// Pins the goody-hut outcome table: the nine rows of cumulative thresholds,
/// the Expansionist row shift, the City rule toggle, the test order and the
/// single-draw rule.
/// </summary>
public class GoodyHutTableTest {
	// Chance in twentieths that each row gives each outcome, derived from the
	// cumulative thresholds. A row's counts must sum to 20.
	[Theory]
	[InlineData(0, 2, 7, 3, 3, 2, 2, 0, 1)]
	[InlineData(1, 2, 6, 3, 3, 2, 2, 2, 0)]
	[InlineData(2, 2, 5, 2, 2, 2, 2, 5, 0)]
	[InlineData(3, 1, 4, 2, 2, 2, 2, 7, 0)]
	[InlineData(4, 1, 3, 2, 2, 2, 2, 8, 0)]
	// Row 5 comes from the threshold table (1, 3, 4, 5, 6, 7) and row 7 from
	// (0, 0, 1, 1, 2, 3). Both thresholds were re-read from the shipped
	// executable and match spec section 4.2's threshold table byte for byte.
	// That table's own derived percentage table disagrees on these two rows:
	// with a strict "draw < threshold" test the thresholds give row 5
	// 1/2/1/1/1/1/13 and row 7 1/0/1/1/1/17, not the 1/2/2/2/2/2/9 and
	// 0/1/1/1/1/1/16 it claims, and row 7's repeated "1" threshold means the
	// Settlers outcome can never be reached on that row at all. The
	// thresholds are the data.
	[InlineData(5, 1, 2, 1, 1, 1, 1, 13, 0)]
	[InlineData(6, 0, 1, 1, 1, 1, 1, 15, 0)]
	[InlineData(7, 0, 0, 1, 0, 1, 1, 17, 0)]
	[InlineData(8, 0, 0, 0, 0, 1, 1, 18, 0)]
	public void EachRowGivesTheSpecifiedDistribution(int row, int city, int tech, int money, int settlers, int maps, int mercenaries, int barbarians, int nothing) {
		Assert.Equal(city, GoodyHutTable.CountInTwentieths(row, GoodyHutOutcome.City));
		Assert.Equal(tech, GoodyHutTable.CountInTwentieths(row, GoodyHutOutcome.Tech));
		Assert.Equal(money, GoodyHutTable.CountInTwentieths(row, GoodyHutOutcome.Money));
		Assert.Equal(settlers, GoodyHutTable.CountInTwentieths(row, GoodyHutOutcome.Settlers));
		Assert.Equal(maps, GoodyHutTable.CountInTwentieths(row, GoodyHutOutcome.Maps));
		Assert.Equal(mercenaries, GoodyHutTable.CountInTwentieths(row, GoodyHutOutcome.Mercenaries));
		Assert.Equal(barbarians, GoodyHutTable.CountInTwentieths(row, GoodyHutOutcome.Barbarians));
		Assert.Equal(nothing, GoodyHutTable.CountInTwentieths(row, GoodyHutOutcome.Nothing));

		int total = city + tech + money + settlers + maps + mercenaries + barbarians + nothing;
		Assert.Equal(20, total);
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(-7)]
	[InlineData(9)]
	[InlineData(100)]
	public void RowsOutsideTheTableAlwaysYieldNothing(int row) {
		for (int draw = 0; draw < 20; ++draw) {
			Assert.Equal(GoodyHutOutcome.Nothing, GoodyHutTable.ForDraw(row, draw));
		}
	}

	// Being Expansionist uses the row of one difficulty level lower.
	[Theory]
	[InlineData(0, true, 0)]
	[InlineData(0, false, 1)]
	[InlineData(2, true, 2)]
	[InlineData(2, false, 3)]
	[InlineData(3, true, 3)]
	[InlineData(3, false, 4)]
	[InlineData(7, true, 7)]
	[InlineData(7, false, 8)]
	public void ExpansionistUsesOneRowLower(int difficulty, bool expansionist, int expectedRow) {
		Assert.Equal(expectedRow, GoodyHutTable.RowIndex(difficulty, expansionist));
	}

	// Row 0's fall-through is Nothing; every other row's is Barbarians.
	[Theory]
	[InlineData(0, 19, GoodyHutOutcome.Nothing)]
	[InlineData(1, 19, GoodyHutOutcome.Barbarians)]
	[InlineData(8, 19, GoodyHutOutcome.Barbarians)]
	public void TheFallThroughDependsOnTheRow(int row, int draw, GoodyHutOutcome expected) {
		Assert.Equal(expected, GoodyHutTable.ForDraw(row, draw));
	}

	// Thresholds are tested in the order City, Tech, Money, Settlers, Maps,
	// Mercenaries; each boundary draw belongs to the next outcome.
	[Theory]
	[InlineData(0, 0, GoodyHutOutcome.City)]
	[InlineData(0, 1, GoodyHutOutcome.City)]
	[InlineData(0, 2, GoodyHutOutcome.Tech)]
	[InlineData(0, 8, GoodyHutOutcome.Tech)]
	[InlineData(0, 9, GoodyHutOutcome.Money)]
	[InlineData(0, 11, GoodyHutOutcome.Money)]
	[InlineData(0, 12, GoodyHutOutcome.Settlers)]
	[InlineData(0, 14, GoodyHutOutcome.Settlers)]
	[InlineData(0, 15, GoodyHutOutcome.Maps)]
	[InlineData(0, 16, GoodyHutOutcome.Maps)]
	[InlineData(0, 17, GoodyHutOutcome.Mercenaries)]
	[InlineData(0, 18, GoodyHutOutcome.Mercenaries)]
	[InlineData(3, 0, GoodyHutOutcome.City)]
	[InlineData(3, 1, GoodyHutOutcome.Tech)]
	[InlineData(3, 4, GoodyHutOutcome.Tech)]
	[InlineData(3, 5, GoodyHutOutcome.Money)]
	public void OutcomesAreTestedInOrder(int row, int draw, GoodyHutOutcome expected) {
		Assert.Equal(expected, GoodyHutTable.ForDraw(row, draw));
	}

	[Fact]
	public void DisablingCitiesMovesTheirMassToTech() {
		for (int row = 0; row < GoodyHutTable.NumRows; ++row) {
			Assert.Equal(0, GoodyHutTable.CountInTwentieths(row, GoodyHutOutcome.City, allowCities: false));
			Assert.Equal(
				GoodyHutTable.CountInTwentieths(row, GoodyHutOutcome.City) + GoodyHutTable.CountInTwentieths(row, GoodyHutOutcome.Tech),
				GoodyHutTable.CountInTwentieths(row, GoodyHutOutcome.Tech, allowCities: false));
		}
	}

	/// <summary>
	/// The roll makes exactly one draw of twenty values: a second generator
	/// stepped once with Next(20) stays in lockstep with it.
	/// </summary>
	[Fact]
	public void TheRollConsumesExactlyOneDraw() {
		foreach (int row in new[] { 0, 3, 5, 8 }) {
			Random rollRng = new(20260101);
			Random probeRng = new(20260101);

			for (int i = 0; i < 25; ++i) {
				GoodyHutOutcome outcome = GoodyHutTable.Roll(row, rollRng);
				int expectedDraw = probeRng.Next(20);
				Assert.Equal(GoodyHutTable.ForDraw(row, expectedDraw), outcome);
			}
		}
	}
}
