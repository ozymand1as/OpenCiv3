namespace C7GameData {
	public class CityResident {
		public CitizenType citizenType;

		// Only relevant if citizenType.IsDefaultCitizen == true
		public Tile tileWorked = Tile.NONE;
		public Civilization nationality;
		public City city;

		// Civ3's per-citizen resistance flag (spec 17 section 7): a citizen of
		// a captured city may resist the new owner. Resisting citizens are
		// excluded from the city's commerce, stop working their tile, and are
		// quelled by the garrison over the following turns.
		public bool isResisting;

		// Only relevant if citizenType.IsDefaultCitizen == true
		public enum Mood {
			Happy,
			Content,
			Unhappy
		};
		public Mood mood;
	}
}
