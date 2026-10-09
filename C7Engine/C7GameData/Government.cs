using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace C7GameData {
	public class Government {
		public ID id;
		public string name;
		public string civilopediaEntry;

		// If non-null, the tech required to use this government.
		public ID prerequisiteTech;

		// If true, this is the starting government.
		public bool defaultType;

		// If true, this is the government used while switching governments.
		public bool transitionType;

		// The mission id this government's cities are immune to, or -1 for
		// none (BIQ GOVT.ImmuneTo). The original engine only consults this for
		// missions 0 through 6.
		public int immuneTo = -1;

		// The index (0..3) into the espionage experience-bonus table that
		// applies when a diplomat runs a mission against this government
		// (BIQ GOVT.DiplomatsAre).
		public int diplomatsAre = 1;

		// Ditto for a spy (BIQ GOVT.SpiesAre).
		public int spiesAre = 1;

		// The "despotism penalty" applies for this goverment; reduces all 
		// commerce, production and food output done by citizen laborers by -1 
		// when they produce more than 2.
		public bool hasTilePenalty {
			get => _hasTilePenalty;
			set {
				_hasTilePenalty = value;
				if (value) {
					tileModifier += TilePenalty;
				}
			}
		}
		private bool _hasTilePenalty;

		// +1 commerce any tile with at least 1 commerce.
		public bool hasTradeBonus {
			get => _hasTradeBonus;
			set {
				_hasTradeBonus = value;
				if (value) {
					tileModifier += TradeBonus;
				}
			}
		}
		private bool _hasTradeBonus;

		[JsonIgnore]
		public Action<Tile.Yield> tileModifier;

		// See https://codehappy.net/apolyton/threads/46801-1.htm and
		// https://forums.civfanatics.com/threads/everything-about-corruption-c3c-edition.76619/.
		public enum CorruptionType {
			Minimal,
			Nuisance,
			Problematic,
			Rampant,
			Catastrophic,
			Communal,
			Off
		};
		public CorruptionType corruptionType;

		public enum HurryProductionType {
			CannotHurry,
			ForcedLabor,
			PaidLabor,
		};
		public HurryProductionType hurryingType;

		public int draftLimit;
		public int militaryPoliceLimit;
		public int workerRate;

		public bool allUnitsFree;
		public int freeUnitsPerTown;
		public int freeUnitsPerCity;
		public int freeUnitsPerMetropolis;
		public int unitCost;

		// One entry per government in the game's government list, in list
		// order: this government's resistance modifier against each other
		// government (BIQ GOVT_GOVT ResistanceModifier; the government-pair
		// term the resistance rolls add, spec 17 section 7). Missing entries
		// mean no modifier, so a ruleset that predates the table plays with a
		// zero modifier rather than failing to load.
		public List<int> resistanceModifiers = new();

		public int ResistanceModifierAgainst(int otherGovernmentIndex) {
			if (otherGovernmentIndex < 0 || otherGovernmentIndex >= resistanceModifiers.Count) {
				return 0;
			}
			return resistanceModifiers[otherGovernmentIndex];
		}

		// One entry per government in the game's government list, in list
		// order: this government's propaganda (bribery) modifier against each
		// other government (BIQ GOVT_GOVT BriberyModifier - the middle member
		// of the CanBribe / BriberyModifier / ResistanceModifier triple; the
		// propaganda roll reads offset +4 of the entry, spec 24 section 6.6).
		// Missing entries mean no modifier, so a ruleset that predates the
		// table plays with a zero modifier rather than failing to load.
		public List<int> briberyModifiers = new();

		public int BriberyModifierAgainst(int otherGovernmentIndex) {
			if (otherGovernmentIndex < 0 || otherGovernmentIndex >= briberyModifiers.Count) {
				return 0;
			}
			return briberyModifiers[otherGovernmentIndex];
		}

		private static void TradeBonus(Tile.Yield yield) {
			if (yield.type == Tile.YieldType.Commerce && yield.baseYield > 0) {
				yield.bonus += 1;
			}
		}

		private static void TilePenalty(Tile.Yield yield) {
			yield.penalty += yield.yield > 2 ? 1 : 0;
		}
	}
}
