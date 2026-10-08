using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using C7GameData;
using EngineTests.Utils;
using QueryCiv3;
using QueryCiv3.Sav;
using Xunit;

namespace EngineTests;

// The save version gate: which save headers the engine accepts, and the version it
// then behaves by. The rules are the original save reader's: the file must begin with
// the save prologue, the major version must be at least 14, and a major version of 17
// or more stores a minor version whose value 1 is never valid. Below 17 no minor
// version is stored at all and the loader forces it to 0. On top of the acceptance
// test sit the per-field rules of section 2.3: the version decides whether a city
// stores its date sub-record and its format-20 field, and how long the block before
// the per-player array is, so it decides how many bytes a record consumes. The same
// version decides where the body starts (section 2.1's header table): the minor
// version dword exists only from major 17 on and the 16-byte GUID only from minor 7
// on, so neither the body's start nor the embedded BIQ's offsets are constant.
public class SaveFormatGateTests {
	private static readonly byte[] SavePrologue = { (byte)'C', (byte)'I', (byte)'V', (byte)'3', 0x00, 0x1A };

	// The header layout, measured from every cached save under data/saves: the
	// prologue's NUL is byte 4, the marker byte the writer puts after it (0x1A) is
	// byte 5, the major version is the four bytes at 6 and the minor version the four
	// at 10. These are literals on purpose. Building the synthetic headers from
	// SaveFormatGate's own offset constants would make a transcription error there
	// self-consistent and invisible to every test; the cached saves pin them instead.
	private const int PrologueNulOffset = 4;
	private const int PrologueMarkerOffset = 5;
	private const int LiteralMajorVersionOffset = 6;
	private const int LiteralMinorVersionOffset = 10;

	// prologue + major version + minor version + the 16-byte GUID a modern save stores
	private const int SaveHeaderLength = 30;

	// Section 2.1's header table as literal thresholds, so the fixtures below are built
	// to the table rather than to the production constants and a transcription error in
	// either cannot hide behind the other.
	private const int LiteralMinorFirstStoredMajor = 17;
	private const int LiteralGuidFirstStoredMinor = 7;
	private const int LiteralGuidLength = 16;

	private static byte[] Header(byte[] prologue, int major, int storedMinor) {
		// Fill with a value that is neither a plausible version nor a plausible GUID
		// first, so a test can tell whether a field was read or ignored.
		byte[] bytes = new byte[SaveHeaderLength];
		for (int i = 0; i < bytes.Length; i++) {
			bytes[i] = 0xAB;
		}
		Array.Copy(prologue, bytes, prologue.Length);
		BitConverter.GetBytes(major).CopyTo(bytes, LiteralMajorVersionOffset);
		BitConverter.GetBytes(storedMinor).CopyTo(bytes, LiteralMinorVersionOffset);
		return bytes;
	}

	private static byte[] SaveHeader(int major, int storedMinor) => Header(SavePrologue, major, storedMinor);

	[Fact]
	public void AcceptsTheVersionTheShippedBuildWrites() {
		// The Civ3 Conquests v1.22 build writes every save as 24.10, so that is the one
		// version pair a save produced by the game has to come back through.
		SaveHeader header = SaveFormatGate.Check(SaveHeader(24, 10));

		Assert.True(header.Accepted, header.FailureMessage);
		Assert.Equal(24, header.MajorVersion);
		Assert.Equal(10, header.MinorVersion);
		Assert.True(header.HasStoredGuid);
		Assert.Null(header.FailureMessage);
	}

