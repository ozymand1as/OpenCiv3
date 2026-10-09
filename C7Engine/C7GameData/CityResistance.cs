using System.Collections.Generic;
using System.Linq;
using C7Engine;

namespace C7GameData {
	// Resistance and quelling for captured cities (spec 17 section 7, with the
	// capture-time roll described by the shipped game's own documentation:
	// each citizen of a captured city independently rolls for resistance, and
	// the per-turn quelling pass asks each resisting citizen whether the
	// resistance continues).
	//
	// The two rolls share one formula: the cultural-opinion level is picked
	// from the CULT table by comparing the new owner's accumulated culture
	// against the culture of the player the city was captured from, the level
	// supplies InitialResistanceChance or ContinuedResistanceChance, and the
	// government-pair ResistanceModifier is added; the roll is 0-99 and
	// resistance continues while the roll is below that sum.
	public partial class City {
		// The player this city was captured from. The resistance rolls compare
		// the current owner against this player; it stays set after the last
		// resister is quelled so a recapture forms the same pair again. Null
		// for a city that was never captured (no pair, no resistance).
		public Player resistanceFrom { get; set; }

		// Rolls the initial resistance for every citizen of a just-captured
		// city (spec 17 section 7; the chance table is CULT's
		// InitialResistanceChance plus the government-pair modifier).
		public void StartResistance(GameData gameData) {
			if (resistanceFrom == null || resistanceFrom == owner) {
				return;
			}
			int chance = ResistanceChance(gameData, initial: true);
			if (chance <= 0) {
				return;
			}
			foreach (CityResident resident in residents.ToList()) {
				if (GameData.rng.Next(100) < chance) {
					SetResisting(resident, true, gameData);
				}
			}
		}

		// The per-turn quelling pass (spec 17 section 7): the garrison gets
		// units_on_tile * DIFF.Quelled_Citizens attempts, each attempt asking
		// one resisting citizen whether its resistance continues. Returns the
		// number of citizens quelled this call.
		public int QuellResistance(GameData gameData) {
			List<CityResident> resisters = residents.Where(r => r.isResisting).ToList();
			if (resisters.Count == 0) {
				return 0;
			}
			// The continuation roll needs a recorded origin for the resisting
			// citizens (spec 17 section 7); without one the attempt is not
			// made and the resistance stands.
			if (resistanceFrom == null || resistanceFrom == owner) {
				return 0;
			}

			int attempts = QuellingAttempts(gameData);
			int quelled = 0;
			for (int i = 0; i < attempts && i < resisters.Count; i++) {
				if (GameData.rng.Next(100) >= ResistanceChance(gameData, initial: false)) {
					SetResisting(resisters[i], false, gameData);
					quelled++;
				}
			}

			// A successful quell of any resister, with the owner the human
			// player, shows the RESISTANCEQUELLED map message (spec 17
			// section 7). The key has no text entry in OpenCiv3's UI, so the
			// popup carries literal text (same approach as pollution).
			if (quelled > 0 && owner.isHuman) {
				new MsgShowTemporaryPopup($"Resistance quelled in {name}", location).send();
			}
			return quelled;
		}

		// The attempt budget of one quelling pass: the number of the owner's
		// ground combat units on the city tile, times the difficulty's
		// Quelled_Citizens (MilitaryLaw) value (spec 15 section 4.7; air,
		// naval, artillery, settler and worker units do not quell).
		public int QuellingAttempts(GameData gameData) {
			if (location == null || owner == null) {
				return 0;
			}
			int units = location.unitsOnTile.Count(u =>
				u.owner == owner && u.IsLandUnit() && u.unitType.attack > 0);
			int militaryLaw = gameData.gameDifficulty?.MilitaryLaw ?? 0;
			return units * militaryLaw;
		}

		internal void SetResisting(CityResident resident, bool resisting, GameData gameData) {
			if (resident.isResisting == resisting) {
				return;
			}
			resident.isResisting = resisting;
			if (resisting) {
				// A resisting citizen stops working its tile (spec 15
				// section 4.7), so the tile's yields leave the city totals
				// with it.
				if (resident.tileWorked != Tile.NONE) {
					resident.tileWorked.personWorkingTile = null;
					resident.tileWorked = Tile.NONE;
				}
			} else if (resident.citizenType.IsDefaultCitizen && resident.tileWorked == Tile.NONE) {
				// Once the resistance ends the citizen goes back to work the
				// same way a newly grown citizen does.
				C7Engine.AI.CityTileAssignmentAI.AssignNewCitizenToTile(gameData, resident);
			}
		}

		// The combined initial/continued resistance chance for this city:
		// the CULT level's base chance plus the government-pair modifier.
		private int ResistanceChance(GameData gameData, bool initial) {
			int level = ResistanceOpinionLevel(gameData);
			if (level < 0) {
				return 0;
			}
			CultureLevel cultureLevel = gameData.cultureLevels[level];
			int chance = initial
				? cultureLevel.initialResistanceChance
				: cultureLevel.continuedResistanceChance;
			return chance + ResistanceGovernmentModifier(gameData);
		}

		// The cultural-opinion level (spec 17 section 8): the CULT row with
		// the largest CultureRatioPercentage not exceeding the ratio of the
		// new owner's accumulated culture to the captured-from player's, or
		// the table's lowest-ratio row when the ratio falls below every
		// threshold. The ratio's direction - owner over captured-from - is
		// what makes "in awe of" (40% initial) the row a strong-culture
		// conqueror draws.
		private int ResistanceOpinionLevel(GameData gameData) {
			List<CultureLevel> levels = gameData.cultureLevels;
			if (levels == null || levels.Count == 0) {
				return -1;
			}
			int own = owner.TotalAccumulatedCulture();
			int other = resistanceFrom.TotalAccumulatedCulture();
			long ratio;
			if (own <= 0) {
				ratio = 0;
			} else if (other <= 0) {
				ratio = int.MaxValue;
			} else {
				ratio = 100L * own / other;
			}

			int chosen = -1;
			int chosenThreshold = int.MinValue;
			int lowest = 0;
			int lowestThreshold = int.MaxValue;
			for (int i = 0; i < levels.Count; i++) {
				int threshold = levels[i].cultureRatioPercentage;
				if (threshold <= ratio && threshold > chosenThreshold) {
					chosen = i;
					chosenThreshold = threshold;
				}
				if (threshold < lowestThreshold) {
					lowest = i;
					lowestThreshold = threshold;
				}
			}
			return chosen >= 0 ? chosen : lowest;
		}

		// The government-pair term (spec 17 section 7): this government's
		// ResistanceModifier against the captured-from player's government.
		private int ResistanceGovernmentModifier(GameData gameData) {
			if (owner?.government == null || resistanceFrom?.government == null) {
				return 0;
			}
			int own = gameData.governments.IndexOf(owner.government);
			int theirs = gameData.governments.IndexOf(resistanceFrom.government);
			if (own < 0 || theirs < 0) {
				return 0;
			}
			return gameData.governments[own].ResistanceModifierAgainst(theirs);
		}
	}
}
