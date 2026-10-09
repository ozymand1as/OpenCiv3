using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using C7Engine;
using C7Engine.Lua;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using QueryCiv3;
using QueryCiv3.Biq;
using Xunit;
namespace EngineTests.GameData;

/// <summary>
/// The reveal-all half of the revelation model: 28_formats.md section 2.3's
/// default for a scenario file too old to carry per-tile reveal data, and the
/// reveal-all pass of section 3.2 that consumes it.
///
/// It is deliberately separate from the Maps goody-hut flag of
/// 22_barbarians_goody_huts.md section 5: that flag is a notice that the reveal
/// state changed, while this mode means "every tile is revealed", and the Maps
/// outcome reveals only the hut's neighbourhood. GoodyHutTest pins the flag and
/// the neighbourhood roll.
/// </summary>
public class MapRevealTest : IClassFixture<SaveGameFixture> {
	private const string SkipReason = "No Civ3 install found.";

	// A shipped Conquest with a map, so the whole ImportBiq path runs. Measured
	// out of the file with QueryCiv3: every one of its 3780 tiles has its
	// FogOfWar bit clear and its GAME section's MapVisible is 0, so nothing in
	// the file reveals a tile by itself and only the loader's reveal-all
	// default can produce a fully revealed map.
	private const string ScenarioFileName = "1 Mesopotamia.biq";

	// VER# payload: two unused dwords, then the version pair (28_formats.md
	// section 2.2), i.e. major at file offset 24 and minor at file offset 28.
	private const int VersionMajorOffset = 24;
	private const int VersionMinorOffset = 28;

	private readonly BehaviorEngine behaviors;

	public MapRevealTest(SaveGameFixture fixture) {
		behaviors = fixture.behaviors;
	}

	private static string ScenarioPath =>
		Path.Combine(Civ3Location.GetCiv3Path(), "Conquests", "Conquests", ScenarioFileName);