	[Theory]
	// Formats 14 through 16 predate the stored minor version, so the bytes at its
	// offset are not a version and the loader behaves by 0.
	[InlineData(14, 0, 0)]
	[InlineData(14, 99, 0)]
	[InlineData(15, -1, 0)]
	[InlineData(16, 12345, 0)]
	[InlineData(17, 0, 0)]
	[InlineData(17, 2, 2)]
	[InlineData(17, 6, 6)]
	[InlineData(17, 7, 7)]
	[InlineData(18, 10, 10)]
	[InlineData(24, 10, 10)]
	[InlineData(99, 5, 5)]
	// The one value the minor version may never hold is exactly 1; every other value,
	// including a negative one, is taken as written.
	[InlineData(24, -3, -3)]
	public void AcceptsEveryVersionTheEngineReads(int major, int storedMinor, int expectedMinor) {
		SaveHeader header = SaveFormatGate.Check(SaveHeader(major, storedMinor));

		Assert.True(header.Accepted, header.FailureMessage);
		Assert.Equal(major, header.MajorVersion);
		Assert.Equal(expectedMinor, header.MinorVersion);
	}

	[Theory]
	[InlineData(13)]
	[InlineData(12)]
	[InlineData(2)]
	[InlineData(0)]
	[InlineData(-1)]
	public void RejectsMajorVersionsOlderThanFourteen(int major) {
		SaveHeader header = SaveFormatGate.Check(SaveHeader(major, 0));

		Assert.False(header.Accepted);
		Assert.Equal(SaveGateOutcome.UnsupportedVersion, header.Outcome);
		Assert.Equal(major, header.MajorVersion);
		Assert.Contains("older than", header.FailureMessage);
	}

	[Theory]
	[InlineData(17)]
	[InlineData(18)]
	[InlineData(24)]
	public void RejectsAStoredMinorVersionOfOne(int major) {
		SaveHeader header = SaveFormatGate.Check(SaveHeader(major, 1));

		Assert.False(header.Accepted);
		Assert.Equal(SaveGateOutcome.UnsupportedVersion, header.Outcome);
		Assert.Equal(1, header.MinorVersion);
		Assert.Contains($"{major}.1", header.FailureMessage);
	}

	[Fact]
	public void AppliesTheMinorVersionRuleOnlyToFormatsThatStoreAMinorVersion() {
		// 17.1 and later are refused, but a format-16 save does not store a minor
		// version at all: whatever sits at that offset is not read, so the same value
		// cannot refuse the file.
		Assert.True(SaveFormatGate.Check(SaveHeader(16, 1)).Accepted);
		Assert.False(SaveFormatGate.Check(SaveHeader(17, 1)).Accepted);
	}

	[Theory]
	[InlineData("CIVX", 0x00, 0x1A)] // the scenario container's magic is not a save
	[InlineData("civ3", 0x00, 0x1A)] // the prologue is case-sensitive
	[InlineData("CIV3", 0x20, 0x1A)] // the fifth byte must be the NUL terminator
	[InlineData("BIC ", 0x00, 0x00)] // a .biq header is not a save
	public void RejectsFilesWithoutTheSavePrologue(string text, byte fifth, byte sixth) {
		byte[] prologue = { (byte)text[0], (byte)text[1], (byte)text[2], (byte)text[3], fifth, sixth };
		SaveHeader header = SaveFormatGate.Check(Header(prologue, 24, 10));

		Assert.Equal(SaveGateOutcome.PrologueMismatch, header.Outcome);
		Assert.False(header.Accepted);
		Assert.Contains("prologue", header.FailureMessage);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(4)]
	[InlineData(5)]
	public void RejectsABufferTooShortToHoldThePrologue(int length) {
		SaveHeader header = SaveFormatGate.Check(new byte[length]);

		Assert.Equal(SaveGateOutcome.PrologueMismatch, header.Outcome);
	}

	[Fact]
	public void RejectsANullBufferWithoutThrowing() {
		SaveHeader header = SaveFormatGate.Check(null);

		Assert.Equal(SaveGateOutcome.PrologueMismatch, header.Outcome);
	}

