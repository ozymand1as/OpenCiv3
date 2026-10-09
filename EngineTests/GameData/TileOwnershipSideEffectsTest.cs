using System;
using System.IO;
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
/// The side effects of a territory ownership assignment (spec 17 sections 5
/// and 5.1; spec 22 sections 4.6 and 6.3). Civ3's owner writer runs them when a
/// tile is GIVEN a non-zero owner and the owner actually changes: a camp on the
/// tile is dispersed, with the 25 gold going to the civ that took the tile, and
/// a hut on it is forced to the City outcome, becoming a city of that civ. A
/// tile whose owner is only cleared runs neither handler, so a raze cannot
/// convert the tiles that merely lose their owner.
/// </summary>
public class TileOwnershipSideEffectsTest {

	// ------------------------------------------------------------------ fixture

	/// <summary>
	/// The capture fixture is reused: two living civilizations with a
	/// relationship, an 8x8 grassland map, a fixed RNG and the engine globals
	/// installed.
	/// </summary>
	private static C7GameData.GameData NewGame(out Player claimer, out Player rival) {
		C7GameData.GameData gd = CityCaptureTest.Game.NewGameData(out claimer, out rival);
		// The hut City outcome names the city it founds, so both civilizations
		// need a name list.
		claimer.civilization.cityNames.Add("Claimer City");
		rival.civilization.cityNames.Add("Rival City");
		return gd;
	}

	/// <summary>
	/// A tile one step from the city, which a level-1 border claims, with a
	/// guard so the test cannot pass by putting the feature outside the border.
	/// </summary>
	private static Tile ClaimedNeighbor(City city, int neighborIndex) {
		Tile tile = city.location.GetTileAtNeighborIndex(neighborIndex);
		Assert.Contains(tile, city.GetTilesWithinBorders());
		return tile;
	}

	// -------------------------------------------------- the assigning half

	/// <summary>
	/// The rule itself: a hut on a tile the border sweep gives to a civ becomes
	/// a city of that civ, because the unit-less entry path forces the City
	/// outcome (spec 22 section 4.6).
	/// </summary>
	[Fact]
	public void AHutInsideNewlyClaimedBordersBecomesACityOfTheClaimingCiv() {
		C7GameData.GameData gd = NewGame(out Player claimer, out _);
		claimer.civilization.traits.Add(Civilization.Trait.Expansionist);
		City city = CityCaptureTest.Game.AddCity(gd, claimer, gd.map.tileAt(4, 4), population: 1);
		Tile hut = ClaimedNeighbor(city, 3);
		hut.hasGoodyHut = true;

		gd.UpdateTileOwners();

		Assert.False(hut.hasGoodyHut);
		Assert.True(hut.HasCity());
		Assert.Equal(claimer, hut.cityAtTile.owner);
	}

	/// <summary>
	/// The camp rule: the camp is gone, its tribe slot is freed and the 25 gold
	/// goes to the civ that took the tile, not to any unit (spec 22 section 6.3
	/// - the owner writer passes the new owner's leader object).
	/// </summary>
	[Fact]
	public void ACampInsideNewlyClaimedBordersIsDispersedForTheClaimingCiv() {
		C7GameData.GameData gd = NewGame(out Player claimer, out Player rival);
		City city = CityCaptureTest.Game.AddCity(gd, claimer, gd.map.tileAt(4, 4), population: 1);
		Tile camp = ClaimedNeighbor(city, 7);
		camp.hasBarbarianCamp = true;
		camp.barbarianTribeId = 7;
		gd.map.barbarianCamps.Add(camp);
		Assert.Contains(7, BarbarianTribes.HeldSlots(gd.map));

		gd.UpdateTileOwners();

		Assert.False(camp.hasBarbarianCamp);
		Assert.Equal(BarbarianTribes.None, camp.barbarianTribeId);
		Assert.DoesNotContain(camp, gd.map.barbarianCamps);
		Assert.Equal(BarbarianCampInteractions.DispersalGold, claimer.gold);
		Assert.Equal(0, rival.gold);
		Assert.DoesNotContain(7, BarbarianTribes.HeldSlots(gd.map));
	}

	/// <summary>
	/// A capture changes the owner of every tile the city already holds, and
	/// Civ3's owner writer compares the tile's stored owner against the owner it
	/// is writing, so the captured city's tiles follow the assigning path even
	/// though their owning city never changed (spec 17 section 5.1). The gold
	/// therefore goes to the capturer.
	/// </summary>
	[Fact]
	public void ACampInACapturedCitysBordersPaysTheNewOwner() {
		C7GameData.GameData gd = NewGame(out Player capturer, out Player loser);
		City city = CityCaptureTest.Game.AddCity(gd, loser, gd.map.tileAt(4, 4), population: 1);
		gd.UpdateTileOwners();

		Tile camp = ClaimedNeighbor(city, 3);
		Assert.Same(city, camp.owningCity);
		camp.hasBarbarianCamp = true;
		camp.barbarianTribeId = 9;
		gd.map.barbarianCamps.Add(camp);

		CityInteractions.CaptureCity(city, capturer);

		Assert.Equal(capturer, city.owner);
		Assert.False(camp.hasBarbarianCamp);
		Assert.Equal(BarbarianCampInteractions.DispersalGold, capturer.gold);
		Assert.Equal(0, loser.gold);
	}

