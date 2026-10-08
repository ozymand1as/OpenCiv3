using System;

namespace C7GameData {
	/// <summary>
	/// The result of popping a goody hut (a "tribal village").
	/// </summary>
	public enum GoodyHutOutcome {
		City,
		Tech,
		Money,
		Settlers,
		Maps,
		Mercenaries,
		Nothing,
		Barbarians,
	}

	/// <summary>
	/// The goody-hut outcome table.
	///
	/// Popping a hut makes exactly one uniform draw over 20 values and walks
	/// cumulative thresholds in a fixed order. The table row is the popping
	/// player's difficulty plus one unless that player is Expansionist, so
	/// being Expansionist is mechanically the same as playing one difficulty
	/// level lower. Rows outside 0..8 yield Nothing.
	///
	/// The final column is not a threshold - it is the outcome returned when
	/// the draw is not below any threshold, and it is 3 ("Nothing") only on
	/// row 0. That is why Chieftain can never get hostile huts.
	/// </summary>
	public static class GoodyHutTable {
		public const int NumRows = 9;

		// The seven columns of the table, in the order the engine tests
		// them. An outcome applies when the draw is strictly below its
		// threshold. Each array is indexed by table row 0..8.
		public static readonly GoodyHutOutcome[] TestOrder = {
			GoodyHutOutcome.City,
			GoodyHutOutcome.Tech,
			GoodyHutOutcome.Money,
			GoodyHutOutcome.Settlers,
			GoodyHutOutcome.Maps,
			GoodyHutOutcome.Mercenaries,
		};

		// City: 2, 2, 2, 1, 1, 1, 0, 0, 0
		// Tech: 9, 8, 7, 5, 4, 3, 1, 0, 0
		// Money: 12, 11, 9, 7, 6, 4, 2, 1, 0
		// Settlers: 15, 14, 11, 9, 8, 5, 3, 1, 0
		// Maps: 17, 16, 13, 11, 10, 6, 4, 2, 1
		// Mercenaries: 19, 18, 15, 13, 12, 7, 5, 3, 2
		private static readonly int[][] thresholds = {
			new[] { 2, 2, 2, 1, 1, 1, 0, 0, 0 },
			new[] { 9, 8, 7, 5, 4, 3, 1, 0, 0 },
			new[] { 12, 11, 9, 7, 6, 4, 2, 1, 0 },
			new[] { 15, 14, 11, 9, 8, 5, 3, 1, 0 },
			new[] { 17, 16, 13, 11, 10, 6, 4, 2, 1 },
			new[] { 19, 18, 15, 13, 12, 7, 5, 3, 2 },
		};

		// The seventh column: the outcome when the draw is below no
		// threshold. Not a threshold itself.
		private static readonly GoodyHutOutcome[] fallback = {
			GoodyHutOutcome.Nothing,
			GoodyHutOutcome.Barbarians,
			GoodyHutOutcome.Barbarians,
			GoodyHutOutcome.Barbarians,
			GoodyHutOutcome.Barbarians,
			GoodyHutOutcome.Barbarians,
			GoodyHutOutcome.Barbarians,
			GoodyHutOutcome.Barbarians,
			GoodyHutOutcome.Barbarians,
		};

		/// <summary>
		/// The table row for a difficulty index and Expansionist trait.
		/// </summary>
		public static int RowIndex(int difficulty, bool expansionist) {
			return difficulty + (expansionist ? 0 : 1);
		}

		/// <summary>
		/// Resolves an already-drawn value against the table.
		/// </summary>
		/// <param name="row">The table row, which may be outside 0..8.</param>
		/// <param name="draw">The uniform draw, in 0..19.</param>
		/// <param name="allowCities">
		/// Whether the City outcome is enabled by the rule toggle. When it is
		/// off the City test is skipped and its probability mass falls through
		/// to Tech.
		/// </param>
		public static GoodyHutOutcome ForDraw(int row, int draw, bool allowCities = true) {
			if (row < 0 || row >= NumRows) {
				return GoodyHutOutcome.Nothing;
			}

			for (int i = 0; i < TestOrder.Length; ++i) {
				if (TestOrder[i] == GoodyHutOutcome.City && !allowCities) {
					continue;
				}
				if (draw < thresholds[i][row]) {
					return TestOrder[i];
				}
			}

			return fallback[row];
		}

		/// <summary>
		/// Makes the single uniform draw and resolves it against the table.
		/// One call is one draw; whether the outcome is then rejected and the
		/// roll repeated is up to the caller (see GoodyHutInteractions.Consume,
		/// which is what Civ3 does).
		/// </summary>
		public static GoodyHutOutcome Roll(int row, Random rng, bool allowCities = true) {
			int draw = rng.Next(20);
			return ForDraw(row, draw, allowCities);
		}

		// The chance, in twentieths, that a row gives each outcome. Used by
		// the tests to pin the table.
		public static int CountInTwentieths(int row, GoodyHutOutcome outcome, bool allowCities = true) {
			int count = 0;
			for (int draw = 0; draw < 20; ++draw) {
				if (ForDraw(row, draw, allowCities) == outcome) {
					++count;
				}
			}
			return count;
		}
	}
}