	[Fact]
	public void AcceptsAFileWhosePrologueMarkerByteIsNotTheWrittenOne() {
		// The reader compares the five bytes of "CIV3" plus its NUL and then skips one
		// byte, so the 0x1A the writer puts there is never compared. A save with
		// something else at offset 5 is still accepted.
		byte[] prologue = { (byte)'C', (byte)'I', (byte)'V', (byte)'3', 0x00, 0x7F };
		SaveHeader header = SaveFormatGate.Check(Header(prologue, 24, 10));

		Assert.True(header.Accepted, header.FailureMessage);
		Assert.Equal(24, header.MajorVersion);
		Assert.Equal(10, header.MinorVersion);
	}

	[Theory]
	[InlineData(6)] // the prologue alone
	[InlineData(9)] // part of the major version
	[InlineData(10)] // a major version, but no minor version for a format that stores one
	[InlineData(13)]
	public void RefusesATruncatedHeaderInsteadOfThrowing(int length) {
		byte[] bytes = SaveHeader(24, 10);
		Array.Resize(ref bytes, length);

		SaveHeader header = SaveFormatGate.Check(bytes);

		Assert.False(header.Accepted);
		Assert.Equal(SaveGateOutcome.UnsupportedVersion, header.Outcome);
		Assert.Contains("ends inside", header.FailureMessage);
	}

	[Theory]
	[InlineData(24, 6, false)]
	[InlineData(24, 7, true)]
	[InlineData(24, 10, true)]
	[InlineData(17, 0, false)]
	// Nothing is stored below format 17, so no old save carries a GUID.
	[InlineData(16, 12345, false)]
	public void ReportsWhetherTheFileStoresAGuid(int major, int storedMinor, bool expected) {
		Assert.Equal(expected, SaveFormatGate.Check(SaveHeader(major, storedMinor)).HasStoredGuid);
	}

	[Fact]
	public void TheParserReportsTheVersionTheGateBehavesBy() {
		// A format-15 save does not store a minor version. The bytes at its offset are
		// not a version, and reporting them as one would disagree with the loader.
		Civ3File old = new Civ3File(SaveHeader(15, 12345));

		Assert.True(old.IsGameFile);
		Assert.Equal(15, old.Civ3Version.MajorVersion);
		Assert.Equal(0, old.Civ3Version.MinorVersion);

		Civ3File modern = new Civ3File(SaveHeader(24, 10));
		Assert.Equal(24, modern.Civ3Version.MajorVersion);
		Assert.Equal(10, modern.Civ3Version.MinorVersion);
	}

	[Fact]
	public void LoadingSomethingThatIsNotASaveFailsWithThePrologueRule() {
		AssertRefused(Encoding.ASCII.GetBytes("This is a text file that was renamed to .SAV"), "prologue");
	}

	[Fact]
	public void LoadingASaveFromAnUnsupportedVersionFailsWithTheVersionRule() {
		AssertRefused(SaveHeader(13, 0), "13");
	}

	[Fact]
	public void LoadingASaveWithAMinorVersionOfOneFailsWithTheVersionRule() {
		AssertRefused(SaveHeader(24, 1), "24.1");
	}

	private static void AssertRefused(byte[] bytes, string expectedInMessage) {
		string path = Path.Combine(Path.GetTempPath(), $"c7-save-gate-{Guid.NewGuid():N}.SAV");
		File.WriteAllBytes(path, bytes);
		try {
			// The ruleset path is deliberately unusable: a refused save must be refused
			// before the engine reaches for the ruleset, and must say which rule refused
			// it rather than fail with a parse error deep in the byte reader.
			InvalidDataException ex = Assert.Throws<InvalidDataException>(() =>
				ImportCiv3.ImportSav(path, "/nonexistent/conquests.biq", scenarioSearchPath => "/nonexistent/PediaIcons.txt"));

			Assert.Contains(expectedInMessage, ex.Message);
			Assert.Contains(path, ex.Message);
		} finally {
			File.Delete(path);
		}
	}