	/// <summary>
	/// The same capture rule for a hut: it founds its city for the capturer.
	/// </summary>
	[Fact]
	public void AHutInACapturedCitysBordersBecomesACityOfTheNewOwner() {
		C7GameData.GameData gd = NewGame(out Player capturer, out Player loser);
		capturer.civilization.traits.Add(Civilization.Trait.Expansionist);
		City city = CityCaptureTest.Game.AddCity(gd, loser, gd.map.tileAt(4, 4), population: 1);
		gd.UpdateTileOwners();

		Tile hut = ClaimedNeighbor(city, 3);
		Assert.Same(city, hut.owningCity);
		hut.hasGoodyHut = true;

		CityInteractions.CaptureCity(city, capturer);

		Assert.Equal(capturer, city.owner);
		Assert.True(hut.HasCity());
		Assert.Equal(capturer, hut.cityAtTile.owner);
	}

	// ---------------------------------------------------- the clearing half

	/// <summary>
	/// A raze clears the owner of the razed city's tiles without giving them to
	/// anyone, and Civ3's clearing writes ask for owner 0, which runs neither
	/// handler (spec 17 sections 5 and 5.1). The two features must survive the
	/// clear untouched.
	/// </summary>
	[Fact]
	public void ARazedCitysTilesDoNotConvert() {
		C7GameData.GameData gd = NewGame(out Player claimer, out _);
		claimer.civilization.traits.Add(Civilization.Trait.Expansionist);
		City city = CityCaptureTest.Game.AddCity(gd, claimer, gd.map.tileAt(4, 4), population: 1);
		gd.UpdateTileOwners();

		// Both features appear after the city already owns its borders, so the
		// only sweep that can consume them is the raze's.
		Tile hut = ClaimedNeighbor(city, 3);
		Tile camp = ClaimedNeighbor(city, 7);
		hut.hasGoodyHut = true;
		camp.hasBarbarianCamp = true;
		camp.barbarianTribeId = 7;
		gd.map.barbarianCamps.Add(camp);
		Assert.Same(city, hut.owningCity);
		Assert.Same(city, camp.owningCity);

		CityInteractions.DestroyCity(city.location);

		Assert.Null(hut.owningCity);
		Assert.Null(camp.owningCity);
		Assert.True(hut.hasGoodyHut);
		Assert.False(hut.HasCity());
		Assert.True(camp.hasBarbarianCamp);
		Assert.Equal(7, camp.barbarianTribeId);
		Assert.Equal(0, claimer.gold);
		Assert.Contains(camp, gd.map.barbarianCamps);
	}

	// -------------------------------------------------------- re-entrancy

	/// <summary>
	/// Founding a city from a hut runs the border sweep again, and the sweep is
	/// itself walking the city list while it assigns tiles. Two huts in one
	/// city's borders must therefore produce exactly two cities, and a second
	/// sweep must produce none: the handler clears the hut bit before it acts,
	/// and the recorded change is re-checked against the tile's final owner.
	/// </summary>
	[Fact]
	public void AHutConversionFoundsEachCityOnceAndDoesNotRecurse() {
		C7GameData.GameData gd = NewGame(out Player claimer, out _);
		claimer.civilization.traits.Add(Civilization.Trait.Expansionist);
		City city = CityCaptureTest.Game.AddCity(gd, claimer, gd.map.tileAt(4, 4), population: 1);
		Tile first = ClaimedNeighbor(city, 3);
		Tile second = ClaimedNeighbor(city, 7);
		Assert.NotSame(first, second);
		first.hasGoodyHut = true;
		second.hasGoodyHut = true;

		gd.UpdateTileOwners();

		Assert.True(first.HasCity());
		Assert.True(second.HasCity());
		Assert.Equal(claimer, first.cityAtTile.owner);
		Assert.Equal(claimer, second.cityAtTile.owner);
		Assert.Equal(3, gd.cities.Count);

		// The huts are gone, so no later sweep can found anything else.
		gd.UpdateTileOwners();

		Assert.Equal(3, gd.cities.Count);
	}