	private static Func<string, string> GetPediaIconsPath(string subfolder) {
		return (relativeModPath) => {
			if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
				relativeModPath = relativeModPath.Replace("\\conquests\\", "/Conquests/");
			}

			return Path.GetFullPath(Path.Combine(
				Civ3Location.GetCiv3Path(), subfolder, relativeModPath, "Text", "PediaIcons.txt"));
		};
	}

	private static SaveGame ImportScenario(string path) {
		EngineStorage.animationsEnabled = false;
		return ImportCiv3.ImportBiq(path, PathUtils.defaultBicPath, GetPediaIconsPath("Conquests/Conquests"));
	}

	/// <summary>
	/// A copy of a scenario with a different VER# major/minor, so the loader's
	/// version gate is exercised against a real file rather than against the
	/// predicate alone. The copy is already decompressed, which the reader
	/// accepts as-is.
	/// </summary>
	private static string WithVersion(string sourcePath, int major, int minor) {
		byte[] bytes = QueryCiv3.Util.ReadFile(sourcePath);
		BitConverter.GetBytes(major).CopyTo(bytes, VersionMajorOffset);
		BitConverter.GetBytes(minor).CopyTo(bytes, VersionMinorOffset);

		string target = Path.Combine(Path.GetTempPath(), $"parity-t7-reveal-{Guid.NewGuid():N}.biq");
		File.WriteAllBytes(target, bytes);
		return target;
	}

	private static Player MakePlayer(C7GameData.GameData gameData, string id) {
		Player player = new() {
			id = ID.FromString(id),
			civilization = new Civilization(),
			government = new Government(),
			rules = new Rules(),
		};
		gameData.players.Add(player);
		return player;
	}

	private static Tile MakeTile(TerrainType terrain) {
		return new Tile(ID.None("tile")) {
			baseTerrainType = terrain,
			overlayTerrainType = terrain,
		};
	}

	/// <summary>
	/// The reveal-all operation itself: every tile of the map becomes known to
	/// every live player, including tiles no unit has seen, and no unknown
	/// neighbour is left marked as a border tile. A defeated player is not part
	/// of the reveal, matching the liveness-bit test of the original pass.
	/// </summary>
	[Fact]
	public void RevealAllTilesMarksEveryTileKnownToEveryLivePlayer() {
		C7GameData.GameData gameData = new();
		EngineStorage.InitializeGameDataForTests(gameData);

		Player alive = MakePlayer(gameData, "player-1");
		Player other = MakePlayer(gameData, "player-2");
		Player defeated = MakePlayer(gameData, "player-3");
		defeated.defeated = true;

		TerrainType land = new() { Key = "grassland" };
		Tile seen = MakeTile(land);
		Tile border = MakeTile(land);
		Tile unexplored = MakeTile(land);
		gameData.map.tiles = new List<Tile> { seen, border, unexplored };

		// One live player has seen a single tile; the next one is its border.
		alive.tileKnowledge.knownTiles.Add(seen);
		alive.tileKnowledge.borderTiles.Add(border);

		gameData.RevealAllTiles();

		foreach (Player player in new[] { alive, other }) {
			Assert.Equal(gameData.map.tiles.Count, player.tileKnowledge.AllKnownTiles().Count);
			Assert.All(gameData.map.tiles, t => Assert.True(player.tileKnowledge.isTileKnown(t)));
		}

		// Everything is known, so nothing is on a border any more.
		Assert.Empty(alive.tileKnowledge.borderTiles);

		Assert.Empty(defeated.tileKnowledge.AllKnownTiles());
	}

	/// <summary>
	/// Both triggers of 28_formats.md section 2.3 are independent, and neither
	/// fires for a Conquests scenario.
	/// </summary>
	[SkippableFact]
	public void TheRevealAllDefaultFiresForBothSpecTriggers() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), SkipReason);

		// The shipped scenario itself: 12.07, a fourteen-record TERR table.
		BiqData biq = BiqData.LoadFile(ScenarioPath);
		Assert.False(ImportCiv3.RevealAllTilesOnLoad(biq));

		// VER# below 12.03.
		biq.FileData.Civ3Version.MinorVersion = 2;
		Assert.True(ImportCiv3.RevealAllTilesOnLoad(biq));

		// 12.03 is the boundary: at it, per-tile reveal data exists.
		biq.FileData.Civ3Version.MinorVersion = 3;
		Assert.False(ImportCiv3.RevealAllTilesOnLoad(biq));

		// A whole major version below 12 also predates reveal tracking.
		biq.FileData.Civ3Version.MajorVersion = 11;
		biq.FileData.Civ3Version.MinorVersion = 20;
		Assert.True(ImportCiv3.RevealAllTilesOnLoad(biq));

		// A twelve-record TERR table at a current version (the second trigger).
		// No shipped file carries both a current version and a pre-Conquests
		// terrain table - the shipped pre-Conquests rules files all predate
		// 12.03 as well, so the version trigger covers them - which is why this
		// trigger is pinned on the predicate rather than through an import.
		BiqData preConquestsTerrain = BiqData.LoadFile(ScenarioPath);
		preConquestsTerrain.Terr = new TERR[ImportCiv3.PreConquestsTerrainCount];
		Assert.True(ImportCiv3.RevealAllTilesOnLoad(preConquestsTerrain));

		// A file that carries no TERR table at all inherits the default
		// ruleset's terrain table, so its record count cannot trigger anything.
		BiqData noTerrainTable = BiqData.LoadFile(ScenarioPath);
		noTerrainTable.Terr = null;
		Assert.False(ImportCiv3.RevealAllTilesOnLoad(noTerrainTable));
	}

	/// <summary>
	/// The whole rule, end to end: a scenario whose VER# is below 12.03 loads
	/// with every tile revealed to every player, and a current scenario does
	/// not.
	/// </summary>
	[SkippableFact]
	public void AScenarioOlderThanTheRevealFormatLoadsFullyRevealed() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), SkipReason);

		// 12.02 is below the reveal threshold but not below the unrelated 12.00
		// tile-byte threshold of section 2.3, so only the reveal rule fires.
		string legacy = WithVersion(ScenarioPath, major: 12, minor: 2);
		try {
			SaveGame save = ImportScenario(legacy);
			Assert.True(save.revealAllTilesOnLoad);
			Assert.NotEmpty(save.Map.tiles);

			C7GameData.GameData gameData = save.ToGameData(behaviors);
			Assert.Equal(save.Map.tiles.Count, gameData.map.tiles.Count);

			foreach (Player player in gameData.players) {
				Assert.Equal(gameData.map.tiles.Count, player.tileKnowledge.AllKnownTiles().Count);
			}
		} finally {
			File.Delete(legacy);
		}
	}

	[SkippableFact]
	public void ACurrentScenarioDoesNotLoadFullyRevealed() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), SkipReason);

		SaveGame save = ImportScenario(ScenarioPath);
		Assert.False(save.revealAllTilesOnLoad);

		C7GameData.GameData gameData = save.ToGameData(behaviors);
		int tiles = gameData.map.tiles.Count;
		Assert.True(tiles > 0);
		Assert.NotEmpty(gameData.players);

		// Every tile of this file has its own reveal bit clear, so a player
		// knows only what a unit or a city can see, which is not the map.
		foreach (Player player in gameData.players) {
			Assert.True(
				player.tileKnowledge.AllKnownTiles().Count < tiles,
				$"{player} knows every tile of a current scenario");
		}
	}
}
