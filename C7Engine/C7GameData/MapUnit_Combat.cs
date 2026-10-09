using System;
using System.Collections.Generic;
using C7Engine;

namespace C7GameData {

	// The combat resolver's odds, bonus and retreat arithmetic, aligned to the
	// binary (re/specs/12_combat.md §2, §5 and §6).
	public partial class MapUnit {
		// The odds are the defender's win probability in units of 1/1024: the
		// round roll is rand_int(1024) and a roll below the odds means the
		// defender wins the round. The clamp to [1, 1023] keeps either side of
		// any fight with at least a 1/1024 chance, so the odds are never
		// exactly 0 or 1024 (12_combat.md §2).
		public const int CombatOddsScale = 1024;
		public const int MinCombatOdds = 1;
		public const int MaxCombatOdds = CombatOddsScale - 1;

		// A side that owns a wonder whose great-wonder flag is
		// DoubleCombatVsBarbarians (The Great Wall in the shipped rules) adds
		// this percentage on top of the difficulty's
		// AttackBonusAgainstBarbarians when it fights the barbarian civ
		// (12_combat.md §2.2).
		public const int GreatWallAntiBarbarianBonusPercent = 100;

		// The retreat chance is ownRetreatBonus / (opponentRetreatBonus + 50),
		// where both bonuses are EXPR.RetreatBonus values (12_combat.md §6.2).
		public const int RetreatChanceDenominatorOffset = 50;

		// The amphibious assault bonus: +25% for a land unit with the Amphibious
		// ability that attacks from water onto a non-water tile
		// (12_combat.md §2.1.2).
		public const int AmphibiousAssaultBonusPercent = 25;

		// The attack-availability test (23_leaders_armies_golden_age.md §6.8):
		// a unit that has already attacked this turn is refused another attack
		// unless its type has the Blitz ability. The Army type carries Blitz, so
		// an army may attack repeatedly, limited only by its remaining movement
		// points.
		public bool CanAttack() => !hasUsedAttack || unitType.isBlitz;

		// Whether this unit receives the amphibious assault bonus for a fight
		// against `defender` (12_combat.md §2.1.2): an Amphibious land type with
		// a positive attack strength, attacking from a water tile onto a
		// non-water tile, and either not having used its attack this turn or
		// being able to blitz. The binary reads the used-attack status bit here.
		// Fight raises that bit only after the attack's odds are computed, so a
		// real first attack still sees it clear; a Blitz type keeps the bonus on
		// its later attacks through the escape.
		public bool GetsAmphibiousAssaultBonus(MapUnit defender) {
			if (!unitType.isAmphibious || !unitType.IsLandUnit() || AttackStrength() <= 0) {
				return false;
			}
			if (!Tile.IsTileValid(location) || !Tile.IsTileValid(defender.location)) {
				return false;
			}
			if (defender.location.IsWater() || !location.IsWater()) {
				return false;
			}
			return !hasUsedAttack || unitType.isBlitz;
		}

		// A unit type may retreat only when it moves faster than one tile per
		// turn: the binary gates retreat on the type's maximum move points
		// exceeding RULE.MovementAlongRoads (3, measured from the shipped
		// conquests.biq), and move points are counted in thirds of a whole
		// move, so for whole-move rates the gate reads "movement rate greater
		// than one" — slow units never retreat (12_combat.md §6.1).
		public bool CanRetreat() => unitType.movement > 1;

		// The chance this unit escapes a fight it is losing, as a probability
		// in [0, 1]. The per-round roll in Fight uses the equivalent integer
		// form rand_int(opponentBonus + 50) < ownBonus.
		public double RetreatChance(MapUnit opponent) {
			if (!CanRetreat()) {
				return 0.0;
			}
			return experienceLevel.retreatBonus / (double)(opponent.experienceLevel.retreatBonus + RetreatChanceDenominatorOffset);
		}

		// The whole-percent value of a set of strength bonuses, summed. The
		// bonuses carry fractions of one (0.25 = +25%); the odds arithmetic
		// below works in whole percent, like the original's integer maths.
		private static int BonusPercent(IEnumerable<StrengthBonus> bonuses) {
			int percent = 0;
			foreach (StrengthBonus bonus in bonuses) {
				percent += (int)Math.Round(100.0 * bonus.amount);
			}
			return percent;
		}

		// The percentage added to the non-barbarian side of a fight against
		// the barbarian civ: the difficulty's AttackBonusAgainstBarbarians
		// plus a flat 100 while that side owns a DoubleCombatVsBarbarians
		// wonder (12_combat.md §2.2). At the shipped default difficulty
		// (Regent, 200) this triples the non-barbarian side's strength.
		public static int AntiBarbarianBonusPercent(Player nonBarbarian) {
			int percent = EngineStorage.gameData.gameDifficulty.AttackBonusAgainstBarbarians;
			if (nonBarbarian.OwnsBuildingWhere(b => b.doublesCombatVsBarbarians)) {
				percent += GreatWallAntiBarbarianBonusPercent;
			}
			return percent;
		}

		// Both sides' effective strengths: each side's strength scaled by 100
		// plus its whole bonus percentage (12_combat.md §2). The defence
		// percentages — terrain, river, city/fortress/barricade and
		// entrenchment — multiply strength; they are never added to it, so a
		// +100% bonus exactly doubles the side's effective strength. The
		// percentages stay un-divided so the odds division truncates like the
		// original's integer arithmetic.
		//
		// The ignoreDefensiveBonuses switch skips only the defender's tile
		// bonuses — the barbarian terms still apply (12_combat.md §2). The
		// attacker's +25% amphibious assault term is modelled; the +25 per-civ
		// tile mask still needs spec §7.1's open questions answered.
		public static (int attackerEffective, int defenderEffective) EffectiveCombatStrengths(
			MapUnit attacker, MapUnit defender, TileDirection? attackDirection, bool ignoreDefensiveBonuses = false) {
			int defenderPercent = ignoreDefensiveBonuses
				? 0
				: BonusPercent(defender.ListStrengthBonusesVersus(attacker, CombatRole.Defense, attackDirection));
			int attackerPercent = BonusPercent(attacker.ListStrengthBonusesVersus(defender, CombatRole.Attack, attackDirection));

			if (attacker.owner.isBarbarians) {
				defenderPercent += AntiBarbarianBonusPercent(defender.owner);
			}
			if (defender.owner.isBarbarians) {
				attackerPercent += AntiBarbarianBonusPercent(attacker.owner);
			}

			int attackerEffective = (int)attacker.BaseStrength(CombatRole.Attack) * (100 + attackerPercent);
			int defenderEffective = (int)defender.BaseStrength(CombatRole.Defense) * (100 + defenderPercent);
			return (attackerEffective, defenderEffective);
		}

		// The defender's win probability in units of 1/1024
		// (12_combat.md §2): defEff * 1024 / (atkEff + defEff), clamped to
		// [1, 1023] so the value is never exactly 0 or 1024.
		public static int DefenderCombatOdds(MapUnit attacker, MapUnit defender, TileDirection? attackDirection, bool ignoreDefensiveBonuses = false) {
			var (attackerEffective, defenderEffective) =
				EffectiveCombatStrengths(attacker, defender, attackDirection, ignoreDefensiveBonuses);
			if (attackerEffective + defenderEffective <= 0) {
				return MinCombatOdds;
			}
			return Math.Clamp((defenderEffective * CombatOddsScale) / (attackerEffective + defenderEffective),
				MinCombatOdds, MaxCombatOdds);
		}
	}
}
