using System.Collections.Generic;
using System.IO;
using C7GameData;
using C7GameData.Save;

namespace EngineTests.Utils;

// The era list the base rules carry for a game with no BIQ, read from
// C7/Lua/civ3/ruleset.json. Tests that build a GameData by hand use this so
// their era behaviour matches a real, rules-driven game. EraModelTest asserts
// this list equals what a BIQ import produces from the shipped conquests.biq.
public static class ShippedEras {
	private static List<Era> cached;

	public static List<Era> Load() {
		if (cached == null) {
			string path = Path.Combine(PathUtils.GameModesDir, "civ3", "ruleset.json");
			cached = SaveGame.LoadFromJSON(File.ReadAllText(path)).Eras;
		}
		// A fresh list each time, so a test mutating its era list cannot leak
		// into another one. The Era objects themselves are never mutated.
		return new List<Era>(cached);
	}
}
