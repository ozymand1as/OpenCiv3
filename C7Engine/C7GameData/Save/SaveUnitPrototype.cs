using System.Collections.Generic;
using System.Linq;

namespace C7GameData.Save {
	public class SaveUnitPrototype {
		public enum Flag {
			RotateBeforeAttack,
			CanCarryFootUnitsOnly,
			CanCarryAircraft,
			CanCarryTacticalMissiles,
			LethalLandBombardment,
			LethalSeaBombardment,
			Radar,
			Army,
			Leader,
			StartsGoldenAge,
			// The King ability (PRTO Flags1[3] bit 5): the ruler unit of
			// regicide-style scenarios. The binary's defender picker prefers a
			// King unit when choosing who defends a tile (12_combat.md §6.3).
			King,
			// The Blitz ability (PRTO Flags1[0] bit 2): the type may attack more
			// than once per turn, limited only by its movement points. The attack
			// availability test refuses a second attack to every other type
			// (23_leaders_armies_golden_age.md §6.8).
			Blitz,
			// The Amphibious ability (PRTO Flags1[0] bit 6): the type may attack
			// from water onto land, and gets the +25% amphibious assault bonus
			// (12_combat.md §2.1.2).
			Amphibious,
			// The Wheeled ability (PRTO Flags1[0] bit 0): the type may not enter
			// terrain whose ImpassableByWheeled flag is set unless the tile carries
			// an improvement that lifts the restriction (11_movement.md §4.1).
			Wheeled,
		}

		// The AI-strategy bitmask of the Civ3 PRTO record (BIQ Flags1[4..6]). The
		// original engine stores it as one scalar per unit type and dispatches on
		// it: it is the key the AI uses to decide what a unit is for. Every shipped
		// unit type has exactly one of these set; the storage permits overlaps.
		public enum AIStrategy {
			Offense,
			Defense,
			Artillery,
			Explore,
			Army,
			CruiseMissile,
			AirBombard,
			AirDefense,
			NavalPower,
			AirTransport,
			NavalTransport,
			NavalCarrier,
			Terraform,
			Settle,
			Leader,
			TacticalNuke,
			ICBM,
			NavalMissileTransport,
			FlagUnit,
			King,
		}

		public string name { get; set; }
		public Art art { get; set; }
		public int shieldCost { get; set; }
		public int populationCost { get; set; }
		public ID requiredTech { get; set; }
		public int attack { get; set; }
		public int defense { get; set; }
		public int bombard { get; set; }
		public int bombardRange { get; set; }
		public int rateOfFire { get; set; }
		public int movement { get; set; }
		public int capacity { get; set; }
		public int hpBonus { get; set; }

		public HashSet<string> producibleBy = [];

		public List<string> upgradesTo;
		public bool unproducible;

		// Assorted boolean flags for the unit prototype. They're stored in
		// this set rather than as booleans to avoid bloating the json file.
		public HashSet<Flag> flags = [];

		// The AI-strategy bits of this unit type. Stored as a set for the same
		// reason as the flags above; the shipped rules put exactly one bit here.
		public HashSet<AIStrategy> aiStrategies = [];

		public HashSet<string> categories = new HashSet<string>();

		public HashSet<UnitAction> actions = [];

		public HashSet<string> attributes = new HashSet<string>();

		public HashSet<string> requiredResources = [];

		public HashSet<ID> terraformActions = [];

		public SaveUnitPrototype() { }

		public SaveUnitPrototype(UnitPrototype proto) {
			(name, art, shieldCost, populationCost, unproducible,
			attack, defense, bombard, bombardRange, rateOfFire, movement, capacity, hpBonus) =
			(proto.name, proto.art, proto.shieldCost, proto.populationCost, proto.unproducible,
			 proto.attack, proto.defense, proto.bombard, proto.bombardRange, proto.rateOfFire, proto.movement,
			 proto.capacity, proto.hpBonus);

			if (proto.requiredTech != null)
				requiredTech = proto.requiredTech.id;

			if (proto.upgradesTo != null)
				upgradesTo = proto.upgradesTo?.Select(x => x.name).OrderBy(x => x).ToList() ?? [];

			categories = new HashSet<string>(proto.categories);
			actions = proto.actions;
			attributes = new HashSet<string>(proto.attributes);
			flags = new HashSet<Flag>(proto.flags);
			aiStrategies = new HashSet<AIStrategy>(proto.aiStrategies);

			requiredResources = proto.requiredResources.Select(r => r.Key).ToHashSet();
			terraformActions = proto.terraformActions.Select(r => r.Id).ToHashSet();
			producibleBy = proto.producibleBy.Select(r => r.name).ToHashSet();
		}
	}
}
