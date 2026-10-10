using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using C7Engine;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using Xunit;

namespace EngineTests.GameData;

/// <summary>
/// Guards the step executor's first gate: a unit type with the Immobile ability
/// (PRTO ability 0xA, Flags1[1] bit 2) cannot take a step at all, and neither
/// can an army whose lead member's type is immobile (11_movement.md §5 step 1).
///
/// The original tests the unit's own type at 0x5b902c, the lead member's type at
/// 0x5b9048 and the unit's own type alone at 0x5b9070 when there is no lead
/// member, and every hit returns 1 (refused) from 0x5b9051 - before the
/// destination is resolved at 0x5b9081 and therefore before the zone-of-control
/// call at 0x5b9434.
///
/// The flag is not inert in the shipped rules: all seven air unit types, the
/// ICBM and the regicide Princess carry it, and the ICBM and the Princess are
/// land-classed with movement 1, so without this gate they walk. The tests below
/// fail against the code before the gate existed: an immobile unit simply moved.
/// </summary>
public sealed class ImmobileStepTest : IClassFixture<SaveGameFixture> {
	private const int SourceX = 50;
	private const int SourceY = 50;
	private const int DestinationX = 52;
	private const int DestinationY = 50;
	private const int BandX = 51;
	private const int BandY = 49;
	private const int SouthBandY = 51;

	private readonly C7GameData.GameData gd;

	public ImmobileStepTest(SaveGameFixture fixture) {
		gd = fixture.saveGame.ToGameData(fixture.behaviors);
		EngineStorage.InitializeGameDataForTests(gd);
		// Move waits on an animation-complete message no test UI sends.
		EngineStorage.animationsEnabled = false;
	}

	private class ScriptedRandom : Random {
		public readonly List<int> requestedBounds = new();
		public int fallback = 1023;

		public override int Next(int maxValue) {
			requestedBounds.Add(maxValue);
			return fallback % maxValue;
		}

		public override double NextDouble() => 0.5;
	}

	private ScriptedRandom UseScriptedRandom(int roll = 1023) {
		ScriptedRandom rng = new ScriptedRandom { fallback = roll };
		C7GameData.GameData.rng = rng;
		return rng;
	}

	// ---------- helpers ----------

	private Player MakePlayer(bool barbarian = false) {
		Civilization civ = new Civilization(barbarian ? "Barbarians" : "TestCiv") {
			noun = "TestCivs",
			adjective = "Test",
			isBarbarian = barbarian,
		};
		civ.traits = [];
		return new Player {
			id = gd.GenerateID("player"),
			civilization = civ,
			rules = gd.rules,
			government = new Government(),
		};
	}

	private Tile CleanTile(int x, int y) {
		Tile tile = gd.map.tileAt(x, y);
		if (tile.cityAtTile != null) {
			tile.cityAtTile = null;   // clears the overlays as a side effect
		}
		tile.unitsOnTile.Clear();
		tile.overlays.Clear();
		tile.hasGoodyHut = false;
		tile.hasBarbarianCamp = false;
		tile.overlayTerrainType = new TerrainType { Key = "test" };
		tile.baseTerrainType = tile.overlayTerrainType;
		return tile;
	}

	// The source, the destination and both tiles of the zone-of-control scan's
	// band, so nothing the generator left behind can add an unexpected threat.
	private (Tile source, Tile destination, Tile band) PrepareLandStep() {
		Tile source = CleanTile(SourceX, SourceY);
		Tile destination = CleanTile(DestinationX, DestinationY);
		CleanTile(BandX, SouthBandY);
		return (source, destination, CleanTile(BandX, BandY));
	}

