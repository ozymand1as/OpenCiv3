using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace C7GameData;

public class VictoryConditions {
	// The flags Civ3 reads from the BIQ GAME section. The shipped "civ3"
	// ruleset turns on the five standard conditions; a scenario or save
	// overrides every one of these from its own GAME section.
	public bool AllowDominationVictory { get; set; }
	public bool AllowSpaceRaceVictory { get; set; }
	public bool AllowDiplomaticVictory { get; set; }
	public bool AllowConquestVictory { get; set; }
	public bool AllowCulturalVictory { get; set; }
	public bool AllowWonderVictory { get; set; }
	public bool CityElimination { get; set; }
	public bool Regicide { get; set; }
	public bool MassRegicide { get; set; }
	public bool VictoryLocations { get; set; }
	public bool CaptureTheFlag { get; set; }
	public bool ReverseCaptureTheFlag { get; set; }

	// The numeric victory parameters from the BIQ GAME section. Civ3 installs
	// the defaults below when a BIQ stores 0, so a ruleset that leaves them
	// out still behaves like the shipped game.
	public int DominationTerrain { get; set; } = 66;
	public int DominationPopulation { get; set; } = 66;
	public int OneCityCultureWin { get; set; } = 20000;
	public int AllCitiesCultureWin { get; set; } = 100000;
	public int VictoryPointLimit { get; set; } = 50000;
	public int WonderCost { get; set; } = 10;
	public int DefeatingOpposingUnitCost { get; set; } = 5;
	public int AdvancementCost { get; set; } = 5;
	public int CityConquestPopulation { get; set; } = 100;
	public int VictoryPointScoring { get; set; } = 25;
	public int CapturingSpecialUnit { get; set; } = 1000;

	// Civ3 gates every victory-point award and the type-8 condition on one
	// three-bit group rather than on VictoryLocations alone: the test is
	// `p_toggleable_rules & 0x26000`, i.e. victory-point scoring (0x2000),
	// capture the princess (0x4000) and reverse capture the flag (0x20000).
	// Derived rather than stored, so it stays out of the saved rules.
	[JsonIgnore]
	public bool VictoryPointsEnabled {
		get => VictoryLocations || CaptureTheFlag || ReverseCaptureTheFlag;
	}

	// How many of each spaceship part a player must build to launch. Civ3's
	// shipped rules require one of each of the ten parts.
	public List<int> SpaceshipPartsNeeded { get; set; } = [1, 1, 1, 1, 1, 1, 1, 1, 1, 1];
}
