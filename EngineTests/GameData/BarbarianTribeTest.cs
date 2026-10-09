using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using C7Engine;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using QueryCiv3;
using QueryCiv3.Biq;
using Xunit;

namespace EngineTests.GameData;

/// <summary>
/// The barbarian tribe table (spec 22 sections 2, 3.2.5, 5.1, 6.3 and 8.2):
/// the culture-group bands of fifteen slots and the slot-75 fallback, the tribe
/// a camp carries on its tile, the tribe name in the hut and camp messages, and
/// the tribe a spawned barbarian unit carries.
///
/// The data assertions pin the shipped table itself, from both carriers: the
/// BIQ's barbarian race record (RACE 0, whose city-name list is the tribe
/// table) and C7/Lua/civ3/ruleset.json, which is what a game with no BIQ reads.
/// </summary>
public class BarbarianTribeTest : IClassFixture<SaveGameFixture> {
	private readonly SaveGameFixture fixture;

	public BarbarianTribeTest(SaveGameFixture fixture) {
		this.fixture = fixture;
	}

	/// <summary>
	/// The 76 tribe names, in slot order, measured out of the shipped
	/// conquests.biq's RACE section (race 0, "A Barbarian Chiefdom") and
	/// re-measured from C7/Lua/civ3/ruleset.json. Slots 0-14 are the American
	/// band, 15-29 the European, 30-44 the Mediterranean, 45-59 the Mid East,
	/// 60-74 the Asian, and slot 75 is the fallback, named "Barbarian".
	/// </summary>
	private static readonly string[] ShippedTribeNames = {
		"Chanca", "Lupaca", "Cherokee", "Anasazi", "Teoihuacan", "Olmec",
		"Zapotec", "Chehalis", "Chinook", "Apache", "Illinois", "Inuit",
		"Navajo", "Carib", "Saxon", "Vandal", "Goth", "Angle",
		"Magyar", "Khazak", "Iberian", "Bulgar", "Alemanni", "Burgundian",
		"Gepid", "Hun", "Jute", "Marcomanni", "Seljuk", "Phoenician",
		"Estruscan", "Illuryian", "Thracian", "Phrygian", "Gaul", "Minoan",
		"Mycenian", "Cimmerian", "Ligurian", "Numidian", "Patzinal", "Sarmatian",
		"Scythian", "Suren", "Assyrian", "Harappan", "Mauryan", "Parthian",
		"Harappan", "Nubian", "Sarbadar", "Bactrian", "Circassian", "Cuman",
		"Hurrian", "Kassite", "Bantu", "Khoisan", "Libyan", "Shangian",
		"Yayoi", "Zhou", "Ainu", "Polynesian", "Aryan", "Avar",
		"Ghuzz", "Hsung-Nu", "Kushans", "Yue-Chi", "Sakae", "Uzbek",
		"Tartar", "Toltec", "Kushite", "Barbarian",
	};

	private static string ConquestsBiqPath =>
		Path.Combine(Civ3Location.GetCiv3Path(), "Conquests", "conquests.biq");

	// ---------------------------------------------------------------------
	// The shipped table, from both carriers
	// ---------------------------------------------------------------------

	/// <summary>
	/// The table is the barbarian race's own city-name list. This is the
	/// measurement everything else rests on, so it asserts the whole list, the
	/// culture group of the race and of one civilization per band.
	/// </summary>
	[SkippableFact]
	public void TheShippedBiqCarriesTheTribeTable() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");

		BiqData biq = BiqData.LoadFile(ConquestsBiqPath);

		Assert.Equal("A Barbarian Chiefdom", biq.Race[0].Name);
		Assert.Equal(-1, biq.Race[0].CultureGroup);
		Assert.Equal(
			ShippedTribeNames,
			biq.RaceCityName[0].Select(c => c.Name).ToArray());

