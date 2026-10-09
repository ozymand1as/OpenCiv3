using System.Collections.Generic;
using System.Linq;

namespace C7GameData {
	/// <summary>
	/// The barbarian tribe table (spec 22 sections 2, 3.2.5, 5.1 and 8.2).
	///
	/// <para>Civ3 keeps one global table of 76 tribe slots, one byte per slot,
	/// where a slot reads as "in use" exactly while a live camp holds it. The
	/// table is banded by culture group: slots
	/// <c>[cultureGroup * 15, cultureGroup * 15 + 14]</c> belong to culture
	/// group <c>cultureGroup</c>, the shipped rules have five culture groups
	/// (American, European, Mediterranean, Mid East, Asian), and slot 75 - the
	/// slot just past the last band - is the fixed fallback taken when every
	/// slot of a band is in use.</para>
	///
	/// <para>The names are not a list of their own. They are the barbarian
	/// race's own city-name list: <c>RACE 0</c>, "A Barbarian Chiefdom", whose
	/// 76 city names are the tribe names, read in slot order (measured out of
	/// the shipped conquests.biq with QueryCiv3, and already carried by
	/// C7/Lua/civ3/ruleset.json on the generated-game path). The band comes from
	/// the <em>popping</em> player's culture group, not from the barbarians'
	/// own, which the shipped rules leave as the "None" culture group.</para>
	///
	/// <para>What is not saved here: the in-use set is derived from the camps on
	/// the map (<see cref="HeldSlots"/>), because the only thing that ever holds
	/// a slot is a camp, the camp's slot id is a field of its tile, and the save
	/// already carries the tile. So a loaded game reconstructs the table from
	/// its own tiles, and there is no second copy of the state to keep in
	/// sync.</para>
	/// </summary>
	public static class BarbarianTribes {
		/// <summary>A tile, camp or unit that carries no tribe.</summary>
		public const int None = -1;

		/// <summary>The number of tribe slots in one culture group's band.</summary>
		public const int SlotsPerCultureGroup = 15;

		/// <summary>The number of culture groups the shipped rules define.</summary>
		public const int CultureGroupCount = 5;

		/// <summary>
		/// The fixed slot used when every slot of the requested band is in use.
		/// It is the slot just past the last band of the five culture groups,
		/// and its name is "Barbarian".
		/// </summary>
		public const int FallbackSlot = CultureGroupCount * SlotsPerCultureGroup;

		/// <summary>The size of the whole table, including the fallback slot.</summary>
		public const int SlotCount = FallbackSlot + 1;

		/// <summary>The first slot of a culture group's band.</summary>
		public static int BandStart(int cultureGroupIndex) {
			return cultureGroupIndex * SlotsPerCultureGroup;
		}

		/// <summary>Whether a slot belongs to a culture group's band.</summary>
		public static bool IsSlotInBand(int slot, int cultureGroupIndex) {
			return slot >= BandStart(cultureGroupIndex)
				&& slot < BandStart(cultureGroupIndex) + SlotsPerCultureGroup;
		}

		/// <summary>Whether a slot is inside the table at all.</summary>
		public static bool IsValidSlot(int slot) {
			return slot >= 0 && slot < SlotCount;
		}

		/// <summary>
		/// The culture group a civilization's band comes from. Civ3 reads the
		/// player's race record, so a civilization with no culture group (a
		/// hand-built one) is treated as the first band rather than failing;
		/// the barbarians' own "None" group never reaches this, because only a
		/// non-barbarian can pop a hut or own the nearest city.
		/// </summary>
		public static int CultureGroupIndex(Civilization civilization) {
			int index = civilization?.cultureGroup?.index ?? 0;
			return index < 0 || index >= CultureGroupCount ? 0 : index;
		}

		/// <summary>
		/// The first free slot of a band, scanning from a random offset and
		/// wrapping once around the band, or <see cref="FallbackSlot"/> when the
		/// whole band is in use. One scan covers all fifteen slots, including
		/// the offset itself, which is what the spec describes ("the first free
		/// slot ... starting from a random offset") and what the hut path in the
		/// binary does.
		///
		/// The camp-creation path in the binary scans only fourteen slots: its
		/// loop increments before its first test, so the offset slot itself is
		/// never chosen there. That is the original's own off-by-one, and both
		/// paths ask for the same thing; this implementation keeps one scan and
		/// the spec's meaning. The difference is a one-in-fifteen tie-break with
		/// no observable consequence beyond which name a camp draws.
		/// </summary>
		public static int PickTribeSlot(int cultureGroupIndex, ICollection<int> heldSlots, int randomOffset) {
			int band = BandStart(cultureGroupIndex);
			for (int i = 0; i < SlotsPerCultureGroup; ++i) {
				int slot = band + (randomOffset + i) % SlotsPerCultureGroup;
				if (heldSlots == null || !heldSlots.Contains(slot)) {
					return slot;
				}
			}

			// Every slot of the band is held. The binary uses the fixed slot
			// even when it is itself held, so two tribes can share the name.
			return FallbackSlot;
		}

		/// <summary>
		/// A pick with the offset drawn uniformly from the band, which is what
		/// every production caller does.
		/// </summary>
		public static int PickTribeSlot(int cultureGroupIndex, ICollection<int> heldSlots) {
			return PickTribeSlot(cultureGroupIndex, heldSlots, GameData.rng.Next(SlotsPerCultureGroup));
		}

		/// <summary>
		/// The slots the tribes in play currently hold: the tribe id of every
		/// camp on the map.
		/// </summary>
		public static List<int> HeldSlots(GameMap map) {
			if (map?.barbarianCamps == null) {
				return new List<int>();
			}
			return map.barbarianCamps
				.Where(camp => camp != null && IsValidSlot(camp.barbarianTribeId))
				.Select(camp => camp.barbarianTribeId)
				.ToList();
		}

		/// <summary>
		/// The word a message uses when the loaded rules carry no name for a
		/// tribe's slot. The shipped rules name all 76 slots, so this only stands
		/// in for a hand-built ruleset, or for a BIQ whose barbarian race has no
		/// name list.
		/// </summary>
		public const string UnnamedTribe = "barbarian";

		/// <summary>
		/// The tribe name at a slot, or<see cref="UnnamedTribe"/> when the loaded
		/// rules carry no name there.
		/// </summary>
		public static string TribeNameOrUnnamed(GameData gameData, int slot) {
			return TribeName(gameData, slot) ?? UnnamedTribe;
		}

		/// <summary>
		/// The tribe name at a slot: the barbarian civilization's city name at
		/// that index. Returns null when the slot is outside the table or the
		/// loaded rules carry no such name, so a caller falls back to a generic
		/// wording instead of showing a wrong or empty name.
		/// </summary>
		public static string TribeName(GameData gameData, int slot) {
			if (gameData == null || !IsValidSlot(slot)) {
				return null;
			}

			List<string> names = gameData.BarbarianCivilization?.cityNames;
			if (names == null || slot >= names.Count) {
				return null;
			}
			return string.IsNullOrEmpty(names[slot]) ? null : names[slot];
		}
	}
}
