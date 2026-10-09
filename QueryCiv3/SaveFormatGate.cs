using System;

namespace QueryCiv3 {
	/// <summary>
	/// Why the save reader refused a file. The original save reader refuses a file
	/// in exactly two whole-file cases, and the values below are its return codes
	/// for them; every other inconsistency inside a save it repairs or ignores.
	/// </summary>
	public enum SaveGateOutcome {
		/// <summary>The version gate accepted the file.</summary>
		Accepted = 0,

		/// <summary>The file does not begin with the save prologue, so it is not a save.</summary>
		PrologueMismatch = 2,

		/// <summary>The file is a save, but its format is one this engine does not read.</summary>
		UnsupportedVersion = 3,
	}

	/// <summary>
	/// The version a save declares, as the loader behaves by, or the reason the file
	/// was refused. Produced by <see cref="SaveFormatGate"/>.
	/// </summary>
	public readonly struct SaveHeader {
		/// <summary>The gate's decision.</summary>
		public SaveGateOutcome Outcome { get; }

		/// <summary>The save format's major version as stored, or 0 when none could be read.</summary>
		public int MajorVersion { get; }

		/// <summary>
		/// The minor version the loader behaves by. Zero for a file whose major version
		/// predates the stored minor version, whatever the bytes there happen to hold.
		/// </summary>
		public int MinorVersion { get; }

		/// <summary>
		/// True when the file carries the 16-byte GUID that follows the version from
		/// minor 7 on. Older saves carry none, and the original generates a fresh GUID
		/// for them instead of reading one.
		/// </summary>
		public bool HasStoredGuid { get; }

		/// <summary>Why the file was refused, or null when it was accepted.</summary>
		public string FailureMessage { get; }

		/// <summary>True when the version gate accepted the file.</summary>
		public bool Accepted => Outcome == SaveGateOutcome.Accepted;

		internal SaveHeader(SaveGateOutcome outcome, int majorVersion, int minorVersion, bool hasStoredGuid, string failureMessage) {
			Outcome = outcome;
			MajorVersion = majorVersion;
			MinorVersion = minorVersion;
			HasStoredGuid = hasStoredGuid;
			FailureMessage = failureMessage;
		}
	}

	/// <summary>
	/// The per-field layout a save's version selects. The save container expresses its
	/// version differences per field, inside the object readers, against the major and
	/// minor version of <see cref="SaveHeader"/> - not per section the way the
	/// scenario container does. Each difference therefore decides whether a field is in
	/// the file at all, and so how many bytes a record consumes; a reader that ignores
	/// one loses its place in the file rather than merely reading a wrong value.
	/// </summary>
	public readonly struct SaveFieldLayout {
		/// <summary>
		/// True when a city record stores its date sub-record. That sub-record (a tagged
		/// `DATE` chunk of 84 payload bytes) only exists from save format 17.04 on: the
		/// city reader tests the minor version and, below 4, computes the value instead of
		/// reading it, so no bytes for it are present in the file (the city reader's
		/// `minor &lt; 4` test at 0x4bc34c in the shipped build).
		/// </summary>
		public bool CityStoresDateSubRecord { get; }

		/// <summary>
		/// True when a city record stores the 4-byte field added in save format 20. The
		/// city reader tests the major version and, below 20, zeroes the field and reads
		/// nothing: the field's chunk - which also carries the city record's own revision,
		/// read at 0x4bc39b - is not in the file, and neither are the arrays and
		/// objects that revision gates.
		/// </summary>
		public bool CityStoresFormat20Field { get; }

		/// <summary>
		/// The number of bytes occupied by the block that sits between the city data and
		/// the per-player (`PALV`) array. From save format 17.04 on it is
		/// <see cref="SaveFormatGate.WorldTileBlockLength"/> bytes; older saves carry 8
		/// more, so the reader advances 0x108 instead (`move_game_data` at 0x590030, the
		/// `minor &lt; 4` test that overrides the 0x100 advance).
		/// </summary>
		public int WorldTileBlockLength { get; }

		/// <summary>
		/// True when the city record's date is computed rather than stored, i.e. for
		/// formats older than save format 17.04. The derivation itself reads city state
		/// (`+0x54` through the helpers at 0x5df710/0x5df100) that this model does not
		/// carry, so the parser only honours the size half of the rule: the sub-record is
		/// not consumed and the date fields keep their defaults.
		/// </summary>
		public bool CityDerivesDateSubRecord => !CityStoresDateSubRecord;

		internal SaveFieldLayout(bool cityStoresDateSubRecord, bool cityStoresFormat20Field, int worldTileBlockLength) {
			CityStoresDateSubRecord = cityStoresDateSubRecord;
			CityStoresFormat20Field = cityStoresFormat20Field;
			WorldTileBlockLength = worldTileBlockLength;
		}
	}

