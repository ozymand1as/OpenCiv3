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

	// The two branches of Civ3's Unit_score_kill: an elite winner rolls for a
	// great leader, a non-elite winner rolls to promote, and any winner whose
	// type starts a Golden Age may open one. defeatedWasTheDefender selects the
	// leader's spawn tile, matching the "the defender was destroyed" call site.
	public void RollForCombatOutcome(MapUnit defeated, bool defeatedWasTheDefender) {
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
		army.movementPoints.reset(armyType.movement);

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
		float memberMovement = movementPoints.remaining;

		loadedOnUnitId = army.id;
		hitPointsRemaining = maxHitPoints;
		movementPoints.reset(0);
		isFortified = true;

		army.hitPointsRemaining = Math.Max(0, army.maxHitPoints - armyDamage - memberDamage);

		// The army moves as fast as its fastest member.
		army.movementPoints.reset(Math.Max(army.movementPoints.remaining, memberMovement));
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