	private MapUnit MakeUnit(Player owner, UnitPrototype proto, Tile tile, int hitPoints = 3) {
		MapUnit unit = proto.GetInstance(gd.GenerateID(proto.name), proto, owner, location: tile, hitPoints: hitPoints);
		unit.experienceLevel = gd.experienceLevels.First(e => e.key == "Regular");
		unit.experienceLevelKey = "Regular";
		unit.hitPointsRemaining = hitPoints;
		tile.unitsOnTile.Add(unit);
		gd.mapUnits.Add(unit);
		owner.AddUnit(unit);
		return unit;
	}

	private static UnitPrototype MakeLandPrototype(string name, int attack = 0, int defense = 1,
			bool immobile = false, bool zoneOfControl = false, int movement = 2) {
		UnitPrototype proto = new UnitPrototype {
			name = name,
			attack = attack,
			defense = defense,
			movement = movement,
		};
		proto.categories.Add("Land");
		if (immobile) proto.flags.Add(SaveUnitPrototype.Flag.Immobile);
		if (zoneOfControl) proto.flags.Add(SaveUnitPrototype.Flag.ZoneOfControl);
		return proto;
	}

	// A player who is at war with the mover and has seen the mover's tile, which
	// is what a zone-of-control candidate needs before it may fire
	// (11_movement.md §6.2).
	private Player MakeSeeingEnemy(MapUnit mover) {
		Player enemy = MakePlayer(barbarian: true);
		enemy.tileKnowledge.knownTiles.Add(mover.location);
		return enemy;
	}

	// ---------- the gate ----------

	[Fact]
	public async Task AnImmobileUnitTypeCannotTakeAStep() {
		(Tile source, Tile destination, _) = PrepareLandStep();
		MapUnit unit = MakeUnit(MakePlayer(), MakeLandPrototype("Immobile", immobile: true), source);
		Assert.True(unit.IsImmobile);
		float before = unit.movementPoints.remaining;

		Assert.False(await unit.Move(TileDirection.EAST));

		Assert.Equal(SourceX, unit.location.XCoordinate);
		Assert.Equal(source, unit.location);
		Assert.Equal(before, unit.movementPoints.remaining);
	}

	// The same step with the flag clear, so the refusal above is the flag and not
	// something about the tiles.
	[Fact]
	public async Task TheSameUnitWithoutTheFlagTakesTheStep() {
		(Tile source, Tile destination, _) = PrepareLandStep();
		MapUnit unit = MakeUnit(MakePlayer(), MakeLandPrototype("Mobile"), source);
		Assert.False(unit.IsImmobile);

		Assert.True(await unit.Move(TileDirection.EAST));

		Assert.Equal(destination, unit.location);
	}

	// Step 1 precedes step 5, so an immobile unit never pays a zone-of-control
	// hit: the original returns 1 at 0x5b9051, long before the call at 0x5b9434.
	// The step is otherwise identical to AnOrdinaryStepFiresTheRule in
	// ZoneOfControlTest, where the same setup costs the mover a hit point.
	[Fact]
	public async Task TheImmobileRefusalPrecedesTheZoneOfControlCall() {
		(Tile source, Tile destination, Tile band) = PrepareLandStep();
		MapUnit mover = MakeUnit(MakePlayer(), MakeLandPrototype("Immobile", immobile: true, movement: 1), source);
		MakeUnit(MakeSeeingEnemy(mover), MakeLandPrototype("Threat", attack: 10, zoneOfControl: true), band);
		Assert.Equal(10, mover.ZoneOfControlThreatStrength(destination));

		ScriptedRandom rng = UseScriptedRandom(700);
		Assert.False(await mover.Move(TileDirection.EAST));

		Assert.Equal(3, mover.hitPointsRemaining);
		Assert.Empty(rng.requestedBounds);
		Assert.Equal(SourceX, mover.location.XCoordinate);
	}

	// ---------- the lead-member clause ----------

