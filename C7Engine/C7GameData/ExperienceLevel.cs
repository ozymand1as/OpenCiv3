namespace C7GameData {
	using System;
	using System.Text.Json.Serialization;
	using QueryCiv3.Biq;

	public class ExperienceLevel {
		public string key;
		public string displayName;
		public int baseHitPoints;
		public double retreatChance;
		public double promotionChance;

		// The retreat bonus as the binary uses it: EXPR.RetreatBonus, an
		// integer. The retreat chance in a fight is
		// ownRetreatBonus / (opponentRetreatBonus + 50), not this value as a
		// probability (12_combat.md §6.2). It is derived from the fractional
		// retreatChance so saves and the Lua ruleset, which carry that
		// fraction, need no new field.
		[JsonIgnore]
		public int retreatBonus => (int)Math.Round(100.0 * retreatChance);

		public ExperienceLevel(string key, string displayName, int baseHitPoints, double retreatChance, double promotionChance) {
			this.key = key;
			this.displayName = displayName;
			this.baseHitPoints = baseHitPoints;
			this.retreatChance = retreatChance;
			this.promotionChance = promotionChance;
		}

		public static ExperienceLevel ImportFromCiv3(string key, EXPR expr, int levelIndex) {
			var promotionChances = new double[] { 0.5, 0.25, 0.125, 0.0 };
			return new ExperienceLevel(key, expr.Name, expr.BaseHitPoints, expr.RetreatBonus / 100.0, promotionChances[levelIndex]);
		}
	}
}
