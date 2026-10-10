using System;
using System.Collections.Generic;
using System.Linq;
using C7Engine;
using C7GameData.Save;

namespace C7GameData;

/// <summary>
/// The nuclear launch: the entry the bombard dispatch reaches for a unit type
/// that carries the ability 16 (NuclearWeapon) gate, plus the full 3x3
/// detonation, the once-per-detonation incident counter and the diplomacy
/// bookkeeping. Contract: 26_nuclear_weapons.md.
///
/// The gate is the unit-type ABILITY word (record +0x88, ability id 16,
/// tested at 0x5c0e4d in the shipped binary). It is NOT the AI-strategy word
/// (record +0x8c), a different field of the same record; nothing here reads the
/// strategy. The launch routine itself never tests distance - the human range
/// limit lives in the target-selection layer (see CanLaunchNuclearWeaponAt).
/// </summary>
public partial class MapUnit {
	// The incident counter the shipped engine keeps at the global 0xa52698,
	// incremented once per DETONATION (0x5b412f) and once per nuclear-plant
	// meltdown (0x4b4a26). Only the launch path writes it here; the meltdown
	// writer is a separate item. It feeds global warming's nukes-squared term.
	private void CountNuclearDetonation(GameData gameData) {
		gameData.nukesUsed++;
	}

	// A launch gets through a missile defence only on a roll of zero: a 1-in-4
	// chance, drawn as rand_int(4) (0x5b3d36) and tested against zero
	// (0x5b4029 offline). Kept as a pure predicate so the rule is directly
	// falsifiable without depending on a specific RNG draw.
	internal static bool InterceptionLetsNukeThrough(int roll) => roll == 0;

	// A struck city loses each eligible building on a roll of zero: rand_int(2)
	// (0x5b4163), so a 1-in-2 chance per unprotected building.
	internal static bool RollDestroysBuilding(int roll) => roll == 0;

	// A damaged unit survives only on a roll of zero: rand_int(3) (0x5b4786),
	// so a 1-in-3 chance. On any non-zero roll the unit is destroyed.
	internal static bool RollSparesDamagedUnit(int roll) => roll == 0;

	// The 3x3 blast block: the target tile and its eight neighbours, the
	// neighbour-index 0..8 set the claim scan and the detonation walk
	// (0x5b3c0a and 0x5b4194). Off-map neighbours are dropped, matching the
	// binary's per-tile wrap/exists test.
	private static List<Tile> NuclearBlastBlock(Tile target) {
		List<Tile> block = new() { target };
		foreach (Tile n in target.neighbors.Values) {
			if (n != null && n != Tile.NONE)
				block.Add(n);
		}
		return block;
	}

	// Civs that hold ground or a unit in the block, i.e. that were hit. The
	// claim scan records the tile owner and the occupier owner for each of the
	// nine tiles and skips player index 0 (barbarians) and the launcher.
	// Returned in ascending player order, which the first loop walks.
	private List<Player> NuclearBlockCivs(List<Tile> block, GameData gameData) {
		HashSet<Player> set = new();
		foreach (Tile t in block) {
			Player tileOwner = t.OwningPlayer();
			if (tileOwner != null && !tileOwner.isBarbarians)
				set.Add(tileOwner);
			foreach (MapUnit u in t.unitsOnTile) {
				if (u.owner != null && !u.owner.isBarbarians)
					set.Add(u.owner);
			}
		}
		set.Remove(owner);
		return gameData.players.Where(set.Contains).ToList();
	}

	// The per-pair reputation record of observer toward subject, creating the
	// relationship if needed so an increment persists. Returns null for a pair
	// the engine will not record (same civ, barbarians, defeated), matching the
	// guards in TryGetRelationship.
	private static DiplomaticReputation ReputationRecord(Player observer, Player subject) {
		if (observer == null || subject == null)
			return null;
		if (observer.isBarbarians || subject.isBarbarians || observer.id == subject.id
				|| observer.defeated || subject.defeated)
			return null;
		observer.EnsureRelationshipExists(subject);
		return observer.playerRelationships[subject.id].reputation;
	}