	/// <summary>
	/// The version gate of the save container (`.sav`): a file is accepted only if it
	/// starts with the save prologue and declares a version the engine knows, and the
	/// minor version is not read at all for formats that predate the field. Every other
	/// inconsistency in a save is repaired or ignored, so this is the whole of the
	/// acceptance test. Keeping it here means the byte parser and the engine loader
	/// agree on what a readable save is.
	/// </summary>
	public static class SaveFormatGate {
		// The prologue is the ASCII text CIV3 followed by its NUL terminator. The byte
		// after that (0x1A in every file the game writes) is skipped by the reader, not
		// compared, so it is not part of the acceptance test.
		public const int PrologueLength = 6;

		/// <summary>Offset of the major version, immediately after the prologue.</summary>
		public const int MajorVersionOffset = PrologueLength;

		/// <summary>
		/// Offset of the minor version, which is only stored by formats from
		/// <see cref="MinorVersionFirstStoredMajor"/> on.
		/// </summary>
		public const int MinorVersionOffset = MajorVersionOffset + sizeof(int);

		/// <summary>The oldest major version the engine reads.</summary>
		public const int MinimumMajorVersion = 14;

		/// <summary>The major version from which a save stores a minor version at all.</summary>
		public const int MinorVersionFirstStoredMajor = 17;

		/// <summary>The minor version from which a save stores the 16-byte GUID.</summary>
		public const int GuidFirstStoredMinor = 7;

		/// <summary>The number of bytes the GUID occupies when the file stores one.</summary>
		public const int GuidLength = 16;

		// BLOCKER: the GUID rule of spec 28 section 2.1 (rule 5, "the GUID is replaced,
		// not validated" below minor 7) has two halves, and only one of them is portable.
		//
		// The ALIGNMENT half is implemented: whether the file stores the sixteen-byte GUID
		// decides where its body - the first tagged chunk - begins, because the GUID sits
		// between the version and that chunk. <see cref="BodyOffset(int, int)"/> advances
		// over exactly the bytes the header table gives the file's version: 30 for the
		// shipped build's 24.10 (measured on every cached save: the body starts with the
		// 'BIC ' wrapper chunk), 14 for a 24.03 save (the major 17+ minor dword, no GUID),
		// and 10 below major 17 (no minor dword either). Before this rule was derived the
		// reader used a single constant calibrated for the modern header, so a spec-shaped
		// older save was read from the wrong position.
		//
		// The VALUE half - generating a fresh GUID below minor 7 and throwing the file's
		// away - is not implemented, and cannot be here without inventing state: OpenCiv3
		// has no save GUID. Neither the engine's game data nor its save model carries such
		// a field, and no code reads or writes one, so there is no value for the rule to
		// act on. A GUID field added only so that the loader could replace it would be
		// invented behaviour rather than a ported rule; the loader therefore records
		// whether the file stores one (<see cref="SaveHeader.HasStoredGuid"/>) and leaves
		// the replacement undone.

		/// <summary>
		/// The offset of the body - the first tagged chunk - in a save of this version,
		/// from the header table of spec 28 section 2.1 and the reader that implements it
		/// (the save-header reader, <c>0x5920e0</c>: it consumes the prologue, reads the major
		/// version, reads the minor version only when the major is at least 17, reads the
		/// GUID only when the minor is at least 7, and hands the body to <c>move_game_data</c>
		/// at whatever offset that leaves): the prologue's six bytes, the major version's
		/// four, the minor version's four only from major 17 on, and the GUID's sixteen only
		/// from minor 7 on. A minor version is not stored below major 17 (the loader behaves
		/// as 0 there), so such a file can never carry a GUID.
		/// </summary>
		public static int BodyOffset(int majorVersion, int minorVersion) {
			int offset = MajorVersionOffset + sizeof(int); // the prologue and the major version
			if (majorVersion < MinorVersionFirstStoredMajor) {
				return offset;
			}
			offset += sizeof(int); // the minor version dword
			if (minorVersion >= GuidFirstStoredMinor) {
				offset += GuidLength; // the GUID
			}
			return offset;
		}