	// ---- the per-field version rules, measured against the cached saves ----

	[Fact]
	public void EveryCachedSaveCarriesItsVersionAtTheLiteralOffsets() {
		foreach (string path in CachedSavePaths()) {
			byte[] bytes = Util.ReadFile(path);

			// The prologue, the NUL and the writer's marker, then the two version dwords.
			Assert.Equal((byte)'C', bytes[0]);
			Assert.Equal((byte)'I', bytes[1]);
			Assert.Equal((byte)'V', bytes[2]);
			Assert.Equal((byte)'3', bytes[3]);
			Assert.Equal(0x00, bytes[PrologueNulOffset]);
			Assert.Equal(0x1A, bytes[PrologueMarkerOffset]);
			Assert.Equal(24, BitConverter.ToInt32(bytes, LiteralMajorVersionOffset));
			Assert.Equal(10, BitConverter.ToInt32(bytes, LiteralMinorVersionOffset));

			// And the gate reads the same version out of those same bytes.
			SaveHeader header = SaveFormatGate.Check(bytes);
			Assert.True(header.Accepted, header.FailureMessage);
			Assert.Equal(24, header.MajorVersion);
			Assert.Equal(10, header.MinorVersion);
		}
	}

	[Theory]
	// Section 2.1's header table as arithmetic: prologue 6, major version 4 always,
	// minor version 4 only from major 17 on, GUID 16 only from minor 7 on. The shipped
	// build writes 24.10, so its body starts at 30; an older spec-shaped save's is 14
	// or 10, and a constant calibrated for 30 reads it from the wrong position.
	[InlineData(24, 10, 30)]
	[InlineData(24, 7, 30)]
	[InlineData(24, 6, 14)]
	[InlineData(19, 5, 14)]
	[InlineData(17, 0, 14)]
	[InlineData(16, 12345, 10)]
	[InlineData(14, -1, 10)]
	public void TheBodyAndEmbeddedBicOffsetsFollowTheHeaderTable(int major, int storedMinor, int expectedBodyOffset) {
		SaveHeader header = SaveFormatGate.Check(SaveHeader(major, storedMinor));

		Assert.Equal(expectedBodyOffset, SaveFormatGate.BodyOffset(header));
		// The embedded BIQ's length dword and first byte sit inside the body, so both move
		// with it: the dword after the 'BIC ' wrapper's own 8-byte header, the BIQ past the
		// wrapper's 524-byte payload.
		Assert.Equal(expectedBodyOffset + 8, SaveFormatGate.EmbeddedBicLengthOffset(header));
		Assert.Equal(expectedBodyOffset + 532, SaveFormatGate.EmbeddedBicStartOffset(header));
	}

	[Fact]
	public void EveryCachedSavesDerivedBodyOffsetPointsAtItsEmbeddedBic() {
		foreach (string path in CachedSavePaths()) {
			byte[] bytes = Util.ReadFile(path);
			SaveHeader header = SaveFormatGate.Check(bytes);
			Assert.Equal(24, header.MajorVersion);
			Assert.Equal(10, header.MinorVersion);

			// The derived body offset is where the body really starts: the first chunk is the
			// 'BIC ' wrapper, whose own length field says 524.
			int body = SaveFormatGate.BodyOffset(header);
			Assert.Equal(30, body);
			Assert.Equal("BIC ", Encoding.ASCII.GetString(bytes, body, 4));
			Assert.Equal(524, BitConverter.ToInt32(bytes, body + 4));

			// The derived embedded-BIQ offsets locate the embedded BIQ: it begins with the
			// scenario magic BICQ and is exactly as long as the dword states, so the next
			// save chunk - the GAME section - starts where it ends.
			int biqStart = SaveFormatGate.EmbeddedBicStartOffset(header);
			int biqLength = BitConverter.ToInt32(bytes, SaveFormatGate.EmbeddedBicLengthOffset(header));
			Assert.True(biqLength > 0);
			Assert.Equal("BICQ", Encoding.ASCII.GetString(bytes, biqStart, 4));
			Assert.Equal("GAME", Encoding.ASCII.GetString(bytes, biqStart + biqLength, 4));
		}
	}

