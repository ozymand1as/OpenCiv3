namespace C7Engine {
	// The map generator's random number generator, read out of `next_float` @
	// `0x60ba80` and `rand_int` @ `0x60bab0`.
	//
	// Each map-generation pass keeps its own state word and initialises it to
	// the map seed (the driver stores the seed at the world object's `+0x1ec`
	// at `0x5eb591`) plus a pass-specific constant. `FUN_005eeee0` adds
	// `0x16062` and its fourth argument to that seed (`0x5ef046`, where the
	// `lea` computes the initial state value) and draws from the resulting
	// word. The earlier note that the state is one word shared between passes,
	// already advanced by the whole pipeline, was wrong: the passes each derive
	// a fresh state, so the start-order stream is a pure function of the seed.
	// The port therefore seeds the same recurrence from
	// `mapSeed + 0x16062 + 1` (the fourth argument is 1 on the single-player
	// path). The sequence - thirty-two draws while the start array is cleared,
	// the candidate shuffle, the final reshuffle - is the original's.
	//
	// One draw is `state = state * 0x41c64e6d + 0x3039`, and the value handed
	// out is the state's bits 16..30 divided by 32768 - the function reads the
	// state's high word, masks it with `0x7fff` and multiplies by the double at
	// `0x6716c8`, which is 2^-15. `rand_int(n)` masks its argument to sixteen
	// bits, multiplies the draw by it and truncates toward zero.
	internal sealed class Civ3StartRandom {
		private uint state;

		internal Civ3StartRandom(int seed) {
			state = unchecked((uint)seed);
		}

		// `next_float` @ `0x60ba80`.
		internal double NextFloat() {
			state = unchecked(state * 0x41c64e6d + 0x3039);
			return ((state >> 16) & 0x7fff) / 32768.0;
		}

		// `rand_int` @ `0x60bab0`.
		internal int Next(int exclusiveUpperBound) {
			return (int)(NextFloat() * (exclusiveUpperBound & 0xffff));
		}
	}
}
