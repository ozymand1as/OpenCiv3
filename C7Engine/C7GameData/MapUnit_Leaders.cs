using System;
using System.Collections.Generic;
using System.Linq;
using C7Engine;

namespace C7GameData;

// Great leaders, armies, and the Golden Age.
//
// Civ3 creates a great leader from one of two rolls (an elite victory and a
// first discovery), lets a military leader form an army or rush production and a
// scientific leader start the Age of Science, and opens a one-per-game Golden
// Age when a unique unit wins a battle or the right Great Wonders are built.
// See re/specs/23_leaders_armies_golden_age.md.
public partial class MapUnit {
	// The two kinds of great leader are the same unit type; this byte on the
	// unit says which kind it is.
	public enum LeaderKind {
		None = 0,
		Military = 1,
		Scientific = 2,
	}

	public LeaderKind leaderKind { get; set; } = LeaderKind.None;

	// Set when this unit has produced a great leader. A given unit can only do
	// that once, and the UI marks such a unit with a "*".
	public bool hasProducedLeader { get; set; }

	// The military leader roll's denominators: 1/16 normally, 1/12 when the civ
	// owns a building with IncreasesLeaderChance (the Heroic Epic).
	private const int LEADER_ROLL_BASE = 16;
	private const int LEADER_ROLL_WITH_EPIC = 12;

	public bool IsArmy => unitType.isArmy;

	public bool IsGreatLeader => unitType.isLeader;

	// The experience level the engine treats as elite: the last one. Only elite
	// units can produce a great leader; everyone else rolls to promote.
	public bool IsElite() {
		return experienceLevel != null && EngineStorage.gameData.GetExperienceLevelAfter(experienceLevel) == null;
	}

	// The units this army is carrying. They share its tile and point at it as
	// their container.
	public List<MapUnit> Members() {
		if (!IsArmy || location == null) {
			return [];
		}
		return location.unitsOnTile.Where(u => u.loadedOnUnitId == id).ToList();
	}

	// Civ3's Unit_get_max_move_points army branch (0x5be470): an army's maximum
	// movement is the SMALLEST maximum among the units whose container is the
	// army, plus RULE.MovementAlongRoads internal units. The comparison at
	// 0x5be528 keeps the running value whenever it is the smaller of the two, so
	// the branch takes the minimum and an army moves at its SLOWEST member's
	// rate, plus one point (11_movement.md §2.2, gap G11).
	//
	// The running value starts at -1 and the "+ MovementAlongRoads" happens
	// before the empty test (0x5be569-0x5be573), so an army with no members
	// returns MovementAlongRoads - 1 internal units - two thirds of a movement
	// point at the shipped scale - and NOT its own type's movement rate
	// (11_movement.md §2.2).
	//
	// C7 counts movement in movement points - TilePath divides an improvement's
	// internal cost by RULE.MovementAlongRoads - so the arithmetic below is done
	// in internal units and converted once. That keeps the one-point bonus exact
	// and the empty-army shortfall of one internal unit exact at any scale. [C]
	public float MaxMovementPoints {
		get {
			int scale = MovementScale;
			if (IsArmy) {
				float running = -1f;  // internal units, as in the binary
				foreach (MapUnit member in Members()) {
					float memberMax = member.MaxMovementPoints * scale;
					if (running == -1f || memberMax < running) {
						running = memberMax;
					}
				}
				return (running + scale) / scale;
			}

			// get_max_move_points (0x5cddf0) runs entirely in internal units:
			// base = UnitType.Movement * MovementAlongRoads, then each sea-only
			// bonus is added as a multiple of the SAME scale, and only the C7
			// boundary turns internal units back into movement points. Doing the
			// arithmetic here in internal units is what makes each bonus a WHOLE
			// movement point at any scale, rather than one internal unit (which
			// would be a third of a point at the shipped scale of 3)
			// (11_movement.md §2.2).
			int internalUnits = (unitType.movement + SeaMovementBonusPoints) * scale;
			return internalUnits / (float)scale;
		}
	}

	// Civ3's movement scale, RULE.MovementAlongRoads: the number of internal
	// movement units in one whole movement point.
	private int MovementScale => Math.Max(1, owner?.rules?.MovementAlongRoads
		?? EngineStorage.gameData?.rules?.MovementAlongRoads
		?? Rules.DefaultMovementAlongRoads);