	// Whether the civ owns at least one building with the Strategic Missile
	// Defence flag (DecreasesMissileSuccess, BLDG Flags[8] bit 6): the
	// low-indexed owner in the block can intercept the launch.
	private static bool OwnsMissileDefence(Player civ) {
		return civ.cities.Any(c => c.GetBuildings().Any(cb => cb.building.decreasesMissileSuccess));
	}

	// The unit carrying this one, if any (the transport/army slot). An army's
	// members are the practical cargo this path cares about.
	private MapUnit CarrierOf() {
		if (loadedOnUnitId == null)
			return null;
		return EngineStorage.gameData.mapUnits.FirstOrDefault(u => u.id == loadedOnUnitId);
	}

	// The reputation event of one block civ (0x5b5600): a strike from a type
	// without hidden nationality declares war; hidden nationality skips the
	// declaration (a privateer-style raid is not an act of war). Either way the
	// event adds one to the victim's field_14 memory of the launcher (reputation
	// index 5, 0x5b577d). The war declaration pulls in the victim's peace/MPP
	// partners (0x5b56e6-0x5b5759).
	private void ReputationEventToward(Player victim, GameData gameData) {
		DiplomaticReputation rep = ReputationRecord(victim, owner);
		if (rep == null)
			return;
		if (!unitType.hasHiddenNationality) {
			owner.DeclareWarOn(victim, gameData.turn);
			foreach (Player partner in gameData.players
					.Where(p => p != owner && p != victim && !p.isBarbarians && !p.defeated
						&& PlayerRelationship.TryGetRelationship(p, victim, out PlayerRelationship rel)
						&& !PlayerRelationship.AtWar(p, victim))) {
				owner.DeclareWarOn(partner, gameData.turn);
			}
		}
		rep.field14++;
	}

	// The launch routine (0x5b3d10). Order: claim scan set, one interception
	// roll, the first player loop (war + reputation + missile-defence count,
	// breaking at the first defence owner), the second player loop (the icbm /
	// icbm_other counters, no break), then resolve: intercept when a defence
	// owner exists and the roll is not zero, otherwise detonate.
	public void LaunchNuclearWeapon(Tile target, GameData gameData = null, Func<int, int> randInt = null) {
		gameData ??= EngineStorage.gameData;
		randInt ??= n => GameData.rng.Next(n);

		List<Tile> block = NuclearBlastBlock(target);
		List<Player> blockCivs = NuclearBlockCivs(block, gameData);   // the claim scan (section 3.2)

		// The one roll the launch draws, before the missile-defence test.
		int interceptionRoll = randInt(4);

		// First loop: war + reputation + defence count over the block civs in
		// ascending player order, stopping at the first one that owns a missile
		// defence. Block civs after the defence owner get neither.
		Player sdiOwner = null;
		foreach (Player other in blockCivs) {
			ReputationEventToward(other, gameData);
			if (sdiOwner == null && OwnsMissileDefence(other)) {
				sdiOwner = other;
				break;
			}
		}

		// Second loop: every living other pair, no break. A civ that held ground
		// or a unit in the block remembers "I nuked you" (icbm); any other civ
		// that was not fighting a block civ remembers "you nuked someone I was
		// not fighting" (icbm_other). Running before resolve means an
		// interception still advances these counters (section 8.1).
		foreach (Player other in gameData.players.Where(p => p != owner && !p.isBarbarians && !p.defeated)) {
			DiplomaticReputation rep = ReputationRecord(other, owner);
			if (rep == null)
				continue;
			if (blockCivs.Contains(other)) {
				rep.icbm++;
			} else if (blockCivs.Any(v => !PlayerRelationship.AtWar(other, v))) {
				rep.icbmOther++;
			}
		}

		// The firing unit is consumed whether the launch gets through or not.
		RemoveFromPlay();

		bool intercepted = sdiOwner != null && !InterceptionLetsNukeThrough(interceptionRoll);
		if (intercepted) {
			// Shot down: no blast, no incident counter, no war-damage event.
			return;
		}

		CountNuclearDetonation(gameData);
		Detonate(target, block, blockCivs, gameData, randInt);
	}

