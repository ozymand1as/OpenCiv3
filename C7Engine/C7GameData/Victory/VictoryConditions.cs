using System.Collections.Generic;

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

	// How many of each spaceship part a player must build to launch. Civ3's
	// shipped rules require one of each of the ten parts.
	public List<int> SpaceshipPartsNeeded { get; set; } = [1, 1, 1, 1, 1, 1, 1, 1, 1, 1];

	// How many copies of one part the rules require. Civ3 reads
	// General.SpaceshipPartsNeeded[part] directly (spec 25 section 2.6); a
	// ruleset that lists fewer requirements than the parts it defines keeps
	// the shipped one-of-each default rather than making the extra part
	// impossible to build.
	public int SpaceshipPartsNeededFor(int partIndex) {
		if (partIndex < 0) {
			return 0;
		}
		if (partIndex < SpaceshipPartsNeeded.Count) {
			return SpaceshipPartsNeeded[partIndex];
		}
		return 1;
	}
}