	// The three sea-only movement bonuses of Civ3's get_max_move_points
	// (0x5cddf0), each expressed in whole movement points:
	//
	//   +1   the civ owns a wonder with wonder-feature flag 0x8
	//        (ITW_Plus_One_Ship_Movement)
	//   +2   the civ owns a wonder with wonder-feature flag 0x4000
	//        (ITW_Plus_Two_Ship_Movement)
	//   +1   the civ's race has trait bit 7 (Seafaring)
	//
	// Each wonder flag is a BOOLEAN, not a per-wonder total: the test is
	// "count > 0" (0x5cde39-0x5cde3d for 0x8, 0x5cde51-0x5cde5b for 0x4000),
	// so a second carrier of the same flag adds nothing, while the two
	// different flags each apply their own amount.
	//
	// All three sit behind the same two guards, both read at 0x5cde03-0x5cde12
	// before the first bonus: the unit type's class field at unit-type +0x9C
	// must be 1 (sea and nothing else), and the civ id must be greater than 0,
	// which excludes the barbarian player. The wonder flags are counted with
	// Leader_count_wonders_with_flag (0x55a8d0), which counts only wonder-class
	// buildings whose flag is set and that are not obsolete for the leader, so
	// the flags do nothing on an ordinary building (11_movement.md §2.2).
	private int SeaMovementBonusPoints {
		get {
			// Unit_Class == 1: sea units only.
			if (!IsWaterUnit()) {
				return 0;
			}
			// civId > 0: the barbarian player is civ 0 and gets nothing. A unit
			// with no owning civilization has no civ to award a bonus to either.
			if (owner?.civilization == null || owner.isBarbarians) {
				return 0;
			}

			int bonus = 0;
			if (owner.OwnsBuildingWhere(b => b.plusOneShipMovement && CountsForWonderFlag(b))) {
				bonus += 1;
			}
			if (owner.OwnsBuildingWhere(b => b.plusTwoShipMovement && CountsForWonderFlag(b))) {
				bonus += 2;
			}
			if (owner.civilization.traits.Contains(Civilization.Trait.Seafaring)) {
				bonus += 1;
			}
			return bonus;
		}
	}

	// Leader_count_wonders_with_flag accepts a building only when its
	// characteristic word at building +0xF0 carries ITC_Wonder (0x4) or
	// ITC_Small_Wonder (0x8) (0x55a930-0x55a93f), and it skips a wonder whose
	// obsoleting advance the leader already knows (0x55a952-0x55a97e).
	//
	// The trade network's safe-sea-travel helper gets the same two properties
	// for free from Player.GetActiveWonders, except that that list only ever
	// contains GREAT wonders, while the binary accepts a small wonder carrying
	// the flag as well - which is the case the Mesoamerican Caracol Observatory
	// exercises.
	private bool CountsForWonderFlag(Building building) {
		return (building.IsGreatWonder() || building.isSmallWonder)
			&& !building.isGreatWonderObsolete(owner);
	}

	// The container this unit is loaded into, if any.
	private MapUnit Container() {
		if (!IsLoaded() || location == null) {
			return null;
		}
		return location.unitsOnTile.FirstOrDefault(u => u.id == loadedOnUnitId);
	}

	/// Whether this unit is carried by an army, as opposed to an ordinary
	/// transport. Civ3 sets its victorious-army status bit when a unit in this
	/// position wins a battle.
	public bool IsCarriedInsideArmy() {
		return Container()?.IsArmy == true;
	}

	// The two branches of Civ3's Unit_score_kill: an elite winner rolls for a
	// great leader, a non-elite winner rolls to promote, and any winner whose
	// type starts a Golden Age may open one. defeatedWasTheDefender selects the
	// leader's spawn tile, matching the "the defender was destroyed" call site.
	public void RollForCombatOutcome(MapUnit defeated, bool defeatedWasTheDefender) {
		// A unit carried inside an army that wins a battle marks its civ: Civ3
		// sets LSF_HAS_VICTORIOUS_ARMY (leader +0x40 bit 0) in Unit_score_kill for
		// any winner whose container is an army. The bit is a one-way latch.
		if (IsCarriedInsideArmy()) {
			owner.hasVictoriousArmy = true;
		}

		if (IsElite()) {
			RollForMilitaryLeader(defeated, defeatedWasTheDefender);
		} else {
			RollToPromote(defeated);
		}
		MaybeStartGoldenAge(defeated);
	}