	// The detonation (0x5b4070). The counter has already been incremented by the
	// caller (section 5.2, once per weapon that actually goes off).
	private void Detonate(Tile target, List<Tile> block, List<Player> blockCivs, GameData gameData, Func<int, int> randInt) {
		// 5.3 - the target city loses buildings.
		if (target.HasCity(out City targetCity)) {
			foreach (CityBuilding cb in targetCity.GetBuildings().ToList()) {
				if (IsProtectedFromNuke(cb.building))
					continue;
				if (RollDestroysBuilding(randInt(2)))
					targetCity.RemoveBuilding(cb);
			}
		}

		// Per block tile: 5.4 population, 5.5 tile objects, 5.6 improvements and
		// pollution, 5.7 units, 5.8 terrain.
		foreach (Tile t in block) {
			// 5.4 - population. The aimed tile loses floor(pop/2); every other
			// block tile loses 1. A size-1 city is never touched, so this can
			// never drop a city below one.
			if (t.HasCity(out City c) && c.residents.Count > 1) {
				bool isAimedTile = t.XCoordinate == target.XCoordinate && t.YCoordinate == target.YCoordinate;
				int loss = isAimedTile ? c.residents.Count / 2 : 1;
				c.RemoveCitizens(loss);
			}

			// 5.5 - tile objects. The barbarian camp is the one modelled here;
			// the colony/airfield/radar-tower/outpost tile buildings have no
			// separate state in the engine yet.
			if (t.hasBarbarianCamp) {
				t.hasBarbarianCamp = false;
				t.barbarianTribeId = BarbarianTribes.None;
			}

			// 5.6 - improvements and pollution. Road/railroad only where no city
			// is present; mine/irrigation/fortress/goody-hut always; one pollution
			// marker on every non-city land tile.
			if (!t.HasCity()) {
				t.RemoveImprovementAtLayer(TerrainImprovement.Layer.Roads);
			}
			t.RemoveImprovementAtLayer(TerrainImprovement.Layer.ResourceDevelopment);
			RemoveFortress(t);
			t.hasGoodyHut = false;
			if (!t.HasCity() && !t.IsWater()) {
				Tile.TryAddPollution(t);
			}

			// 5.7 - units are killed, damaged or spared.
			DamageUnitsOn(t, randInt, gameData);

			// 5.8 - terrain change.
			ChangeTerrainForNuke(t, gameData);
		}

		// 5.9 - war-damage memory: every block civ that was hit (not the launcher)
		// takes 8 against both its war-damage counters (0x5631b0).
		foreach (Player hit in blockCivs) {
			DiplomaticReputation rep = ReputationRecord(hit, owner);
			if (rep == null)
				continue;
			rep.warDamageMemory += 8;
			rep.warDamageThisTurn += 8;
		}

		// 5.9 - the block is revealed to the launcher's observer.
		foreach (Tile t in block) {
			if (t != Tile.NONE)
				owner.tileKnowledge.knownTiles.Add(t);
		}
	}

	// Improvements a nuke can never destroy (0x4b3290 protection test): great
	// wonders, small wonders, the Palace, the Aqueduct and the Hospital.
	private static bool IsProtectedFromNuke(Building b) {
		return b.IsGreatWonder() || b.isSmallWonder
			|| b.isCenterOfEmpire || b.allowsCitySize2 || b.allowsCitySize3;
	}

