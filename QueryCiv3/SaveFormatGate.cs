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
