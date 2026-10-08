namespace C7GameData;

/// Shared status class for easy abstractions
public class VictoryStatus {
	public Player Player { get; set; }
	public int CurrentTurn { get; set; }
	public float TurnScore { get; set; }
	public float Score { get; set; }

	// Domination: the counted tiles and population of the player's alliance,
	// and the map-wide totals they are measured against.
	public int OwnedTiles { get; set; }
	public int TotalTiles { get; set; }
	public int OwnedPopulation { get; set; }
	public int TotalPopulation { get; set; }

	// Cultural: the culture of the player's most cultured city, the player's
	// civilization-wide culture, the map-scaled requirement it must reach, and
	// the highest total of any rival.
	public int TopCityCulture { get; set; }
	public int CivilizationCulture { get; set; }
	public int CultureRequirement { get; set; }
	public int TopRivalCulture { get; set; }

	// Conquest and diplomatic: how many surviving civilizations share the
	// player's alliance, and how many survive in total.
	public int AllianceSize { get; set; }
	public int SurvivingCivs { get; set; }

	// Space race: the parts of the player's spaceship that are complete and
	// how many the rules require.
	public int SpaceshipPartsBuilt { get; set; }
	public int SpaceshipPartsRequired { get; set; }

	// Victory points: the player's (or the player's alliance's) total and the
	// limit the rules require to win.
	public int VictoryPoints { get; set; }
	public int VictoryPointLimit { get; set; }
}
