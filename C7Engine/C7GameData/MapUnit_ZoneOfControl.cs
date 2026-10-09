using System;
using C7Engine;

namespace C7GameData {
	/// <summary>
	/// The zone-of-control rule (11_movement.md §6): a unit that steps between
	/// two tiles which share an adjacent tile held by an enemy of the mover's
	/// owner can lose a hit point. The original fires it from the step executor
	/// only when the source and the destination are both water or both land
	/// (0x5b9416-0x5b9434), and before the step's cost is computed, so a step the
	/// executor then refuses still pays.
	/// </summary>
	public partial class MapUnit {
		/// <summary>
		/// Civ3's escape chance on the 1024 scale, from the mover's defence `D`
		/// and the winning threat strength `E`: `(D * 16384) / (D * 16 + E)`,
		/// clamped to 1..1023. The roll escapes (no damage) when it is below this
		/// value, so with D = E the chance is 963/1024 (§6.3).
		/// </summary>
		internal static int ZoneOfControlEscapeChance(int defence, int threat) {
			int escape = (defence * 16384) / (defence * 16 + threat);
			return Math.Clamp(escape, 1, 1023);
		}

		/// <summary>
		/// The strongest threat against a step from this unit's tile to
		/// <paramref name="destination"/>, or 0 when nothing threatens the step.
		/// Exposed so tests can pin the scan's candidate rules directly.
		/// </summary>
		internal int ZoneOfControlThreatStrength(Tile destination) {
			if (destination == null || destination == Tile.NONE || location == null || location == Tile.NONE)
				return 0;

			// §6.1: a mover with no defence strength can never be hit, and a
			// land- or sea-classed mover with fewer than two hit points left is
			// skipped before the scan. An air-classed mover passes both guards
			// and then collects nothing, because the candidate scan below only
			// runs for the land and the sea class.
			if (DefenseStrength() < 1)
				return 0;
			bool isSea = IsWaterUnit();
			bool isLand = IsLandUnit();
			if ((isLand || isSea) && hitPointsRemaining < 2)
				return 0;
			if (!isLand && !isSea)
				return 0;

			int strongest = 0;
			foreach (Tile candidateTile in location.neighbors.Values) {
				if (candidateTile == null || candidateTile == Tile.NONE)
					continue;

				// The candidate must also be adjacent to the destination, so the
				// scan covers exactly the tiles adjacent to both ends of the
				// step. The destination itself is at distance 0 and drops out.
				if (candidateTile.DistanceTo(destination) != 1)
					continue;

				if (isSea) {
					// A sea mover scans water tiles and any tile holding a city.
					if (!candidateTile.IsWater() && !candidateTile.HasCity())
						continue;
					strongest = Math.Max(strongest, CityThreatAgainst(candidateTile, this));
				} else if (candidateTile.IsWater()) {
					// A land mover scans land tiles only.
					continue;
				}

				foreach (MapUnit candidate in candidateTile.unitsOnTile) {
					if (isSea ? !candidate.IsWaterUnit() : !candidate.IsLandUnit())
						continue;
					strongest = Math.Max(strongest, candidate.ContributionToZoneOfControl(this));
				}
			}

			return strongest;
		}

		/// <summary>
		/// Fires the rule for a step from this unit's tile to
		/// <paramref name="destination"/>. Returns true when the mover lost a hit
		/// point. The caller decides whether the rule applies at all (the
		/// both-water-or-both-land gate and the ordering around a fight); this
		/// method implements the guards, the scan and the roll of §6.1-§6.3.
		/// </summary>
		public bool ApplyZoneOfControl(Tile destination) {
			int threat = ZoneOfControlThreatStrength(destination);
			if (threat <= 0)
				return false;

			int escape = ZoneOfControlEscapeChance(DefenseStrength(), threat);
			if (GameData.rng.Next(1024) < escape)
				return false;

			// The original increments a damage counter unconditionally; the
			// guard above keeps at least one hit point, so this never kills. The
			// winning candidate's direction is remembered there only to play the
			// animation, which the fork does not model.
			hitPointsRemaining -= 1;
			return true;
		}

