using System.Collections.Generic;
using System.Linq;

namespace C7GameData.Save {

	public class SaveUnit : IHasID {
		public ID id { get; set; }
		public string name;
		public string nationality;
		public string prototype;
		public ID owner;
		public TileLocation previousLocation = new TileLocation();
		public TileLocation currentLocation;
		public List<TileLocation> path;
		public int hitPointsRemaining;
		public float movePointsRemaining;
		public string action; // "fortified"
		public TileDirection facingDirection = TileDirection.SOUTHEAST;
		public string experience;

		// The unit's barbarian tribe slot (spec 22 section 3.2.6), absent for
		// every unit that is not a barbarian.
		public int? barbarianTribeId;
		public float WorkerProgressTowardsJob;
		public ID WorkerJob;
		public ID loadedOnUnitId;

		// Great-leader state: which kind of leader this unit is (none for every
		// other unit) and whether it has already produced a leader.
		public MapUnit.LeaderKind leaderKind = MapUnit.LeaderKind.None;
		public bool hasProducedLeader;

		// True when this unit has already attacked this turn (Civ3's
		// USF_USED_ATTACK status bit). Persisted so that reloading a save in the
		// middle of a turn cannot hand the unit a second attack.
		public bool hasUsedAttack;

		// True for multiple types of automation, including worker automation
		// and automated exploring.
		public bool isAutomated;

		public SaveUnit() { }

		public SaveUnit(MapUnit unit) {
			id = unit.id;
			name = unit.name;
			nationality = unit.nationality.name;
			prototype = unit.unitType.name;
			owner = unit.owner.id;
			if (unit.previousLocation is not null) {
				previousLocation = new TileLocation(unit.previousLocation);
			}
			currentLocation = new TileLocation(unit.location);
			loadedOnUnitId = unit.loadedOnUnitId;
			leaderKind = unit.leaderKind;
			hasProducedLeader = unit.hasProducedLeader;
			hasUsedAttack = unit.hasUsedAttack;
			if (BarbarianTribes.IsValidSlot(unit.barbarianTribeId)) {
				barbarianTribeId = unit.barbarianTribeId;
			}
			if (unit.path?.PathLength() > 0) {
				path = unit.path.path.ToList().ConvertAll(tile => new TileLocation(tile));
			}
			hitPointsRemaining = unit.hitPointsRemaining;
			action = unit.isFortified ? "fortified" : "";
			isAutomated = unit.isAutomated;
			facingDirection = unit.facingDirection;
			experience = unit.experienceLevelKey;
			movePointsRemaining = unit.movementPoints.remaining;
			WorkerProgressTowardsJob = unit.WorkerProgressTowardsJob;
			WorkerJob = unit.WorkerJob?.Id;
		}


		public MapUnit ToMapUnit(List<UnitPrototype> prototypes, List<ExperienceLevel> experienceLevels, List<Player> players, List<Terraform> terraforms, GameMap map) {
			MapUnit unit = new MapUnit{
				id = id,
				unitType = prototypes.Find(p => p.name == prototype),
				experienceLevelKey = experience,
				experienceLevel = experienceLevels.Find(el => el.key == experience),
				owner = players.Find(player => player.id == owner),
				location = map.tileAt(currentLocation.X, currentLocation.Y),
				loadedOnUnitId = loadedOnUnitId,
				leaderKind = leaderKind,
				hasProducedLeader = hasProducedLeader,
				hasUsedAttack = hasUsedAttack,
				barbarianTribeId = barbarianTribeId ?? BarbarianTribes.None,
				previousLocation = currentLocation.X == - 1 ? Tile.NONE : map.tileAt(previousLocation.X, previousLocation.Y),
				hitPointsRemaining = hitPointsRemaining,
				movementPoints = new MovementPoints(),
				isFortified = action == "fortified",
				isAutomated = isAutomated,
				facingDirection = facingDirection,
				WorkerProgressTowardsJob = WorkerProgressTowardsJob,
				WorkerJob = WorkerJob == null ? null:terraforms.Find(tf => tf.Id == WorkerJob)
			};
			unit.location.unitsOnTile.Add(unit);
			unit.movementPoints.reset(movePointsRemaining);
			unit.name = string.IsNullOrEmpty(name) ? unit.unitType.name : name;

			Player originalNationality = players.Find(player => player.civilization.name == nationality);
			if (originalNationality != null) {
				unit.nationality = originalNationality.civilization;
			} else {
				unit.nationality = unit.owner.civilization;
			}

			return unit;
		}
	}
}
