using System;
using System.Collections.Generic;
using System.Linq;
using C7GameData;
using C7GameData.Save;
using Serilog;

namespace C7Engine;

public struct SelectedOpponent {
	public bool isRandom;
	public string Name;
}

public class GameSetup {
	private static ILogger log = Log.ForContext<GameSetup>();

	public Civilization playerCivilization { get; init; }
	public Difficulty difficulty { get; init; }
	public WorldCharacteristics worldCharacteristics { get; init; }
	public List<SelectedOpponent> opponents { get; init; } = [];
	public VictoryConditions victoryConditions { get; set; }

	ID.Factory ids;

	public void Populate(SaveGame save) {
		save.GameDifficulty = difficulty;

		save.VictoryConditions = victoryConditions;

		if (save.Map.tiles.Count == 0) {
			log.Information("Starting map generation");
			save.Map = new SaveMap(MapGenerator.GenerateMap(worldCharacteristics));
			save.Seed = worldCharacteristics.mapSeed;
			log.Information("Done with map generation");
		}

		if (save.Players.Count == 0) {
			ids = new(save);
			PopulatePlayers(save);
		}
	}

	private void PopulatePlayers(SaveGame save) {
		// Every player starts in an era, and the era list is rules data: the
		// BIQ's ERAS section, or the game mode's ruleset.json. A rules file with
		// no eras cannot start a game, so say which file is missing what here,
		// where the game mode is known, instead of letting the player loop
		// throw an index error.
		string startingEra = EraUtils.GetStartingEraCivilopediaName(save.Eras);
		if (startingEra == null) {
			string rulesLocation = save.GameModeConfig?.baseModeDir is string dir
				? $"Lua/{dir}/ruleset.json"
				: "the game mode's ruleset.json";
			throw new InvalidOperationException(
				$"The rules define no eras, so a new game has no era to put its players in: " +
				$"{rulesLocation} has no \"eras\" array. Add one - each entry needs a " +
				"civilopediaName, a name and an artName, in era order, like the shipped four in " +
				"C7/Lua/civ3/ruleset.json - or start the game from a BIQ scenario, whose ERAS " +
				"section supplies the list.");
		}

		Random rand = new(worldCharacteristics.mapSeed + 0x531);

		// Add barbarian
		AddPlayer(save, save.Civilizations.Find(c => c.isBarbarian), isHuman: false, startingEra);
		save.BarbarianInfo.barbarianActivity = worldCharacteristics.barbarianActivity;

		// TODO: There is an option called "Culturally Linked Start Loc."
		// which (if on) puts players with the same culture group near each other

		// Add the human player.
		AddPlayer(save, this.playerCivilization, isHuman: true, startingEra);

		// Add the opponents.
		HashSet<string> taken = new();
		taken.Add(this.playerCivilization.name);

		foreach (SelectedOpponent opponent in opponents) {
			bool isRandom = opponent.isRandom;
			string selectedName = opponent.Name;

			if (taken.Contains(opponent.Name)) {
				isRandom = true;
			}

			if (isRandom) {
				do {
					selectedName = save.Civilizations[rand.Next(1, save.Civilizations.Count)].name;
				} while (taken.Contains(selectedName));
			}
			taken.Add(selectedName);

			Civilization civ = save.Civilizations.Find(x => x.name == selectedName);
			AddPlayer(save, civ, isHuman: false, startingEra);
		}
	}

	private void AddPlayer(SaveGame save, Civilization civ, bool isHuman, string startingEra) {
		SavePlayer player = new() {
			human = isHuman,
			id = ids.CreateID("Player"),
			primaryColorIndex = civ.primaryColorIndex,
			secondaryColorIndex = civ.secondaryColorIndex,
			civilization = civ.name,
			knownTechs = civ.startingTechs,
			// The first era of the rules' era list is where a new game starts;
			// PopulatePlayers resolved it, and refused to start a game whose
			// rules carry no era list.
			eraCivilopediaName = startingEra,
			// TODO: load this from the rules
			gold = 10,
			governmentId = worldCharacteristics.defaultGovernment.id,
		};
		save.Players.Add(player);

		if (civ.isBarbarian) {
			player.canBePicked = false;
			return;
		}

		SaveTile startingTile = save.Map.startingLocations[save.Players.Count - 2];
		TileLocation startingLocation = new TileLocation(startingTile.X, startingTile.Y);

		AddUnit(save, player, save.Rules.StartUnitType1, startingLocation);
		AddUnit(save, player, save.Rules.StartUnitType2, startingLocation);
		if (civ.traits.Contains(Civilization.Trait.Expansionist)) {
			AddUnit(save, player, save.Rules.ScoutUnitType, startingLocation);
		}
	}

	private void AddUnit(SaveGame save, SavePlayer player, string unitType, TileLocation location) {
		var defaultExpLevelKey = save.DefaultExperienceLevel;

		var defaultExpLevel = save.ExperienceLevels.First(e => e.key == defaultExpLevelKey);
		var barbExpLevel = save.ExperienceLevels.First(e => e.baseHitPoints == save.BarbarianInfo.maxHitpoints);

		var proto = save.UnitPrototypes.First(p => p.name == unitType);

		SaveUnit unit = new() {
			id = ids.CreateID(unitType),
			name = unitType,
			nationality = player.civilization,
			prototype = unitType,
			owner = player.id,
			previousLocation = new TileLocation(-1, -1),
			currentLocation = location,

			hitPointsRemaining = (player.isBarbarian ? barbExpLevel.baseHitPoints : defaultExpLevel.baseHitPoints) + proto.hpBonus,
			movePointsRemaining = proto.movement,
			experience = player.isBarbarian ? barbExpLevel.key : defaultExpLevel.key,

			facingDirection = TileDirection.SOUTHEAST,
		};
		save.Units.Add(unit);
	}
}