		// What this unit contributes to a zone of control, or 0 when it
		// contributes nothing: the higher of its attack strength and its bombard
		// strength, with a cruise missile's bombard term zeroed, and only when
		// the candidate is eligible, hostile to the mover and visible to the
		// candidate's owner (§6.2).
		private int ContributionToZoneOfControl(MapUnit mover) {
			int bombard = (unitType.bombard > 0 && !unitType.isCruiseMissile) ? unitType.bombard : 0;
			int strength = Math.Max(AttackStrength(), bombard);
			if (strength <= 0)
				return 0;

			// The strength test above is also the original's gate on the three
			// position bypasses: only a candidate that carries attack or bombard
			// strength may use them (0x4a79fb-0x4a7a8e, 0x4a7ce3-0x4a7d76).
			if (!ProjectsZoneOfControl())
				return 0;

			if (owner == null || mover.owner == null)
				return 0;

			// The visibility direction is the mover's, not the candidate's: the
			// candidate's owner must be able to see the mover (0x4a7a9e for the
			// sea branch, 0x4a7d86 for the land one). The fork models that as
			// tile knowledge, so an enemy the owner has never seen does not fire.
			if (!owner.HasExploredTile(mover.location))
				return 0;
			if (owner.IsAtPeaceWith(mover.owner))
				return 0;

			return strength;
		}

		// Whether this candidate may project a zone of control at all. The
		// original requires the unit type's own ZoneOfControl field (+0x04,
		// 0x4a7a85 for a sea mover and 0x4a7d6d for a land one) unless the
		// candidate is inside an army, standing on a fortress, or a land unit
		// inside a small walled city.
		private bool ProjectsZoneOfControl() {
			if (unitType.projectsZoneOfControl)
				return true;

			if (IsCarriedInsideArmy())
				return true;
			if (location == null || location == Tile.NONE)
				return false;
			if (location.HasFortress())
				return true;
			return IsLandUnit() && location.HasCity() && HasWorkingWalls(location.cityAtTile);
		}

		// The city bypass's own test (at 0x437e60): the city is
		// still a town - its population is at most RULE.MaximumLevel1CitySize
		// (and at most MaximumLevel2CitySize) - and one of its buildings still
		// supplies a bombard defence, which is how the rules encode city walls.
		// A building whose obsolescence technology the owner knows is skipped,
		// like the original's loop over the rule's buildings.
		private static bool HasWorkingWalls(City city) {
			if (city == null || city.owner == null)
				return false;

			Rules rules = EngineStorage.gameData?.rules;
			int maximumLevel1 = rules?.MaximumLevel1CitySize ?? 6;
			int maximumLevel2 = rules?.MaximumLevel2CitySize ?? 12;
			if (city.residents.Count > maximumLevel1 || city.residents.Count > maximumLevel2)
				return false;

			foreach (CityBuilding cb in city.GetBuildings()) {
				if (!cb.building.providesWalls)
					continue;
				if (cb.building.renderedObsoleteBy != null
						&& city.owner.knownTechs.Contains(cb.building.renderedObsoleteBy.id))
					continue;
				return true;
			}

			return false;
		}

		// A hostile city's contribution to the zone of control a sea mover
		// passes through: the summed naval power of its live buildings
		// (City_sum_buildings_naval_power @ 0x4c1280). Cities have no unit-type
		// gate and no class filter; they are only examined for a sea mover.
		private static int CityThreatAgainst(Tile candidateTile, MapUnit mover) {
			City city = candidateTile.cityAtTile;
			if (city == null || city.owner == null || mover.owner == null)
				return 0;

			// The city's owner must be an enemy of the mover that can see it,
			// read in the same direction as the unit candidates.
			if (!city.owner.HasExploredTile(mover.location))
				return 0;
			if (city.owner.IsAtPeaceWith(mover.owner))
				return 0;

			int navalPower = 0;
			foreach (CityBuilding cb in city.GetBuildings()) {
				if (cb.building.renderedObsoleteBy != null
						&& city.owner.knownTechs.Contains(cb.building.renderedObsoleteBy.id))
					continue;
				navalPower += cb.building.navalPower;
			}
			return navalPower;
		}
	}
}
