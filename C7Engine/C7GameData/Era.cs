namespace C7GameData;

/// <summary>
/// One era of the rules' era list, in the order the rules list it. The list is
/// the BIQ's ERAS section for a BIQ game and the "eras" array of ruleset.json
/// for a game that has no BIQ; a scenario can carry any number of eras.
///
/// The civilopediaName is the key everything else joins on: a technology's
/// EraCivilopediaName and a player's eraCivilopediaName are both one of these.
/// The name is the display name (the BIQ's ERAS name, e.g. "Middle Ages"). The
/// artName is the texture-registry key the tech box and science-advisor art is
/// chosen by; the BIQ has no art field for an era, so the import assigns it by
/// the era's position and ruleset.json carries it explicitly.
/// </summary>
public class Era {
	public string civilopediaName;
	public string name;
	public string artName;
}