	// Unit_has_ability @ 0x5bc8b0 and the step executor's own test both consult
	// the army's lead member's type, so an army carrying an immobile type is
	// immobile even though the Army type itself is not.
	[Fact]
	public async Task AnArmyWhoseLeadMemberIsImmobileCannotStep() {
		(Tile source, Tile destination, _) = PrepareLandStep();
		Player player = MakePlayer();
		MapUnit army = MakeArmy(player, source);
		MapUnit member = MakeUnit(player, MakeLandPrototype("ImmobileMember", immobile: true), source);
		Assert.True(member.LoadIntoArmy(army));

		Assert.False(army.unitType.immobile);
		Assert.True(army.IsImmobile);

		Assert.False(await army.Move(TileDirection.EAST));

		Assert.Equal(source, army.location);
		Assert.NotEqual(destination, army.location);
	}

	// The lead-member lookup yields nothing when the members' types differ
	// (the lookup at 0x5bc6d0 returns -1 at 0x5bc81a as soon as two members' type
	// ids
	// differ), and the caller then reads only the army's own record - so a mixed
	// army is not immobile.
	[Fact]
	public async Task AMixedArmyIsNotImmobile() {
		(Tile source, Tile destination, _) = PrepareLandStep();
		Player player = MakePlayer();
		MapUnit army = MakeArmy(player, source);
		Assert.True(MakeUnit(player, MakeLandPrototype("ImmobileMember", immobile: true), source).LoadIntoArmy(army));
		Assert.True(MakeUnit(player, MakeLandPrototype("MobileMember"), source).LoadIntoArmy(army));

		Assert.Null(army.LeadMemberType());
		Assert.False(army.IsImmobile);

		Assert.True(await army.Move(TileDirection.EAST));

		Assert.Equal(destination, army.location);
	}

	private MapUnit MakeArmy(Player player, Tile tile) {
		UnitPrototype armyType = new UnitPrototype { name = "TestArmy", movement = 1, capacity = 3 };
		armyType.flags.Add(SaveUnitPrototype.Flag.Army);
		armyType.categories.Add("Land");

		MapUnit army = armyType.GetInstance(gd.GenerateID("TestArmy"), armyType, player, location: tile);
		army.experienceLevel = gd.experienceLevels.First(e => e.key == "Regular");
		army.experienceLevelKey = "Regular";
		army.hitPointsRemaining = army.maxHitPoints;
		tile.unitsOnTile.Add(army);
		gd.mapUnits.Add(army);
		player.AddUnit(army);
		return army;
	}

	// ---------- the shipped rules ----------

	// The shipped rules are what makes this gate matter: the ICBM and the
	// regicide Princess are land-classed with movement 1 and carry the ability,
	// so before this gate they walked across the map.
	[Theory]
	[InlineData("ICBM")]
	[InlineData("Princess")]
	public async Task TheShippedLandCarriersOfImmobileCannotStep(string name) {
		(Tile source, Tile destination, _) = PrepareLandStep();
		UnitPrototype proto = gd.unitPrototypes.First(p => p.name == name);
		Assert.True(proto.immobile);
		Assert.Contains("Land", proto.categories);

		MapUnit unit = MakeUnit(MakePlayer(), proto, source);
		Assert.True(unit.IsImmobile);

		Assert.False(await unit.Move(TileDirection.EAST));

		Assert.Equal(source, unit.location);
		Assert.NotEqual(destination, unit.location);
	}

	// The air carriers reach the same gate, though the fork's passability rules
	// already refuse an air unit's step. This pins the data path, not a second
	// behaviour: the flag is on the runtime prototype either way.
	[Fact]
	public void TheShippedAirCarriersAlsoCarryTheFlag() {
		foreach (string name in new[] {
			"Fighter", "Bomber", "Helicopter", "Jet Fighter", "Stealth Fighter",
			"Stealth Bomber", "F-15",
		}) {
			UnitPrototype proto = gd.unitPrototypes.First(p => p.name == name);

			Assert.True(proto.immobile, name);
			Assert.Contains("Air", proto.categories);
		}
	}
}