	/// The post-victory military great leader roll.
	public void RollForMilitaryLeader(MapUnit defeated, bool defeatedWasTheDefender) {
		// Only land victories, only against non-barbarians, only for a unit that
		// has not already produced a leader, only for a unit that is not itself
		// carried by an army, and only while the civ has no leader at all.
		if (!IsLandUnit()) return;
		if (defeated.owner.isBarbarians) return;
		if (hasProducedLeader) return;
		if (IsLoaded()) return;
		if (owner.HasGreatLeader()) return;

		int denominator = owner.OwnsBuildingWhere(b => b.increasesLeaderChance)
			? LEADER_ROLL_WITH_EPIC
			: LEADER_ROLL_BASE;
		if (GameData.rng.Next(denominator) != 0) return;

		hasProducedLeader = true;

		// A leader usually appears with the winning unit, but when the defeated
		// unit was alone on its tile the leader appears there instead.
		Tile spawnTile = location;
		if (defeatedWasTheDefender && defeated.location.unitsOnTile.Count == 1) {
			spawnTile = defeated.location;
		}
		owner.CreateGreatLeader(LeaderKind.Military, spawnTile);
	}

	/// Whether this scientific leader may start the Age of Science: it must be a
	/// scientific leader standing in a city, and no Age of Science may already be
	/// running.
	public bool CanStartScienceAge() {
		if (!unitType.isLeader) return false;
		if (leaderKind != LeaderKind.Scientific) return false;
		if (location == null || !location.HasCity()) return false;
		return !owner.AgeOfScienceActive;
	}

	/// Start the owner's Age of Science. The leader is consumed.
	public bool StartScienceAge() {
		if (!CanStartScienceAge()) return false;
		owner.StartAgeOfScience();
		RemoveFromPlay();
		return true;
	}

	// A leader's rush applies a shield-cost floor only when the caller asks for
	// one; Civ3's own availability test and its AI path both pass zero.
	public const int MilitaryLeaderRushMinimumShields = 100;
	public const int ScientificLeaderRushMinimumShields = 200;

	/// Whether this leader may rush the city's current production. The leader
	/// must stand in one of its own cities. A leader rushes buildings only (never
	/// a unit), and a military leader may not rush a Great Wonder. When
	/// requireMinimumShieldCost is set the caller is asking for Civ3's shield
	/// floor - 100 for a military leader, 200 for a scientific one - which
	/// refuses cheaper items.
	public bool CanHurryProduction(bool requireMinimumShieldCost = false) {
		if (!unitType.isLeader) return false;
		if (location == null || !location.HasCity()) return false;

		City city = location.cityAtTile;
		if (city.owner != owner) return false;
		if (city.itemBeingProduced is not Building building) return false;

		switch (leaderKind) {
			case LeaderKind.Military:
				// Improvements and small wonders; a Great Wonder is refused.
				if (building.IsGreatWonder()) return false;
				break;
			case LeaderKind.Scientific:
				// Any improvement, small wonder or Great Wonder.
				break;
			default:
				// A kind of unset is a wildcard over items that look like
				// buildings: a Great or Small Wonder, or one that needs another
				// building first.
				if (!building.IsGreatWonder() && !building.isSmallWonder && building.requiredBuilding == null) return false;
				break;
		}

		if (requireMinimumShieldCost) {
			int minimum = leaderKind switch {
				LeaderKind.Military => MilitaryLeaderRushMinimumShields,
				LeaderKind.Scientific => ScientificLeaderRushMinimumShields,
				_ => 0,
			};
			if (owner.ShieldCost(building) < minimum) return false;
		}

		return true;
	}

	/// Rush the city's current production. The item's own shield cost is placed
	/// in the production box, so the item completes at the owner's next
	/// production phase; no gold is paid and no citizen is lost, unlike the
	/// ordinary hurry. The leader is consumed. Civ3 also sets a per-city "rushed"
	/// status bit at the same time; C7 has no city status word to hold it.
	public bool HurryProduction(bool requireMinimumShieldCost = false) {
		if (!CanHurryProduction(requireMinimumShieldCost)) return false;

		City city = location.cityAtTile;
		city.SetStoredShields(owner.ShieldCost(city.itemBeingProduced));
		RemoveFromPlay();
		return true;
	}