	[Fact]
	public void TheShippedBuildsVersionPairSelectsTheModernFieldLayout() {
		SaveFieldLayout layout = SaveFormatGate.FieldLayout(SaveFormatGate.Check(SaveHeader(24, 10)));

		Assert.True(layout.CityStoresDateSubRecord);
		Assert.True(layout.CityStoresFormat20Field);
		Assert.False(layout.CityDerivesDateSubRecord);
		Assert.Equal(0x100, layout.WorldTileBlockLength);
	}

	[Fact]
	public void MinorVersionBeforeFourRemovesTheDateSubRecordAndLengthensTheBlock() {
		SaveFieldLayout layout = SaveFormatGate.FieldLayout(SaveFormatGate.Check(SaveHeader(17, 3)));

		Assert.False(layout.CityStoresDateSubRecord);
		Assert.True(layout.CityDerivesDateSubRecord);
		Assert.Equal(0x108, layout.WorldTileBlockLength);
		// The format-20 rule is a major-version rule: a format 17.03 save is below it.
		Assert.False(layout.CityStoresFormat20Field);

		// A major version below 17 does not store a minor version at all, so the loader
		// behaves as minor 0 and takes the same older layout.
		SaveFieldLayout old = SaveFormatGate.FieldLayout(SaveFormatGate.Check(SaveHeader(16, 10)));
		Assert.False(old.CityStoresDateSubRecord);
		Assert.Equal(0x108, old.WorldTileBlockLength);
	}

	[Fact]
	public void MajorVersionBeforeTwentyRemovesTheFormat20Field() {
		SaveFieldLayout nineteen = SaveFormatGate.FieldLayout(SaveFormatGate.Check(SaveHeader(19, 10)));

		Assert.True(nineteen.CityStoresDateSubRecord);
		Assert.False(nineteen.CityStoresFormat20Field);
		Assert.Equal(0x100, nineteen.WorldTileBlockLength);
	}

	[Fact]
	public void MinorVersionBeforeFourReadsTheEightByteLongerBlockBeforeThePerPlayerArray() {
		byte[] modern = Util.ReadFile(PathUtils.getDataPath("saves/12345.SAV"));
		List<string> expected = Snapshot(ParseSave(modern));

		// The same save as a format 24.3 file: the version pair in a header that is
		// spec-shaped for it (no GUID below minor 7), and the eight extra bytes a format
		// that old carries at the end of the block before the per-player array. Nothing
		// else about the file changes, so only the block rule and the version's body
		// offset can explain the parse still landing on every later section.
		byte[] older = RestateHeader(modern, 24, 3);
		AssertSpecShapedForVersion(older, 24, 3);
		older = InsertBytes(older, PerPlayerBlockStart(older) + SaveFormatGate.WorldTileBlockLength, 8);
		Assert.Equal(3, SaveFormatGate.Check(older).MinorVersion);

		Assert.Equal(expected, Snapshot(ParseSave(older)));
	}

	[Fact]
	public void MinorVersionBeforeFourCityRecordStoresNoDateSubRecord() {
		byte[] modern = Util.ReadFile(PathUtils.getDataPath("saves/multi-turn-deals/MultiTurnDeal_Save_A.SAV"));
		List<string> expected = Snapshot(ParseSave(modern));

		byte[] older = RestateHeader(modern, 24, 3);
		AssertSpecShapedForVersion(older, 24, 3);
		older = InsertBytes(older, PerPlayerBlockStart(older) + SaveFormatGate.WorldTileBlockLength, 8);
		List<int> dates = CityDateSubRecordOffsets(older);
		Assert.NotEmpty(dates);
		older = RemoveRanges(older, dates.Select(offset => (offset, DateSubRecordLength)).ToList());
		Assert.Equal(3, SaveFormatGate.Check(older).MinorVersion);

		// Every city still reads the same; the load only survives because the 92 bytes
		// of each date sub-record are no longer taken from the file.
		Assert.Equal(expected, Snapshot(ParseSave(older)));
	}