		// The band each culture group's civilizations belong to, as the RACE
		// records carry them. America is group 0, Germany 1, Rome 2, Babylon 3,
		// China 4 - the five bands the table is divided into.
		Assert.Equal(0, biq.Race.First(r => r.Name == "America").CultureGroup);
		Assert.Equal(1, biq.Race.First(r => r.Name == "Germany").CultureGroup);
		Assert.Equal(2, biq.Race.First(r => r.Name == "Rome").CultureGroup);
		Assert.Equal(3, biq.Race.First(r => r.Name == "Babylon").CultureGroup);
		Assert.Equal(4, biq.Race.First(r => r.Name == "China").CultureGroup);
	}

	/// <summary>
	/// A game with no BIQ reads its rules from ruleset.json, so the tribe names
	/// and the culture groups have to be there too. This never skips.
	/// </summary>
	[Fact]
	public void RulesetJsonCarriesTheTribeTable() {
		using System.Text.Json.JsonDocument ruleset = JsonUtils.LoadBaseRuleset();
		System.Text.Json.JsonElement civilizations = ruleset.RootElement.GetProperty("civilizations");

		List<string> barbarianNames = null;
		foreach (System.Text.Json.JsonElement civ in civilizations.EnumerateArray()) {
			if (civ.TryGetProperty("isBarbarian", out System.Text.Json.JsonElement isBarbarian) && isBarbarian.GetBoolean()) {
				barbarianNames = civ.GetProperty("cityNames").EnumerateArray().Select(n => n.GetString()).ToList();
			}
		}

		Assert.NotNull(barbarianNames);
		Assert.Equal(ShippedTribeNames, barbarianNames);

		// Every band the picker can ask for has to exist as a culture group,
		// with the index the band arithmetic uses.
		List<int> cultureGroupIndexes = ruleset.RootElement.GetProperty("cultureGroups")
			.EnumerateArray()
			.Select(cg => cg.GetProperty("index").GetInt32())
			.ToList();
		for (int band = 0; band < BarbarianTribes.CultureGroupCount; ++band) {
			Assert.Contains(band, cultureGroupIndexes);
		}
	}

	/// <summary>
	/// The same table reaches a real game built from ruleset.json: the barbarian
	/// civilization carries all 76 names, and every civilization carries the
	/// culture group index the band arithmetic reads. This is the carrier set
	/// through the path a generated game actually takes.
	/// </summary>
	[Fact]
	public void TheRulesetGameCarriesTheTribeNamesAndBands() {
		C7GameData.GameData gameData = fixture.saveGame.ToGameData(fixture.behaviors);

		Assert.Equal(ShippedTribeNames, gameData.BarbarianCivilization.cityNames);

		foreach (Civilization civilization in gameData.civilizations) {
			Assert.NotNull(civilization.cultureGroup);
			Assert.True(
				BarbarianTribes.CultureGroupIndex(civilization) < BarbarianTribes.CultureGroupCount,
				$"{civilization.name} has no band in the tribe table");
		}

		// Every camp the generator placed has a slot whose name exists, so the
		// generated path is not playing with a tribe id that resolves to
		// nothing.
		Assert.NotEmpty(gameData.map.barbarianCamps);
		foreach (Tile camp in gameData.map.barbarianCamps) {
			Assert.True(BarbarianTribes.IsValidSlot(camp.barbarianTribeId));
			Assert.NotNull(BarbarianTribes.TribeName(gameData, camp.barbarianTribeId));
		}
	}

	// ---------------------------------------------------------------------
	// The table itself
	// ---------------------------------------------------------------------

	/// <summary>
	/// With a free band, the pick lands inside the popping player's band and
	/// nowhere else, whatever the random offset is.
	/// </summary>
	[Fact]
	public void ASlotIsPickedInsideThePlayersBand() {
		List<int> held = new();

		for (int band = 0; band < BarbarianTribes.CultureGroupCount; ++band) {
			for (int offset = 0; offset < BarbarianTribes.SlotsPerCultureGroup; ++offset) {
				int slot = BarbarianTribes.PickTribeSlot(band, held, offset);
				Assert.True(BarbarianTribes.IsSlotInBand(slot, band), $"band {band}, offset {offset} -> {slot}");
			}
		}
	}

	/// <summary>
	/// The first free slot at or after the random offset wins; when the band's
	/// tail is held the scan wraps to the band's start rather than leaving it.
	/// </summary>
	[Fact]
	public void TheRandomOffsetSearchWrapsWithinTheBand() {
		int band = 2;
		int start = BarbarianTribes.BandStart(band);

		// Hold everything but the first slot of the band.
		List<int> held = Enumerable.Range(start + 1, BarbarianTribes.SlotsPerCultureGroup - 1).ToList();
		Assert.Equal(start, BarbarianTribes.PickTribeSlot(band, held, 7));

		// Hold the first thirteen, so only the last two are free: offset 0 and
		// offset 13 both land on the first of them.
		held = Enumerable.Range(start, 13).ToList();
		Assert.Equal(start + 13, BarbarianTribes.PickTribeSlot(band, held, 0));
		Assert.Equal(start + 13, BarbarianTribes.PickTribeSlot(band, held, 13));
		Assert.Equal(start + 13, BarbarianTribes.PickTribeSlot(band, held, 12));
		Assert.Equal(start + 14, BarbarianTribes.PickTribeSlot(band, held, 14));
	}

	/// <summary>
	/// A band with all fifteen slots in use falls back to the fixed slot, which
	/// the shipped rules name "Barbarian", and the fallback is per band: another
	/// band's free slots do not rescue it.
	/// </summary>
	[Fact]
	public void AFullBandFallsBackToTheSeventyFifthSlot() {
		int band = 1;
		List<int> held = Enumerable.Range(BarbarianTribes.BandStart(band), BarbarianTribes.SlotsPerCultureGroup).ToList();

		for (int offset = 0; offset < BarbarianTribes.SlotsPerCultureGroup; ++offset) {
			Assert.Equal(BarbarianTribes.FallbackSlot, BarbarianTribes.PickTribeSlot(band, held, offset));
		}

		// The band is full and every other band is free; the fallback is still
		// the answer, because the scan never leaves the requested band.
		Assert.Equal(15 * BarbarianTribes.CultureGroupCount, BarbarianTribes.FallbackSlot);
		Assert.Equal("Barbarian", ShippedTribeNames[BarbarianTribes.FallbackSlot]);
	}

	/// <summary>
	/// The binary takes the fallback slot even when it is already held, so two
	/// tribes can share its name; there is no second scan.
	/// </summary>
	[Fact]
	public void TheFallbackSlotIsUsedEvenWhenItIsHeld() {
		List<int> held = Enumerable.Range(0, BarbarianTribes.SlotsPerCultureGroup).ToList();
		held.Add(BarbarianTribes.FallbackSlot);

		Assert.Equal(BarbarianTribes.FallbackSlot, BarbarianTribes.PickTribeSlot(0, held, 3));
	}

	/// <summary>
	/// A hut pop reads a free slot for its message but reserves nothing - only
	/// a camp holds a slot - so the same name can be drawn twice in a row.
	/// </summary>
	[Fact]
	public void ReadingATribeForAMessageDoesNotHoldTheSlot() {
		C7GameData.GameData gd = MakeGameData(out Player player, out Tile tile, band: 3, withNames: true);

		player.isHuman = true;
		C7GameData.GameData.rng = new Random(5);

		Assert.Equal(GoodyHutOutcome.Money, GoodyHutInteractions.Apply(gd, player, tile, GoodyHutOutcome.Money));
		int slot = SlotNamedIn(LastMessageText());

		Assert.True(BarbarianTribes.IsSlotInBand(slot, 3), $"slot {slot} is outside the player's band");
		Assert.Empty(BarbarianTribes.HeldSlots(gd.map));

		// The same slot is drawn again for the next message, because nothing
		// held it.
		C7GameData.GameData.rng = new Random(5);
		Assert.Equal(GoodyHutOutcome.Money, GoodyHutInteractions.Apply(gd, player, tile, GoodyHutOutcome.Money));
		Assert.Equal(slot, SlotNamedIn(LastMessageText()));
	}

	/// <summary>
	/// Every hut outcome's message names a tribe, not only the barbarian one:
	/// the binary's outcome resolver draws a slot for all eight messages (spec
	/// 22 sections 2 and 8.2).
	/// </summary>
	[Fact]
	public void EveryHutMessageNamesATribeFromThePlayersBand() {
		C7GameData.GameData gd = MakeGameData(out Player player, out Tile tile, band: 1, withNames: true);
		player.isHuman = true;

		C7GameData.GameData.rng = new Random(11);
		Assert.Equal(GoodyHutOutcome.Nothing, GoodyHutInteractions.Apply(gd, player, tile, GoodyHutOutcome.Nothing));

		string text = LastMessageText();
		int slot = SlotNamedIn(text);
		Assert.True(BarbarianTribes.IsSlotInBand(slot, 1));
		Assert.Contains(ShippedTribeNames[slot], text);
	}

	// ---------------------------------------------------------------------
	// Camps carry the tribe
	// ---------------------------------------------------------------------

	/// <summary>
	/// A generated camp carries a real slot, and no two camps of one generated
	/// map share a slot until the band is exhausted (the fallback is the only
	/// way to repeat).
	/// </summary>
	[Fact]
	public void GeneratedCampsCarryATribeSlot() {
		GameMap map = Generate(BarbarianActivity.Roaming);

		Assert.NotEmpty(map.barbarianCamps);
		foreach (Tile camp in map.barbarianCamps) {
			Assert.True(camp.hasBarbarianCamp);
			Assert.True(BarbarianTribes.IsValidSlot(camp.barbarianTribeId), $"{camp} has no tribe");
		}

		List<int> slots = map.barbarianCamps.Select(c => c.barbarianTribeId).ToList();
		List<int> belowFallback = slots.Where(s => s != BarbarianTribes.FallbackSlot).ToList();
		Assert.Equal(belowFallback.Count, belowFallback.Distinct().Count());
	}

	/// <summary>
	/// The camp's tribe round-trips through the save format: the tile's tribe id
	/// is written when it has one and read back, and a tile with no camp gains
	/// no tribe field at all.
	/// </summary>
	[Fact]
	public void TheCampTribeIdRoundTripsThroughTheSaveFormat() {
		TerrainType grass = new() { Key = "grassland", movementCost = 1, allowCities = true };
		List<TerrainType> terrains = new() { grass };

		Tile camp = new(ID.None("tile")) {
			XCoordinate = 3,
			YCoordinate = 5,
			baseTerrainType = grass,
			overlayTerrainType = grass,
			hasBarbarianCamp = true,
			barbarianTribeId = 17,
		};
		SaveTile saved = new(camp);
		Assert.Contains("barbarianCamp", saved.features);
		Assert.Equal(17, saved.barbarianTribeId);

		Tile restored = saved.ToTile(terrains, new List<Resource>(), new List<TerrainImprovement>());
		Assert.True(restored.hasBarbarianCamp);
		Assert.Equal(17, restored.barbarianTribeId);

		Tile plain = new(ID.None("tile")) {
			XCoordinate = 1,
			YCoordinate = 1,
			baseTerrainType = grass,
			overlayTerrainType = grass,
		};
		SaveTile plainSaved = new(plain);
		Assert.Null(plainSaved.barbarianTribeId);
		Assert.Equal(BarbarianTribes.None, plainSaved.ToTile(terrains, new List<Resource>(), new List<TerrainImprovement>()).barbarianTribeId);
	}

	/// <summary>
	/// Dispersing a camp frees its tribe slot and clears the tile's tribe id,
	/// and the message names the tribe (spec 22 section 6.3).
	/// </summary>
	[Fact]
	public void DispersingACampFreesItsSlotAndClearsTheTribeId() {
		C7GameData.GameData gd = MakeGameData(out Player player, out Tile tile, band: 4, withNames: true);
		player.isHuman = true;

		const int campSlot = 63;
		SetCamp(gd, tile, campSlot);
		Assert.Contains(campSlot, BarbarianTribes.HeldSlots(gd.map));

		MapUnit unit = PlaceUnit(player, tile);
		DrainMessages();

		unit.OnEnterTile(tile);

		Assert.False(tile.hasBarbarianCamp);
		Assert.Equal(BarbarianTribes.None, tile.barbarianTribeId);
		Assert.Empty(gd.map.barbarianCamps);
		Assert.DoesNotContain(campSlot, BarbarianTribes.HeldSlots(gd.map));
		Assert.Equal(25, player.gold);

		// The freed slot is available again to the same band.
		Assert.Equal(campSlot, BarbarianTribes.PickTribeSlot(4, BarbarianTribes.HeldSlots(gd.map), campSlot - BarbarianTribes.BandStart(4)));

		Assert.Equal($"We dispersed a {ShippedTribeNames[campSlot]} encampment and took 25 gold!", LastMessageText());
	}

	/// <summary>
	/// A camp unit spawned from a camp carries the camp's tribe.
	/// </summary>
	[Fact]
	public void CampSpawnsCarryTheCampsTribe() {
		C7GameData.GameData gd = MakeGameData(out Player player, out Tile tile, band: 0, withNames: true);
		Player barbarians = AddBarbarianPlayer(gd);
		gd.barbarianInfo.barbarianActivity = BarbarianActivity.Raging;
		gd.barbarianInfo.basicBarbarian = new UnitPrototype() { name = "Warrior" };
		gd.barbarianInfo.basicBarbarian.categories.Add("Land");
		// The spawn path may pick the advanced type instead; nothing here
		// depends on which, only on the tribe the spawned unit carries.
		gd.barbarianInfo.advancedBarbarian = gd.barbarianInfo.basicBarbarian;

		List<int> campSlots = new() { 4, 20, 41, 60 };
		for (int i = 0; i < campSlots.Count; ++i) {
			Tile camp = gd.map.tileAt(1 + i, 1);
			SetCamp(gd, camp, campSlots[i]);
		}

		// The camp spawn rate is a separate, known-divergent model (spec 22
		// section 10.2), and with so few camps it usually spawns nothing, so the
		// assertion is that every unit that does appear carries its camp's
		// tribe, over seeds until some appear.
		int spawned = 0;
		foreach (int seed in Enumerable.Range(1, 40)) {
			C7GameData.GameData.rng = new Random(seed);
			BarbarianInteractions.SpawnBarbarians(gd);
		}

		foreach (MapUnit unit in barbarians.units) {
			Assert.Contains(unit.barbarianTribeId, campSlots);
			Assert.Equal(unit.barbarianTribeId, unit.location.barbarianTribeId);
			++spawned;
		}
		Assert.True(spawned > 0, "no camp ever spawned a unit");
	}

	// ---------------------------------------------------------------------
	// Units carry the tribe
	// ---------------------------------------------------------------------

	/// <summary>
	/// The unit's tribe id round-trips: a barbarian unit's slot survives the
	/// save format, and a non-barbarian unit has none.
	/// </summary>
	[Fact]
	public void TheUnitTribeIdRoundTripsThroughTheSaveFormat() {
		C7GameData.GameData gd = MakeGameData(out Player player, out Tile tile, band: 0, withNames: true);
		Player barbarians = AddBarbarianPlayer(gd);
		UnitPrototype warrior = new() { name = "Warrior" };
		warrior.categories.Add("Land");
		gd.unitPrototypes.Add(warrior);
		gd.experienceLevels[0].baseHitPoints = 2;
		gd.barbarianInfo.maxHitpoints = 2;

		gd.SpawnUnit(barbarians, warrior, tile, 9);
		MapUnit barbarian = barbarians.units.Single();
		Assert.Equal(9, barbarian.barbarianTribeId);

		SaveUnit saved = new(barbarian);
		Assert.Equal(9, saved.barbarianTribeId);

		MapUnit restored = saved.ToMapUnit(
			gd.unitPrototypes, gd.experienceLevels, gd.players, gd.Terraforms, gd.map);
		Assert.Equal(9, restored.barbarianTribeId);

		// A barbarian unit spawned with no tribe takes the fallback slot, the
		// way Civ3's unit constructor gives owner 0 + tribe -1 the fixed slot.
		gd.SpawnUnit(barbarians, warrior, tile, BarbarianTribes.None);
		Assert.Equal(BarbarianTribes.FallbackSlot, barbarians.units.Last().barbarianTribeId);

		MapUnit civilian = PlaceUnit(player, tile);
		Assert.Equal(BarbarianTribes.None, civilian.barbarianTribeId);
		Assert.Null(new SaveUnit(civilian).barbarianTribeId);
	}

	/// <summary>
	/// A hut's barbarian spawn carries the tribe whose name the message shows,
	/// and that tribe is drawn from the popping player's band.
	/// </summary>
	[Fact]
	public void HutBarbariansCarryTheTribeNameAndTheUnitsCarryTheSlot() {
		C7GameData.GameData gd = MakeGameData(out Player player, out Tile tile, band: 2, withNames: true);
		player.isHuman = true;
		Player barbarians = AddBarbarianPlayer(gd);
		gd.barbarianInfo.basicBarbarian = new UnitPrototype() { name = "Warrior" };
		gd.barbarianInfo.basicBarbarian.categories.Add("Land");
		player.cities.Add(new City(tile, player, "Test", ID.None("city")));
		PlaceUnit(player, tile);

		C7GameData.GameData.rng = new Random(2);
		DrainMessages();

		Assert.Equal(GoodyHutOutcome.Barbarians, GoodyHutInteractions.Apply(gd, player, tile, GoodyHutOutcome.Barbarians));

		int slot = SlotNamedIn(LastMessageText());
		Assert.True(BarbarianTribes.IsSlotInBand(slot, 2), $"slot {slot} is outside the player's band");
		Assert.NotEmpty(barbarians.units);
		foreach (MapUnit barbarian in barbarians.units) {
			Assert.Equal(slot, barbarian.barbarianTribeId);
		}

		// A hut pop holds nothing.
		Assert.Empty(BarbarianTribes.HeldSlots(gd.map));
	}

	/// <summary>
	/// The Civ3 save import reads the camp's tribe off the tile and the unit's
	/// tribe off the unit record. Measured on the Middle Ages scenario save: 7
	/// camp tiles carrying slots 0, 1 and 2, and 16 barbarian units carrying
	/// those same slots while every other unit carries -1.
	/// </summary>
	[SkippableFact]
	public void TheCiv3SaveImportCarriesTheCampAndUnitTribes() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");
		string savePath = PathUtils.getDataPath("saves/unit-availability/Middle Ages Scenario Abbasids, 843 AD.SAV");
		Skip.If(!File.Exists(savePath), "The Middle Ages save fixture is not present.");

		SaveGame save = ImportCiv3.ImportSav(savePath, PathUtils.defaultBicPath, (relativeModePath) =>
			Path.Combine(Civ3Location.GetCiv3Path(), "Conquests", "Conquests", "Middle Ages", "Text", "PediaIcons.txt"));

		List<SaveTile> camps = save.Map.tiles.Where(t => t.barbarianTribeId != null).ToList();
		Assert.Equal(7, camps.Count);
		Assert.Equal(new List<int> { 0, 1, 2 }, camps.Select(c => c.barbarianTribeId.Value).Distinct().OrderBy(v => v).ToList());

		// The camp bit and the tribe id are set on exactly the same tiles, so
		// reading the feature from one and the id from the other cannot drop a
		// camp or invent one.
		Assert.Equal(7, save.Map.tiles.Count(t => t.features.Contains("barbarianCamp")));
		Assert.All(camps, c => Assert.Contains("barbarianCamp", c.features));

		SaveTile campTile = save.Map.tiles.Find(t => t.X == 7 && t.Y == 103);
		Assert.Equal(1, campTile.barbarianTribeId);

		List<SaveUnit> barbarianUnits = save.Units.Where(u => u.barbarianTribeId != null).ToList();
		Assert.Equal(16, barbarianUnits.Count);
		Assert.All(barbarianUnits, u => Assert.InRange(u.barbarianTribeId.Value, 0, 2));
		Assert.All(save.Units, u => Assert.True(
			u.barbarianTribeId != null || save.Players.Find(p => p.id == u.owner).civilization != "A Barbarian Chiefdom",
			"a barbarian unit with no tribe"));
	}

	// ---------------------------------------------------------------------
	// Helpers
	// ---------------------------------------------------------------------

	private static GameMap Generate(BarbarianActivity activity) {
		return MapGenerator.GenerateMap(new WorldCharacteristics() {
			landform = WorldCharacteristics.Landform.Pangaea,
			oceanCoverage = WorldCharacteristics.OceanCoverage.Percent_70,
			age = WorldCharacteristics.Age.Billion_4,
			climate = WorldCharacteristics.Climate.Normal,
			temperature = WorldCharacteristics.Temperature.Temperate,
			barbarianActivity = activity,
			worldSize = new WorldSize() { width = 64, height = 64, numberOfCivs = 4, distanceBetweenCivs = 12 },
			terrainTypes = TerrainTypes(),
			mapSeed = 20260101,
		});
	}

	private static List<TerrainType> TerrainTypes() {
		return new List<TerrainType> {
			new() { Key = "grassland", movementCost = 1, allowCities = true },
			new() { Key = "plains", movementCost = 1, allowCities = true },
			new() { Key = "desert", movementCost = 1, allowCities = true },
			new() { Key = "tundra", movementCost = 1, allowCities = true },
			new() { Key = "coast", movementCost = 1, allowCities = false },
			new() { Key = "sea", movementCost = 1, allowCities = false },
			new() { Key = "ocean", movementCost = 1, allowCities = false },
			new() { Key = "forest", movementCost = 2, allowCities = true },
			new() { Key = "jungle", movementCost = 2, allowCities = true },
			new() { Key = "marsh", movementCost = 2, allowCities = true },
			new() { Key = "hills", movementCost = 2, allowCities = true },
			new() { Key = "volcano", movementCost = 3, allowCities = false },
			new() { Key = "mountains", movementCost = 3, allowCities = false },
		};
	}

	/// <summary>
	/// A tiny land map with a player whose civilization sits in the requested
	/// culture-group band. When <paramref name="withNames"/> is set the
	/// barbarian civilization carries the shipped 76-name table, so names can
	/// be asserted; otherwise it carries none, which is what a hand-built
	/// ruleset looks like.
	/// </summary>
	private static C7GameData.GameData MakeGameData(out Player player, out Tile tile, int band, bool withNames = false) {
		TerrainType grass = new() { Key = "grassland", movementCost = 1, allowCities = true, height = 1 };
		GameMap map = new() { numTilesWide = 8, numTilesTall = 8, tiles = new List<Tile>() };
		for (int i = 0; i < 64; ++i) {
			map.tileIndexToCoords(i, out int x, out int y);
			map.tiles.Add(new Tile(ID.None("tile")) {
				XCoordinate = x,
				YCoordinate = y,
				continent = 0,
				baseTerrainType = grass,
				overlayTerrainType = grass,
			});
		}
		map.computeNeighbors();

		C7GameData.GameData gd = new(customSeed: 7) {
			map = map,
			rules = new Rules() { MaxRankOfWorkableTiles = 2 },
			difficulties = new List<Difficulty> { new(), new(), new(), new() },
			eras = ShippedEras.Load(),
			barbarianInfo = new BarbarianInfo(),
			gameDifficulty = new Difficulty(),
		};
		gd.experienceLevels = new List<ExperienceLevel> { new("veteran", "Veteran", 2, 0, 0) };
		gd.defaultExperienceLevel = gd.experienceLevels[0];
		gd.citizenTypes = new List<CitizenType> { new() { IsDefaultCitizen = true } };
		gd.unitPrototypes.Add(gd.barbarianInfo.basicBarbarian = new UnitPrototype() { name = "Warrior" });
		gd.barbarianInfo.basicBarbarian.categories.Add("Land");

		Civilization barbarians = new("A Barbarian Chiefdom") { isBarbarian = true };
		if (withNames) {
			barbarians.cityNames.AddRange(ShippedTribeNames);
		}
		gd.civilizations.Add(barbarians);

		player = new Player() {
			civilization = new Civilization("Test"),
			government = new Government(),
		};
		player.civilization.SetCultureGroup(band, $"Band {band}");
		player.civilization.cityNames.Add("Test City");
		player.rules = gd.rules;
		gd.players.Add(player);

		tile = map.tileAt(4, 4);
		EngineStorage.InitializeGameDataForTests(gd);
		DrainMessages();
		return gd;
	}

	private static Player AddBarbarianPlayer(C7GameData.GameData gd) {
		Player barbarians = new() {
			civilization = gd.BarbarianCivilization,
			government = new Government(),
		};
		barbarians.rules = gd.rules;
		gd.players.Add(barbarians);
		return barbarians;
	}

	private static void SetCamp(C7GameData.GameData gd, Tile tile, int slot) {
		tile.hasBarbarianCamp = true;
		tile.barbarianTribeId = slot;
		gd.map.barbarianCamps.Add(tile);
	}

	private static MapUnit PlaceUnit(Player player, Tile tile) {
		UnitPrototype warrior = new() { name = "Warrior", attack = 1, defense = 1 };
		warrior.categories.Add("Land");
		MapUnit unit = new(ID.None("unit")) {
			name = "Test unit",
			unitType = warrior,
			owner = player,
			nationality = player.civilization,
		};
		unit.location = tile;
		tile.unitsOnTile.Add(unit);
		player.units.Add(unit);
		return unit;
	}

	/// <summary>The text of the last popup the engine sent, drained from the UI
	/// queue so each test sees only its own.</summary>
	private static string LastMessageText() {
		string text = null;
		while (EngineStorage.TryDequeueNextMessageToUI(out MessageToUI message)) {
			if (message is MsgShowMilitaryAdvisorPopup popup) {
				text = popup.message;
			}
		}
		return text;
	}

	private static void DrainMessages() {
		while (EngineStorage.TryDequeueNextMessageToUI(out _)) { }
	}

	/// <summary>
	/// The slot whose name the last message showed, read back out of the
	/// message text rather than out of the engine, so a wrong name cannot hide.
	/// </summary>
	/// <summary>
	/// The slot whose name the given message text shows, read back out of the
	/// text rather than out of the engine, so a wrong name cannot hide.
	/// </summary>
	private static int SlotNamedIn(string text) {
		Assert.NotNull(text);
		for (int slot = 0; slot < ShippedTribeNames.Length; ++slot) {
			if (text.Contains(ShippedTribeNames[slot])) {
				return slot;
			}
		}
		Assert.Fail($"no tribe name in \"{text}\"");
		return BarbarianTribes.None;
	}
}
