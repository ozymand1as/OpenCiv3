using System;
using System.IO;
using System.Text;
using C7GameData;
using QueryCiv3;
using Xunit;

namespace EngineTests;

// The save version gate: which save headers the engine accepts, and the version it
// then behaves by. The rules are the original save reader's: the file must begin with
// the save prologue, the major version must be at least 14, and a major version of 17
// or more stores a minor version whose value 1 is never valid. Below 17 no minor
// version is stored at all and the loader forces it to 0.
public class SaveFormatGateTests {
	private static readonly byte[] SavePrologue = { (byte)'C', (byte)'I', (byte)'V', (byte)'3', 0x00, 0x1A };

	// prologue + major version + minor version + the 16-byte GUID a modern save stores
	private const int SaveHeaderLength = 30;

	private static byte[] Header(byte[] prologue, int major, int storedMinor) {
		// Fill with a value that is neither a plausible version nor a plausible GUID
		// first, so a test can tell whether a field was read or ignored.
		byte[] bytes = new byte[SaveHeaderLength];
		for (int i = 0; i < bytes.Length; i++) {
			bytes[i] = 0xAB;
		}
		Array.Copy(prologue, bytes, prologue.Length);
		BitConverter.GetBytes(major).CopyTo(bytes, SaveFormatGate.MajorVersionOffset);
		BitConverter.GetBytes(storedMinor).CopyTo(bytes, SaveFormatGate.MinorVersionOffset);
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
}