	[Fact]
	public void MajorVersionBeforeTwentyCityRecordHasNoFormat20FieldOrTail() {
		byte[] modern = Util.ReadFile(PathUtils.getDataPath("saves/unit-availability/Middle Ages Scenario Abbasids, 843 AD.SAV"));
		List<string> expected = Snapshot(ParseSave(modern));

		// This save's format-20 field really is non-zero for some cities, so the rule is
		// observable in the values and not only in the byte count.
		Assert.Contains(expected, line => !line.EndsWith("format20=0 rev=0"));

		// The same save as a format 19.5 file: the version pair in a header that is
		// spec-shaped for it (major 17+ does store a minor version, but minor 5 stores no
		// GUID, so the header is 14 bytes and the body starts there rather than at 30),
		// and each city's version-gated tail removed. Below save format 20 the loader
		// zeroes the field and behaves as revision 0, so that tail is not in the file at
		// all. Minor 5 is at or above every other threshold, so the format-20 rule is the
		// only one this fixture exercises.
		byte[] older = RestateHeader(modern, 19, 5);
		AssertSpecShapedForVersion(older, 19, 5);
		older = RemoveRanges(older, CityTailRanges(older));
		Assert.Equal(19, SaveFormatGate.Check(older).MajorVersion);
		Assert.Equal(5, SaveFormatGate.Check(older).MinorVersion);

		List<string> actual = Snapshot(ParseSave(older));
		Assert.Equal(expected.Count, actual.Count);
		for (int i = 0; i < actual.Count; i++) {
			if (actual[i].StartsWith("city ")) {
				// The version pair is the only difference: the field is zeroed, the
				// revision that gates the tail is 0, and everything else is identical.
				Assert.Equal(StripVersionFields(expected[i]), StripVersionFields(actual[i]));
				Assert.EndsWith("format20=0 rev=0", actual[i]);
			} else {
				Assert.Equal(expected[i], actual[i]);
			}
		}
	}

	// ---- helpers for the fixture surgery ----

	private const int DateSubRecordLength = 92; // a DATE chunk: 8-byte header plus 84 payload bytes

	/// <summary>
	/// Parses a save the way the engine does. The rules file is the one every other
	/// save test in this suite uses; the save then loads its own embedded rule
	/// sections over it, so the values below do not depend on which rules file it is.
	/// </summary>
	private static SavData ParseSave(byte[] savBytes) {
		return new SavData(savBytes, Util.ReadFile(PathUtils.defaultBicPath));
	}

	private static List<string> CachedSavePaths() {
		List<string> paths = Directory.EnumerateFiles(PathUtils.getDataPath("saves"), "*.SAV", SearchOption.AllDirectories).ToList();
		Assert.NotEmpty(paths);
		return paths;
	}

	/// <summary>
	/// How many bytes a spec-shaped header occupies for this version, from section
	/// 2.1's table: the six prologue bytes, the four-byte major version, the four-byte
	/// minor version only from major 17 on (below that the loader behaves as 0, so no
	/// minor is stored), and the sixteen-byte GUID only from minor 7 on.
	/// </summary>
	private static int HeaderLength(int major, int minor) {
		int length = LiteralMajorVersionOffset + sizeof(int); // the prologue and the major version
		if (major < LiteralMinorFirstStoredMajor) {
			return length;
		}
		length += sizeof(int); // the minor version dword
		if (minor >= LiteralGuidFirstStoredMinor) {
			length += LiteralGuidLength; // the GUID
		}
		return length;
	}