	/// Whether this leader may form an army: it must be a military (or unset)
	/// leader standing in one of its owner's cities, and the civ must have
	/// enough cities to support another army.
	public bool CanFormArmy() {
		if (!unitType.isLeader) return false;
		if (leaderKind == LeaderKind.Scientific) return false;
		if (location == null || !location.HasCity() || location.cityAtTile.owner != owner) return false;
		if (!EngineStorage.gameData.unitPrototypes.Any(p => p.isArmy)) return false;

		int citiesNeeded = owner.rules.CitiesNeededToSupportAnArmy * (owner.ArmyCount() + 1);
		return owner.RemainingCities() >= citiesNeeded;
	}

	/// Turn this leader into an army on its tile. The leader is consumed.
	public MapUnit FormArmy() {
		if (!CanFormArmy()) return null;

		UnitPrototype armyType = EngineStorage.gameData.unitPrototypes.First(p => p.isArmy);
		GameData gameData = EngineStorage.gameData;
		MapUnit army = armyType.GetInstance(gameData.GenerateID(armyType.name), armyType, owner, nationality: nationality, location: location);
		army.experienceLevelKey = gameData.defaultExperienceLevelKey;
		army.experienceLevel = gameData.defaultExperienceLevel;
		army.hitPointsRemaining = army.maxHitPoints;
		army.movementPoints.reset(army.MaxMovementPoints);

		location.unitsOnTile.Add(army);
		gameData.mapUnits.Add(army);
		owner.AddUnit(army);

		RemoveFromPlay();
		return army;
	}

	// An army's capacity is its unit type's Capacity field, plus one while the
	// civ owns a building with AllowsLargerArmies (the Pentagon).
	public int GetTransportCapacity() {
		int capacity = unitType.capacity;
		if (IsArmy && owner.OwnsBuildingWhere(b => b.allowsLargerArmies)) {
			capacity++;
		}
		return capacity;
	}

	/// Whether this unit may be loaded into the given army.
	public bool CanLoadIntoArmy(MapUnit army) {
		if (army == null || !army.IsArmy) return false;
		if (army == this) return false;
		if (army.owner != owner) return false;
		if (army.location != location) return false;
		if (IsLoaded()) return false;
		if (IsArmy) return false;
		if (!IsLandUnit()) return false;
		return army.Members().Count < army.GetTransportCapacity();
	}

	/// Load this unit into an army. Returns whether it was loaded.
	public bool LoadIntoArmy(MapUnit army) {
		if (!CanLoadIntoArmy(army)) return false;

		// Hit points are pooled: the army accumulates the members' damage and
		// the member's own damage is zeroed.
		int armyDamage = army.maxHitPoints - army.hitPointsRemaining;
		int memberDamage = maxHitPoints - hitPointsRemaining;

		// Civ3 folds the member's SPENT movement into the army's spent movement
		// (Unit_load_into_army @ 0x5bcc90: army.Moves = max(army.Moves,
		// member.Moves)), and the army's maximum is recomputed on demand from its
		// members. A load therefore never hands the army more movement than the
		// aggregation allows, and a slow member slows the army down at once. C7
		// stores remaining movement, so the same rule is applied to the spent
		// values (11_movement.md §2.2).
		float armySpent = Math.Max(0f, army.MaxMovementPoints - army.movementPoints.remaining);
		float memberSpent = Math.Max(0f, MaxMovementPoints - movementPoints.remaining);

		loadedOnUnitId = army.id;
		hitPointsRemaining = maxHitPoints;
		movementPoints.reset(0);
		isFortified = true;

		army.hitPointsRemaining = Math.Max(0, army.maxHitPoints - armyDamage - memberDamage);
		army.movementPoints.reset(Math.Max(0f, army.MaxMovementPoints - Math.Max(armySpent, memberSpent)));
		army.Wake();

		return true;
	}

	/// Open this unit's owner's Golden Age if this victory qualifies. Both a
	/// unique unit and an army carrying one trigger it; a barbarian victim never
	/// does.
	public void MaybeStartGoldenAge(MapUnit defeated) {
		if (defeated.owner.isBarbarians) return;
		if (owner.HasHadGoldenAge) return;

		bool triggers = unitType.startsGoldenAge
			|| (IsArmy && Members().Any(m => m.unitType.startsGoldenAge));
		if (triggers) {
			owner.StartGoldenAge();
		}
	}
}
