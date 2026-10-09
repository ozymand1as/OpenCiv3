using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using C7Engine;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using QueryCiv3;
using QueryCiv3.Biq;
using Xunit;

namespace EngineTests.GameData;

/// <summary>
/// The goody-hut Mercenaries picker's wheeled screen (spec 22 section 4.5) and
/// the shipped data that feeds it.
///
/// The picker never hands over a wheeled unit type, and that precondition was
/// unimplementable until the unit prototypes carried the flag at all. The set
/// below is probed out of the shipped conquests.biq and asserted on all three
/// carriers a game can be built from - the ruleset.json prototype list, the BIQ
/// import path, and the runtime prototypes the picker actually reads - so a
/// dropped flag cannot pass silently.
/// </summary>
public class GoodyHutMercenaryPickerTest : IClassFixture<SaveGameFixture> {
	private readonly SaveGameFixture fixture;

	public GoodyHutMercenaryPickerTest(SaveGameFixture fixture) {
		this.fixture = fixture;
	}

	/// <summary>
	/// The shipped conquests.biq has 141 unit records and exactly these seven
	/// carry the Wheeled ability (PRTO Flags1[0] bit 0). Probed, not inferred.
	/// </summary>
	private static readonly string[] ShippedWheeledUnitNames = {
		"Cannon",
		"Catapult",
		"Chariot",
		"Hwach'a",
		"Three-Man Chariot",
		"Trebuchet",
		"War Chariot",
	};

	private static string ConquestsBiqPath => PathUtils.defaultBicPath;

	/// <summary>
	/// Every type the ruleset marks wheeled is refused by the picker even when
	/// every other precondition is satisfied, and the same type without the flag
	/// is handed over. The shipped prototype object is the one under test, so
	/// losing its flag fails here rather than quietly widening the picker.
	/// </summary>
	[Fact]
	public void ThePickerRefusesEveryShippedWheeledType() {
		C7GameData.GameData gd = fixture.saveGame.ToGameData(fixture.behaviors);
		Player player = gd.players.First(p => !p.isBarbarians);
		// The message path is a UI send, which a test game has no home for.
		player.isHuman = false;
		Tile hut = gd.map.tiles.First(t => t.IsLand());

		// Take the shipped prototypes out of the catalogue before replacing it.
		List<UnitPrototype> shipped = ShippedWheeledUnitNames
			.Select(name => gd.unitPrototypes.FirstOrDefault(p => p.name == name))
			.ToList();
		Assert.All(shipped, p => Assert.True(p != null));

		// The second catalogue entry always qualifies, so the walk falling
		// through to it is what proves the first entry was refused.
		UnitPrototype fallback = new() { name = "Fallback Warrior" };
		fallback.categories.Add("Land");
		fallback.producibleBy.Add(player.civilization);

		foreach (UnitPrototype wheeled in shipped) {
			Assert.True(wheeled.wheeled, $"{wheeled.name} must carry the ruleset's wheeled flag");

			// Clear the gates that would otherwise refuse this type for
			// unrelated reasons, so the flag under test is the only difference
			// between the two runs.
			wheeled.requiredTech = null;
			wheeled.unproducible = false;
			wheeled.categories.Remove("Sea");
			wheeled.categories.Add("Land");
			wheeled.producibleBy.Add(player.civilization);

			// Only the popping player is alive, so the buildable-or-owned
			// sweep is satisfied.
			gd.players = new List<Player> { player };
			gd.unitPrototypes = new List<UnitPrototype> { wheeled, fallback };

			Assert.Equal(fallback.name, RunPicker(gd, player, hut));

			// The isolation above really did clear the other gates: the same
			// shipped type is handed over once the wheeled flag is off.
			wheeled.wheeled = false;
			Assert.Equal(wheeled.name, RunPicker(gd, player, hut));
			wheeled.wheeled = true;
		}
	}

	/// <summary>
	/// The wheeled set of the shipped conquests.biq, of the ruleset.json
	/// prototype list and of the runtime prototypes the picker reads are the
	/// same seven types.
	/// </summary>
	[SkippableFact]
	public void TheShippedBiqAndTheRulesetAgreeOnTheWheeledTypes() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");

		// The BIQ itself, as the original engine reads it: 141 unit records,
		// seven of them wheeled.
		BiqData biq = BiqData.LoadFile(ConquestsBiqPath);
		Assert.Equal(141, biq.Prto.Length);
		Assert.Equal(ShippedWheeledUnitNames, Sorted(biq.Prto.Where(p => p.Wheeled).Select(p => p.Name)));

		// The ruleset.json prototype list, which is what a game with no BIQ
		// reads.
		Assert.Equal(ShippedWheeledUnitNames, Sorted(
			fixture.saveGame.UnitPrototypes
				.Where(p => p.flags.Contains(SaveUnitPrototype.Flag.Wheeled))
				.Select(p => p.name)));

		// And the runtime prototypes the picker reads, so the flag survives the
		// conversion as well.
		C7GameData.GameData gd = fixture.saveGame.ToGameData(fixture.behaviors);
		Assert.Equal(ShippedWheeledUnitNames, Sorted(
			gd.unitPrototypes.Where(p => p.wheeled).Select(p => p.name)));
	}

	/// <summary>
	/// The other carrier a game can be built from: the BIQ import path. The
	/// shipped conquests.biq itself carries no WMAP section, so it cannot be
	/// imported as a scenario; the sample save imports against it as the default
	/// rules file, which is the path ImportCiv3's PRTO mapping runs on.
	/// </summary>
	[SkippableFact]
	public async Task TheBiqImportPathCarriesTheWheeledFlag() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");

		string savePath = await SampleSaves.TryEnsureSampleSave();
		Skip.If(savePath == null, "The sample save fixture is not available.");

		SaveGame imported = ImportCiv3.ImportSav(savePath, ConquestsBiqPath, (relativeModePath) => PathUtils.defaultPediaIconsPath);

		Assert.Equal(ShippedWheeledUnitNames, Sorted(
			imported.UnitPrototypes
				.Where(p => p.flags.Contains(SaveUnitPrototype.Flag.Wheeled))
				.Select(p => p.name)));
	}

	private static string[] Sorted(IEnumerable<string> names) {
		return names.OrderBy(n => n).ToArray();
	}

	/// <summary>
	/// Runs the picker over a two-entry catalogue under the seed the rest of the
	/// mercenary tests use, which draws start index 0 for a catalogue that size,
	/// and returns the name of the type handed over.
	/// </summary>
	private static string RunPicker(C7GameData.GameData gd, Player player, Tile hut) {
		// Guard the seed's meaning rather than assuming it: the walk must reach
		// the wheeled entry first for the run to prove anything.
		Assert.Equal(0, new Random(1).Next(2));

		player.units.Clear();
		C7GameData.GameData.rng = new Random(1);

		Assert.Equal(GoodyHutOutcome.Mercenaries, GoodyHutInteractions.Apply(gd, player, hut, GoodyHutOutcome.Mercenaries));
		return Assert.Single(player.units).unitType.name;
	}
}