	/// <summary>
	/// Restates a modern save's version as this pair, keeping only the header bytes that
	/// version stores: it drops the minor version dword below major 17 and the GUID below
	/// minor 7 instead of leaving them in place. The rest of the file is the modern body,
	/// shifted to follow the shorter (or equal) header. Declaring an older version on a
	/// file that still carries the modern header would not be spec-shaped, and would let
	/// a hardcoded body offset pass unnoticed.
	/// </summary>
	private static byte[] RestateHeader(byte[] bytes, int major, int minor) {
		int length = HeaderLength(major, minor);
		byte[] result = new byte[bytes.Length - SaveHeaderLength + length];
		Array.Copy(bytes, 0, result, 0, LiteralMajorVersionOffset); // the prologue, incl. its marker byte
		BitConverter.GetBytes(major).CopyTo(result, LiteralMajorVersionOffset);
		int cursor = LiteralMajorVersionOffset + sizeof(int);
		if (major >= LiteralMinorFirstStoredMajor) {
			BitConverter.GetBytes(minor).CopyTo(result, cursor);
			cursor += sizeof(int);
			if (minor >= LiteralGuidFirstStoredMinor) {
				// The GUID is at 14 and is the only part of the modern header an older
				// version may still store, so it is copied rather than generated.
				Array.Copy(bytes, LiteralMinorVersionOffset + sizeof(int), result, cursor, LiteralGuidLength);
			}
		}
		Array.Copy(bytes, SaveHeaderLength, result, length, bytes.Length - SaveHeaderLength);
		return result;
	}

	/// <summary>
	/// Asserts the fixture is spec-shaped for the version it declares: it stores that
	/// version pair at the literal offsets, and its first body chunk - the 'BIC ' wrapper
	/// that carries the embedded BIQ - starts exactly where the header table says the body
	/// does. A file that declares an older version but still carries the modern header, or
	/// a body at the modern offset, fails here.
	/// </summary>
	private static void AssertSpecShapedForVersion(byte[] bytes, int major, int minor) {
		Assert.Equal(major, BitConverter.ToInt32(bytes, LiteralMajorVersionOffset));
		if (major >= LiteralMinorFirstStoredMajor) {
			Assert.Equal(minor, BitConverter.ToInt32(bytes, LiteralMinorVersionOffset));
		}
		Assert.Equal("BIC ", Encoding.ASCII.GetString(bytes, HeaderLength(major, minor), 4));
	}

	/// <summary>
	/// The offset of the per-player `PALV` array, found by its signature: the four
	/// ASCII characters followed by the length of one record (148). The block the
	/// loader advances over sits immediately before it.
	/// </summary>
	private static int PerPlayerArrayOffset(byte[] bytes) {
		for (int i = 0; i + 8 <= bytes.Length; i++) {
			if (IsTag(bytes, i, "PALV") && BitConverter.ToInt32(bytes, i + 4) == 148) {
				return i;
			}
		}
		throw new InvalidOperationException("the save has no per-player array to find");
	}

	private static int PerPlayerBlockStart(byte[] bytes) => PerPlayerArrayOffset(bytes) - SaveFormatGate.WorldTileBlockLength;

	private static bool IsTag(byte[] bytes, int offset, string tag) {
		if (offset < 0 || offset + 4 > bytes.Length) {
			return false;
		}
		for (int i = 0; i < 4; i++) {
			if (bytes[offset + i] != (byte)tag[i]) {
				return false;
			}
		}
		return true;
	}

	/// <summary>
	/// The offsets of the city date sub-records in a save: a `DATE` chunk of 84
	/// payload bytes directly after the 48-byte `BITM` chunk that ends the fixed part
	/// of a city record.
	/// </summary>
	private static List<int> CityDateSubRecordOffsets(byte[] bytes) {
		List<int> offsets = new();
		for (int i = 0; i + 48 + 8 <= bytes.Length; i++) {
			if (IsTag(bytes, i, "BITM") && BitConverter.ToInt32(bytes, i + 4) == 40
				&& IsTag(bytes, i + 48, "DATE") && BitConverter.ToInt32(bytes, i + 52) == 84) {
				offsets.Add(i + 48);
			}
		}
		return offsets;
	}

