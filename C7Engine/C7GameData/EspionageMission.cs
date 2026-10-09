namespace C7GameData {
	// One row of the BIQ ESPN table: a mission an espionage agent can run.
	//
	// The mission id is the position in GameData.espionageMissions, which
	// matches the BIQ order the original engine dispatches on (mission 0 is
	// Build an Embassy, mission 8 is Expose Enemy Spy).
	public class EspionageMission {
		public string name;
		public string civilopediaEntry;

		// A diplomat may run this mission (ESPN flags bit 0).
		public bool diplomatAllowed;

		// A spy may run this mission (ESPN flags bit 1).
		public bool spyAllowed;

		// The mission's base cost. The full price feeds this value through the
		// per-mission formula and the shared scaling term (see
		// C7Engine/Espionage.cs).
		public int baseCost;
	}

	// One row of the BIQ CULT table: a cultural level, used to pick the
	// per-citizen chance of an Initiate Propaganda mission.
	public class CultureLevel {
		public string name;

		// The per-citizen chance that a propaganda mission subverts a citizen.
		public int chanceOfSuccessfulPropaganda;

		// The lowest own:target culture percentage (100 * own / target) that
		// reaches this level.
		public int cultureRatioPercentage;

		// The chance in one hundred that a citizen of a city captured from (or
		// by) this level's civ begins resisting, and the chance in one hundred
		// that a resisting citizen keeps resisting on a later turn (spec 17
		// section 7; BIQ CULT InitialResistanceChance / ContinuedResistanceChance).
		public int initialResistanceChance;
		public int continuedResistanceChance;
	}
}