	// Clears the fortress overlay (overlay bit 4) without disturbing a
	// barricade, which shares the holdings layer but is not a nuke target.
	private static void RemoveFortress(Tile t) {
		TerrainImprovement holdings = t.overlays.ImprovementAtLayer(TerrainImprovement.Layer.Holdings);
		if (holdings != null && holdings.key == Tile.TileOverlays.FORTRESS) {
			t.overlays.Remove(holdings);
		}
	}

	// 5.7 - the two unit rules. Air units based on a tile that carries an
	// airfield are destroyed outright (the airfield itself is not modelled, so
	// they are handled by the general rule here). Every other unit: a nuclear
	// type is spared; army cargo is spared; a defence-0 unit is destroyed;
	// a unit at one remaining hit point is destroyed; otherwise a 1-in-3 roll
	// leaves it alive at half hit points.
	private void DamageUnitsOn(Tile t, Func<int, int> randInt, GameData gameData) {
		foreach (MapUnit u in t.unitsOnTile.ToList()) {
			if (u == this)
				continue;
			if (u.unitType.isNuclearWeapon)
				continue;

			MapUnit carrier = u.CarrierOf();
			if (carrier != null && carrier.unitType.isArmy)
				continue;

			if (u.DefenseStrength() <= 0) {
				u.RemoveFromPlay();
				continue;
			}

			int remaining = u.hitPointsRemaining;
			if (remaining <= 1) {
				u.RemoveFromPlay();
				continue;
			}

			if (!RollSparesDamagedUnit(randInt(3))) {
				u.RemoveFromPlay();
			} else {
				// Survivor: half of what it had, rounded down, at least one. The
				// state reset of 0x5b3040 (except states 1 and 15) has no engine
				// field yet (open item O4) and is omitted.
				u.hitPointsRemaining = Math.Max(1, remaining / 2);
			}
		}
	}

	// 5.8 - the terrain change. The pollution-effect target of the tile's
	// terrain (the 14/0xe sentinel resolves to the tile's underlying terrain);
	// a target of -1 or of 4 or more changes nothing, otherwise the tile's
	// visible terrain becomes the target terrain (0x5e9a60, 0x5d59e0).
	private static void ChangeTerrainForNuke(Tile t, GameData gameData) {
		int effect = t.overlayTerrainType.pollutionEffect;
		int resolved = effect == TerrainType.UnderlyingTerrainPollutionEffect
			? TerrainType.Civ3TerrainIdForKey(t.baseTerrainType.Key)
			: effect;
		if (resolved < 0 || resolved >= 4)
			return;
		string key = TerrainType.KeyForCiv3TerrainId(resolved);
		TerrainType targetTerrain = key == null ? null : gameData.terrainTypes.Find(x => x.Key == key);
		if (targetTerrain == null)
			return;
		t.overlayTerrainType = targetTerrain;
	}

	// The human target-validity gate for the nuclear action (section 4.3). The
	// launch routine never tests distance; this is the target-selection layer,
	// so the AI's ICBM is unaffected. A nuclear weapon is range-limited to its
	// type's BombardRange by the Civ3 metric floor((xDist + yDist)/2), and a
	// type with ability 20 (ICBM) is exempt (unlimited range), exactly as the
	// UI arms the action with -1 for an ICBM (0x4d963d, 0x4d968c).
	public bool CanLaunchNuclearWeaponAt(Tile target) {
		if (!unitType.isNuclearWeapon)
			return false;
		if (unitType.isICBM)
			return true;
		return location.DistanceTo(target) <= unitType.bombardRange;
	}

	// Whether the two civs are at peace in the sense the target-validity test
	// wants (26_nuclear_weapons.md section 3.3): they have met and are not at
	// war, which is how the engine carries the peace treaty bit.
	internal static bool AreAtPeace(Player one, Player other) {
		return PlayerRelationship.TryGetRelationship(one, other, out PlayerRelationship rel) && !rel.AtWar();
	}
}