	/// <summary>
	/// The byte ranges a save older than format 20 does not have: for every city, the
	/// version-gated tail that starts after its date sub-record. The tail ends where
	/// the next city record starts, or where the block before the per-player array
	/// starts for the last one.
	/// </summary>
	private static List<(int Start, int Length)> CityTailRanges(byte[] bytes) {
		int blockStart = PerPlayerBlockStart(bytes);
		List<(int Start, int Length)> ranges = new();
		foreach (int date in CityDateSubRecordOffsets(bytes)) {
			int start = date + DateSubRecordLength;
			int end = NextCityRecord(bytes, start, blockStart);
			ranges.Add((start, end - start));
		}
		return ranges;
	}

	private static int NextCityRecord(byte[] bytes, int from, int limit) {
		for (int i = from; i + 8 <= limit; i++) {
			if (IsTag(bytes, i, "CITY") && BitConverter.ToInt32(bytes, i + 4) == 136) {
				return i;
			}
		}
		return limit;
	}

	private static byte[] InsertBytes(byte[] bytes, int offset, int count) {
		byte[] result = new byte[bytes.Length + count];
		Array.Copy(bytes, 0, result, 0, offset);
		Array.Copy(bytes, offset, result, offset + count, bytes.Length - offset);
		return result;
	}

	private static byte[] RemoveRanges(byte[] bytes, List<(int Start, int Length)> ranges) {
		int removed = ranges.Sum(range => range.Length);
		byte[] result = new byte[bytes.Length - removed];
		int read = 0;
		int write = 0;
		foreach ((int start, int length) in ranges.OrderBy(range => range.Start)) {
			Array.Copy(bytes, read, result, write, start - read);
			write += start - read;
			read = start + length;
		}
		Array.Copy(bytes, read, result, write, bytes.Length - read);
		return result;
	}

	/// <summary>
	/// Every value the parse produces that a version rule can change, so a doctored
	/// file has to agree with the untouched one field by field. The rules line comes
	/// from the embedded BIQ, so it also pins where the body - and with it the BIQ -
	/// was read from.
	/// </summary>
	private static List<string> Snapshot(SavData sav) {
		List<string> lines = new() {
			$"world {sav.Wrld.Width}x{sav.Wrld.Height} seed={sav.Wrld.WorldSeed} continents={sav.Wrld.ContinentCount}",
			$"tiles={sav.Tile.Length} units={sav.Unit.Length} cities={sav.City.Length} colonies={sav.Clny?.Length ?? 0}",
			$"players={sav.Palv.Length} history={sav.Hist.TurnCount} turn={sav.Game.TurnNumber}",
			$"rules tech={sav.Bic.Tech.Length} bldg={sav.Bic.Bldg.Length} good={sav.Bic.Good.Length}",
		};
		for (int i = 0; i < sav.City.Length; i++) {
			ref CITY city = ref sav.City[i];
			lines.Add($"city {city.ID} '{city.Name}' owner={city.Owner} at={city.X},{city.Y} pop={city.Popd.CitizenCount} "
				+ $"bldg={city.Binf.BuildingCount} culture={city.CulturePerTurn} food={city.TotalFood} shields={city.ShieldsCollected} "
				+ $"maintenance={city.MaintenanceGPT} format20={city.Format20Value} rev={city.Revision}");
		}
		return lines;
	}

	private static string StripVersionFields(string cityLine) {
		int index = cityLine.IndexOf(" format20=", StringComparison.Ordinal);
		return index < 0 ? cityLine : cityLine.Substring(0, index);
	}
}
