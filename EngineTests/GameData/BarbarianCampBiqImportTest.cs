using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using C7Engine;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using QueryCiv3;
using QueryCiv3.Biq;
using Xunit;

namespace EngineTests.GameData;

/// <summary>
/// The BIQ tile import's camp and tribe mapping (spec 22 section 2).
///
/// <para>The BIQ keeps the camp on bit 7 of the tile's C3COverlays flag word
/// and the tribe id in a separate 16-bit field, so the <em>bit</em> is the
/// presence test and the id alone proves nothing about a camp. The shipped
/// "6 Age of Discovery.biq" is the file that exposes the difference: it carries
/// a tribe id on 37 tiles that have no camp bit (all of them the slot-75
/// fallback), alongside the 29 real camps that have both.</para>
///
/// <para>The SAV is the other way round - its 16-bit BarbarianCamp field
/// <em>is</em> the tribe id, with -1 meaning no camp, so its sign doubles as the
/// presence test - and that is what ImportSav reads; BarbarianTribeTest's
/// TheCiv3SaveImportCarriesTheCampAndUnitTribes pins that path on the Middle
/// Ages save.</para>
/// </summary>
public class BarbarianCampBiqImportTest {
	private const string SkipReason = "No Civ3 install found.";

	// The scenario whose shipped BIQ carries tribe ids without the camp bit.
	private const string ScenarioFileName = "6 Age of Discovery.biq";

	private static string ScenarioPath =>
		Path.Combine(Civ3Location.GetCiv3Path(), "Conquests", "Conquests", ScenarioFileName);

	/// <summary>
	/// The regression for the defect: a tile whose BIQ record carries a tribe id
	/// but not the camp bit must import with no tribe.
	/// </summary>
	[SkippableFact]
	public void ATribeIdWithoutTheCampBitIsNotImported() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), SkipReason);

		List<(int Index, int Tribe, bool Camp)> withoutCamp = BiqTiles().Where(t => !t.Camp).ToList();

		// Guard the fixture: if this file ever stops carrying a tribe id with no
		// camp bit, the test would pass for the wrong reason.
		Assert.NotEmpty(withoutCamp);
		Assert.All(withoutCamp, t => Assert.Equal(BarbarianTribes.FallbackSlot, t.Tribe));

		SaveGame save = ImportAgeOfDiscovery();

		// No imported tile may carry a tribe without a camp, whatever the BIQ's
		// id field says.
		List<string> offenders = save.Map.tiles
			.Where(t => t.barbarianTribeId != null && !t.features.Contains("barbarianCamp"))
			.Select(t => $"({t.X},{t.Y}) -> {t.barbarianTribeId}")
			.ToList();
		Assert.Empty(offenders);

		foreach ((int Index, int Tribe, bool Camp) tile in withoutCamp) {
			SaveTile imported = save.Map.tiles[tile.Index];
			Assert.False(imported.features.Contains("barbarianCamp"), $"the BIQ has no camp on tile {tile.Index}");
			Assert.Null(imported.barbarianTribeId);
		}
	}

	/// <summary>
	/// The positive control: a tile whose BIQ record carries both the camp bit
	/// and a tribe id still imports that id, so the fix cannot pass by dropping
	/// every tribe.
	/// </summary>
	[SkippableFact]
	public void ACampTilesTribeIdIsStillImported() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), SkipReason);

		List<(int Index, int Tribe, bool Camp)> camps = BiqTiles().Where(t => t.Camp).ToList();
		Assert.NotEmpty(camps);

		SaveGame save = ImportAgeOfDiscovery();

		foreach ((int Index, int Tribe, bool Camp) tile in camps) {
			SaveTile imported = save.Map.tiles[tile.Index];
			Assert.Contains("barbarianCamp", imported.features);
			Assert.Equal(tile.Tribe, imported.barbarianTribeId);
		}
	}

	// ---------------------------------------------------------------------
	// Helpers
	// ---------------------------------------------------------------------

	/// <summary>
	/// Every tile of the scenario's BIQ whose tribe-id field is set, keyed by its
	/// index in the BIQ's tile list, with the camp bit as the separate field it
	/// is. ImportBiq walks that list in order and appends each tile to the save,
	/// so the index identifies the imported tile without re-deriving the map
	/// coordinates.
	/// </summary>
	private static List<(int Index, int Tribe, bool Camp)> BiqTiles() {
		BiqData biq = BiqData.LoadFile(ScenarioPath);

		List<(int Index, int Tribe, bool Camp)> tiles = new();
		for (int i = 0; i < biq.Tile.Length; ++i) {
			TILE tile = biq.Tile[i];
			if (tile.BarbarianTribe < 0) {
				continue;
			}
			tiles.Add((i, tile.BarbarianTribe, tile.BarbarianCamp));
		}
		return tiles;
	}

	private static SaveGame ImportAgeOfDiscovery() {
		EngineStorage.animationsEnabled = false;
		return ImportCiv3.ImportBiq(ScenarioPath, PathUtils.defaultBicPath, GetPediaIconsPath("Conquests/Conquests"));
	}

	private static Func<string, string> GetPediaIconsPath(string subfolder) {
		return (relativeModPath) => {
			if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
				relativeModPath = relativeModPath.Replace("\\conquests\\", "/Conquests/");
			}

			return Path.GetFullPath(Path.Combine(
				Civ3Location.GetCiv3Path(), subfolder, relativeModPath, "Text", "PediaIcons.txt"));
		};
	}
}
