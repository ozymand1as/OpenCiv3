using System.Collections.Generic;
using System.Linq;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using QueryCiv3;
using QueryCiv3.Biq;
using Xunit;

namespace EngineTests.GameData;

/// <summary>
/// Guards the shipped ruleset data for terrain passability and the units Civ3
/// marks as wheeled.
///
/// Civ3 marks mountains, jungle, marsh and volcano as impassable to wheeled
/// units, and flags chariots, catapults, cannon, trebuchets and the like as
/// wheeled. The expected values below are the whole set probed out of the
/// install's conquests.biq with QueryCiv3 (11_movement.md §8.1), not a summary
/// of them: every terrain's two flag bytes and every unit's Wheeled bit is
/// asserted, so a typo or an omitted carrier is caught rather than silently
/// doing nothing.
/// </summary>
public class WheeledMovementRulesTest : IClassFixture<SaveGameFixture> {
	private readonly SaveGameFixture fixture;
	private readonly SaveGame save;

	public WheeledMovementRulesTest(SaveGameFixture fixture) {
		this.fixture = fixture;
		save = fixture.saveGame;
	}

	// Every terrain in the shipped BIQ, with its ImpassableByWheeled byte
	// (+0x7B of the terrain record). Only these four are set.
	public static TheoryData<string, bool> ShippedImpassableByWheeled => new() {
		{ "desert", false },
		{ "plains", false },
		{ "grassland", false },
		{ "tundra", false },
		{ "flood plain", false },
		{ "hills", false },
		{ "mountains", true },
		{ "forest", false },
		{ "jungle", true },
		{ "marsh", true },
		{ "volcano", true },
		{ "coast", false },
		{ "sea", false },
		{ "ocean", false },
	};

	[Theory]
	[MemberData(nameof(ShippedImpassableByWheeled))]
	public void ImpassableByWheeledMatchesTheShippedBiq(string key, bool expected) {
		TerrainType terrain = save.TerrainTypes.First(t => t.Key == key);

		if (expected) {
			// Civ3 lifts the restriction when the tile is roaded or railed, and
			// only wheeled units are restricted.
			Assert.Equal(new[] { "road", "railroad" }, terrain.impassableTo[SaveUnitPrototype.Flag.Wheeled]);
		} else {
			Assert.DoesNotContain(SaveUnitPrototype.Flag.Wheeled, terrain.impassableTo.Keys);
		}
	}

	[Fact]
	public void ImpassableIsZeroForEveryShippedTerrain() {
		// The other byte of the same flag pair (+0x7A) is clear for all 14
		// terrains in the shipped rules, so the wheeled flag is the only
		// terrain-impassability lever the base game pulls.
		Assert.Equal(14, save.TerrainTypes.Count);
		Assert.All(save.TerrainTypes, terrain => Assert.False(terrain.impassable));
	}

	// Every unit type in the shipped BIQ that carries the Wheeled ability
	// (UTA_Wheeled). Probed, not inferred: the BIQ has 141 unit records and
	// exactly these seven are wheeled.
	public static TheoryData<string> ShippedWheeledUnits => new() {
		"Chariot",
		"War Chariot",
		"Three-Man Chariot",
		"Catapult",
		"Trebuchet",
		"Cannon",
		"Hwach'a",
	};

	[Theory]
	[MemberData(nameof(ShippedWheeledUnits))]
	public void UnitIsWheeled(string name) {
		SaveUnitPrototype proto = save.UnitPrototypes.First(p => p.name == name);

		Assert.Contains(SaveUnitPrototype.Flag.Wheeled, proto.flags);
	}

	[Fact]
	public void NoOtherUnitIsWheeled() {
		// The set is asserted as a whole: a flag added to the wrong unit is a
		// play difference just as much as a missing one.
		string[] wheeled = save.UnitPrototypes
			.Where(p => p.flags.Contains(SaveUnitPrototype.Flag.Wheeled))
			.Select(p => p.name)
			.OrderBy(n => n)
			.ToArray();

		Assert.Equal(new[] { "Cannon", "Catapult", "Chariot", "Hwach'a", "Three-Man Chariot", "Trebuchet", "War Chariot" }, wheeled);
	}

	[Theory]
	[InlineData("Settler")]
	[InlineData("Worker")]
	[InlineData("Warrior")]
	[InlineData("Horseman")]
	public void UnitIsNotWheeled(string name) {
		SaveUnitPrototype proto = save.UnitPrototypes.First(p => p.name == name);

		Assert.DoesNotContain(SaveUnitPrototype.Flag.Wheeled, proto.flags);
	}

	[Fact]
	public void WheeledFlagSurvivesConversionToRuntimeUnitPrototype() {
		C7GameData.GameData gameData = save.ToGameData(fixture.behaviors);

		// The movement code reads the runtime unit prototype, so the flag has
		// to make it across from the save data for the rule to take effect.
		UnitPrototype catapult = gameData.unitPrototypes.First(p => p.name == "Catapult");
		UnitPrototype warrior = gameData.unitPrototypes.First(p => p.name == "Warrior");

		Assert.True(catapult.wheeled);
		Assert.False(warrior.wheeled);
	}

	[Fact]
	public void TerrainRestrictionSurvivesConversionToRuntimeGameData() {
		C7GameData.GameData gameData = save.ToGameData(fixture.behaviors);

		// The movement code reads the terrain off the tile, so the restriction
		// has to make it across from the save data for the rule to take effect.
		TerrainType mountains = gameData.terrainTypes.First(t => t.Key == "mountains");

		Assert.Equal(new[] { "road", "railroad" }, mountains.impassableTo[SaveUnitPrototype.Flag.Wheeled]);
	}

	// The tests above hold the checked-in ruleset to values measured from the BIQ
	// by hand. This one re-reads the shipped BIQ, so the copy cannot drift from
	// the data the original engine consumes, and it runs the BIQ import path
	// itself rather than only the ruleset path.
	[SkippableFact]
	public void TheShippedBiqAndTheRulesetAgree() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");

		BiqData biq = BiqData.LoadFile(PathUtils.defaultBicPath);

		// ImpassableByWheeled is set on exactly four terrains, and Impassable on
		// none, so the wheeled flag is the only lever the shipped rules pull.
		Assert.Equal(
			new[] { "jungle", "marsh", "mountains", "volcano" },
			biq.Terr.Where(t => t.ImpassableByWheeled != 0).Select(t => t.Name.ToLowerInvariant()).OrderBy(n => n).ToArray());
		Assert.All(biq.Terr, t => Assert.Equal(0, t.Impassable));

		// The import path turns that byte into the same restriction the ruleset
		// declares, so a game imported from the BIQ is not missing the rule.
		for (int i = 0; i < biq.Terr.Length; i++) {
			TerrainType imported = TerrainType.ImportFromCiv3(i, biq.Terr[i]);

			Assert.Equal(biq.Terr[i].ImpassableByWheeled != 0, imported.impassableTo.ContainsKey(SaveUnitPrototype.Flag.Wheeled));
		}

		// And the ruleset flags exactly the BIQ's wheeled units.
		Assert.Equal(
			biq.Prto.Where(p => p.Wheeled).Select(p => p.Name).Distinct().OrderBy(n => n).ToArray(),
			save.UnitPrototypes.Where(p => p.flags.Contains(SaveUnitPrototype.Flag.Wheeled)).Select(p => p.name).OrderBy(n => n).ToArray());
	}
}
