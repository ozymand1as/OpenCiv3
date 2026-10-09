namespace C7Engine {
	// The map generator's random number generator: the float draw at
	// `0x60ba80` and the ranged draw at `0x60bab0`.
	//
	// Each map-generation pass keeps its own state word and initialises it to
	// the map seed (the driver stores the seed at the world object's `+0x1ec`
	// at `0x5eb591`) plus a pass-specific constant. The start placer adds
	// `0x16062` and its SECOND argument to that seed (`0x5ef03d`, `0x5ef046`,
	// stored at `0x5ef051`) and draws from the resulting word. The second
	// argument is the literal 0 the call site supplies (`0x5eb7aa`); the gate
	// byte is the fourth argument and reaches only the ordering blocks, not the
	// stream. (The operand read at `0x5ef035` is the second argument: the
	// argument words of the allocation calls at `0x5ef018` and `0x5ef030` are
	// still on the stack when it is read, which shifts every later argument
	// slot by eight bytes. A record read it as the fourth argument and so
	// added the gate byte.) The earlier note that the state is one word shared
	// between passes, already advanced by the whole pipeline, was wrong too:
	// the passes each derive a fresh state, so the start-order stream is a pure
	// function of the seed. The port therefore seeds the same recurrence from
	// `mapSeed + 0x16062`. The sequence - thirty-two draws while the start
	// array is cleared, the candidate shuffle, the final reshuffle - is the
	// original's.
	//
	// One draw advances the state by multiplying it with `0x41c64e6d` and
	// adding `0x3039`, and the value handed out is bits 16..30 of the new state
	// divided by 32768 - the draw takes the state's high word, masks it with
	// `0x7fff` and multiplies by the double at `0x6716c8`, which is 2^-15. The
	// ranged draw masks its argument to sixteen bits, multiplies the draw by it
	// and truncates toward zero.
	internal sealed class Civ3StartRandom {
		private uint state;

		internal Civ3StartRandom(int seed) {
			state = unchecked((uint)seed);
		}

		// The float draw at `0x60ba80`.
		internal double NextFloat() {
			state = unchecked(state * 0x41c64e6d + 0x3039);
			return ((state >> 16) & 0x7fff) / 32768.0;
		}

		// The ranged draw at `0x60bab0`.
		internal int Next(int exclusiveUpperBound) {
			return (int)(NextFloat() * (exclusiveUpperBound & 0xffff));
		}
	}
}