		/// <summary>The body offset of a save with this header.</summary>
		public static int BodyOffset(SaveHeader header) => BodyOffset(header.MajorVersion, header.MinorVersion);

		/// <summary>
		/// The offset inside the body of the dword that states the embedded BIQ's length.
		/// The body opens with the 'BIC ' wrapper chunk: an eight-byte tag-and-length
		/// header whose 524-byte payload begins with that dword. Measured on every cached
		/// save: the wrapper's own length field reads 524 and the embedded length at this
		/// offset is the byte count of the 'BICQ' BIQ that follows it.
		/// </summary>
		public const int EmbeddedBicLengthOffsetInBody = 8;

		/// <summary>The payload length of the 'BIC ' wrapper chunk that precedes the embedded BIQ.</summary>
		public const int EmbeddedBicWrapperLength = 524;

		/// <summary>
		/// The offset inside the body of the embedded BIQ's first byte: past the eight-byte
		/// 'BIC ' wrapper header and its 524-byte payload.
		/// </summary>
		public const int EmbeddedBicStartOffsetInBody = EmbeddedBicLengthOffsetInBody + EmbeddedBicWrapperLength;

		/// <summary>The offset of the embedded BIQ's length dword in a save of this version.</summary>
		public static int EmbeddedBicLengthOffset(int majorVersion, int minorVersion) =>
			BodyOffset(majorVersion, minorVersion) + EmbeddedBicLengthOffsetInBody;

		/// <summary>The offset of the embedded BIQ's first byte in a save of this version.</summary>
		public static int EmbeddedBicStartOffset(int majorVersion, int minorVersion) =>
			BodyOffset(majorVersion, minorVersion) + EmbeddedBicStartOffsetInBody;

		/// <summary>The embedded BIQ's length dword offset for a save with this header.</summary>
		public static int EmbeddedBicLengthOffset(SaveHeader header) => EmbeddedBicLengthOffset(header.MajorVersion, header.MinorVersion);

		/// <summary>The embedded BIQ's first byte offset for a save with this header.</summary>
		public static int EmbeddedBicStartOffset(SaveHeader header) => EmbeddedBicStartOffset(header.MajorVersion, header.MinorVersion);

		/// <summary>The minor version from which a city record stores its date sub-record.</summary>
		public const int CityDateSubRecordFirstMinor = 4;

		/// <summary>The major version from which a city record stores the format-20 field.</summary>
		public const int CityFormat20FieldFirstMajor = 20;

		/// <summary>
		/// The first minor version from which the block before the per-player array is the
		/// short one. Older formats carry 8 more bytes (see
		/// <see cref="WorldTileBlockLengthBeforeMinor4"/>).
		/// </summary>
		public const int WorldTileBlockFirstMinor = 4;

		/// <summary>
		/// The length of the block between the city data and the per-player array from
		/// save format 17.04 on, and the length older formats give it (8 bytes more).
		/// </summary>
		public const int WorldTileBlockLength = 0x100;
		public const int WorldTileBlockLengthBeforeMinor4 = 0x108;

		// BLOCKER: spec 28 section 5.1's "minor version < 4 (world/tile blocks)" row
		// has two effects, and only the size half is ported. The row reads "skip 8 extra
		// bytes and omit one sub-field"; the second effect is the sub-field the row refers
		// to. The row's wording is oriented from the modern reader's side: in the file a
		// minor < 4 save CARRIES the extra sub-field in those eight bytes and a modern save
		// omits it (the block advances 0x108 rather than 0x100). It is the field those eight
		// extra bytes carry, which the loader never reads into its model. The size half is
		// implemented as <see cref="WorldTileBlockLengthBeforeMinor4"/>: move_game_data at
		// 0x590030 copies the block's 0x100 bytes (0x40 dwords) and
		// then advances the cursor 0x108 instead of 0x100 for minor < 4.
		//
		// The omitted sub-field itself is not ported, and cannot be named from the binary
		// as shipped: spec 28's row names neither the field nor the record it belongs to,
		// and its only cited evidence is that advance. At that site the 0x100 bytes are
		// read into the block whatever the minor is, so no field is read conditionally; the
		// only other minor < 4 test in the binary is the city reader's derived value
		// (the city reader at 0x4bbed0), which is the separate row already
		// ported as <see cref="SaveFieldLayout.CityDerivesDateSubRecord"/>. The save TILE
		// reader loop that could carry the field is itself an open item in spec 28 section
		// 7.1. The blocker is therefore recorded by what the container shows: the
		// world/tile block's sub-field that a minor < 4 save carries and a modern save does
		// not, skipped rather than read into the model.

