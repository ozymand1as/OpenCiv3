using C7GameData;

namespace C7Engine {
	/// <summary>
	/// Dispersing a barbarian camp (spec 22 section 6.3).
	///
	/// The original has one handler for this, at <c>0x565a00</c>, reached from
	/// two places: the unit-movement path, when a non-barbarian unit ends its
	/// move on a camp tile, and the territory owner writer, when a border
	/// change gives the camp's tile to a civilization. The reward follows the
	/// receiver, not the path: on the movement path it is the moving unit's
	/// owner, on the ownership path the civ that took the tile, which the
	/// writer passes as the new owner's leader object rather than a unit.
	/// </summary>
	public static class BarbarianCampInteractions {
		/// <summary>
		/// The gold a dispersed camp pays. The message text confirms the
		/// amount: "We dispersed a $BARBARIAN0 encampment and took 25 gold!".
		/// </summary>
		public const int DispersalGold = 25;

		/// <summary>
		/// Removes the camp on <paramref name="tile"/> and pays
		/// <paramref name="receiver"/> for it. The tribe name is read before
		/// the camp is cleared, because the freed slot stops naming this camp
		/// as soon as the tile's tribe id is reset.
		/// </summary>
		public static void DisperseCamp(GameData gameData, Tile tile, Player receiver) {
			string tribe = gameData.BarbarianTribeName(tile.barbarianTribeId);
			gameData.map.barbarianCamps.Remove(tile);
			tile.hasBarbarianCamp = false;
			tile.barbarianTribeId = BarbarianTribes.None;

			// TODO: make this configurable
			receiver.gold += DispersalGold;
			if (receiver.isHuman) {
				new MsgShowMilitaryAdvisorPopup(
					tribe == null
						? $"We cleared a barbarian encampment and earned {DispersalGold} gold!"
						: $"We dispersed a {tribe} encampment and took {DispersalGold} gold!",
					happy: true).send();
			}
		}
	}
}