	/// <summary>
	/// The forced City outcome keeps the City outcome's own guards (spec 22
	/// section 4.5): a non-Expansionist civilization that takes a hut tile is
	/// not handed a city, because the resolver re-rolls a rejected City outcome
	/// instead of applying it. The hut is still consumed - it always pops once.
	/// </summary>
	[Fact]
	public void TheForcedCityOutcomeKeepsTheCityGuards() {
		C7GameData.GameData gd = NewGame(out Player claimer, out _);
		City city = CityCaptureTest.Game.AddCity(gd, claimer, gd.map.tileAt(4, 4), population: 1);
		Tile hut = ClaimedNeighbor(city, 3);
		hut.hasGoodyHut = true;

		gd.UpdateTileOwners();

		Assert.False(hut.hasGoodyHut);
		Assert.False(hut.HasCity());
		Assert.Single(gd.cities);
	}

	// ------------------------------------------------------ the movement path

	/// <summary>
	/// The movement path keeps its behaviour after the dispersal moved into the
	/// shared handler: same tile state, same 25 gold, same freed slot. This test
	/// passes before the change as well; it is the guard on the refactor.
	/// </summary>
	[Fact]
	public void TheMovementPathStillDispersesACampTheSameWay() {
		C7GameData.GameData gd = NewGame(out Player mover, out Player rival);
		Tile tile = gd.map.tileAt(4, 4);
		tile.hasBarbarianCamp = true;
		tile.barbarianTribeId = 11;
		gd.map.barbarianCamps.Add(tile);
		MapUnit unit = CityCaptureTest.Game.PlaceUnit(gd, mover, tile);

		unit.OnEnterTile(tile);

		Assert.False(tile.hasBarbarianCamp);
		Assert.Equal(BarbarianTribes.None, tile.barbarianTribeId);
		Assert.DoesNotContain(tile, gd.map.barbarianCamps);
		Assert.Equal(BarbarianCampInteractions.DispersalGold, mover.gold);
		Assert.Equal(0, rival.gold);
		Assert.DoesNotContain(11, BarbarianTribes.HeldSlots(gd.map));
	}

	// ------------------------------------------------ the toggle's carriers

	/// <summary>
	/// The City outcome's rule toggle (bit 0x400 of the toggleable-rules word,
	/// spec 22 sections 4.5 and 8.1) reaches the rules object from the BIQ.
	/// "3 MP Fall of Rome" is the one shipped BIQ that sets the bit, so the
	/// second half is distinguishable from the field's own default.
	/// </summary>
	[SkippableFact]
	public void TheBiqImportCarriesTheHutCityToggle() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");

		// Positive control: a shipped BIQ that clears bit 0x400 leaves the
		// outcome enabled.
		string cleared = Path.Combine(Civ3Location.GetCiv3Path(), "Conquests", "Conquests", "1 Mesopotamia.biq");
		Assert.False(BiqData.LoadFile(cleared).Game[0].CityElimination);
		Assert.True(ImportScenario("Conquests/Conquests", "1 Mesopotamia.biq").Rules.AllowCitiesFromGoodyHuts);

		string set = Path.Combine(Civ3Location.GetCiv3Path(), "Conquests", "Scenarios", "3 MP Fall of Rome.biq");
		Assert.True(BiqData.LoadFile(set).Game[0].CityElimination);
		Assert.False(ImportScenario("Conquests/Scenarios", "3 MP Fall of Rome.biq").Rules.AllowCitiesFromGoodyHuts);
	}

	private static SaveGame ImportScenario(string subfolder, string fileName) {
		string path = Path.Combine(Civ3Location.GetCiv3Path(), subfolder, fileName);
		EngineStorage.animationsEnabled = false;
		return ImportCiv3.ImportBiq(path, PathUtils.defaultBicPath, GetPediaIconsPath(subfolder));
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

	/// <summary>
	/// The same toggle reaches a game that has no BIQ, through ruleset.json -
	/// the carrier set a generated game reads. This never skips.
	/// </summary>
	[Fact]
	public void RulesetJsonCarriesTheHutCityToggle() {
		using System.Text.Json.JsonDocument ruleset = JsonUtils.LoadBaseRuleset();

		Assert.True(ruleset.RootElement.GetProperty("rules").GetProperty("allowCitiesFromGoodyHuts").GetBoolean());
	}

	/// <summary>
	/// And the toggle is read by the forced-by-territory path: with it off, a
	/// hut inside newly claimed borders is consumed but founds no city.
	/// </summary>
	[Fact]
	public void TheForcedCityOutcomeIsSuppressedByTheToggle() {
		C7GameData.GameData gd = NewGame(out Player claimer, out _);
		claimer.civilization.traits.Add(Civilization.Trait.Expansionist);
		gd.rules.AllowCitiesFromGoodyHuts = false;
		City city = CityCaptureTest.Game.AddCity(gd, claimer, gd.map.tileAt(4, 4), population: 1);
		Tile hut = ClaimedNeighbor(city, 3);
		hut.hasGoodyHut = true;

		gd.UpdateTileOwners();

		Assert.False(hut.hasGoodyHut);
		Assert.False(hut.HasCity());
		Assert.Single(gd.cities);
	}
}