		/// <summary>
		/// The per-field layout of a save with this header: which city fields are present,
		/// and how long the block before the per-player array is.
		/// </summary>
		public static SaveFieldLayout FieldLayout(SaveHeader header) => FieldLayout(header.MajorVersion, header.MinorVersion);

		/// <summary>
		/// The per-field layout of a save with this version pair, using the loader's own
		/// version values: a minor version is only meaningful from major 17 on, and callers
		/// pass the minor the gate reports for the file.
		/// </summary>
		public static SaveFieldLayout FieldLayout(int majorVersion, int minorVersion) {
			return new SaveFieldLayout(
				cityStoresDateSubRecord: minorVersion >= CityDateSubRecordFirstMinor,
				cityStoresFormat20Field: majorVersion >= CityFormat20FieldFirstMajor,
				worldTileBlockLength: minorVersion < WorldTileBlockFirstMinor
					? WorldTileBlockLengthBeforeMinor4
					: WorldTileBlockLength);
		}

		/// <summary>
		/// Applies the version gate to a save file's header.
		/// </summary>
		public static SaveHeader Check(byte[] saveBytes) {
			if (!HasPrologue(saveBytes)) {
				return new SaveHeader(SaveGateOutcome.PrologueMismatch, 0, 0, false,
					"the file does not begin with the save prologue, the text CIV3 followed by a NUL byte");
			}

			if (!TryReadInt32(saveBytes, MajorVersionOffset, out int major)) {
				return new SaveHeader(SaveGateOutcome.UnsupportedVersion, 0, 0, false,
					$"the file ends inside its version header: it holds {saveBytes.Length} bytes, too few to read a major version");
			}

			if (major < MinimumMajorVersion) {
				return new SaveHeader(SaveGateOutcome.UnsupportedVersion, major, 0, false,
					$"save format {major} is older than the oldest the engine reads ({MinimumMajorVersion})");
			}

			if (major < MinorVersionFirstStoredMajor) {
				// Formats before 17 do not store a minor version: the original forces it to
				// 0 instead of reading the bytes in its place.
				return new SaveHeader(SaveGateOutcome.Accepted, major, 0, false, null);
			}

			if (!TryReadInt32(saveBytes, MinorVersionOffset, out int minor)) {
				return new SaveHeader(SaveGateOutcome.UnsupportedVersion, major, 0, false,
					$"the file ends inside its version header: save format {major} stores a minor version, but the file holds only {saveBytes.Length} bytes");
			}

			// The only check on the value: a stored minor version of 1 is never valid.
			if (minor == 1) {
				return new SaveHeader(SaveGateOutcome.UnsupportedVersion, major, minor, false,
					$"save format {major}.1 is not a version the engine accepts");
			}

			return new SaveHeader(SaveGateOutcome.Accepted, major, minor, minor >= GuidFirstStoredMinor, null);
		}

		private static bool HasPrologue(byte[] saveBytes) {
			if (saveBytes == null || saveBytes.Length < PrologueLength - 1) {
				return false;
			}
			return saveBytes[0] == (byte)'C' && saveBytes[1] == (byte)'I' && saveBytes[2] == (byte)'V'
				&& saveBytes[3] == (byte)'3' && saveBytes[4] == 0;
		}

		private static bool TryReadInt32(byte[] saveBytes, int offset, out int value) {
			if (offset < 0 || offset + sizeof(int) > saveBytes.Length) {
				value = 0;
				return false;
			}
			value = BitConverter.ToInt32(saveBytes, offset);
			return true;
		}
	}
}
