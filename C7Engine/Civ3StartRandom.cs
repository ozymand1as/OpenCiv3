namespace C7Engine {
	// The map generator's random number generator, read out of `next_float` @
	// `0x60ba80` and `rand_int` @ `0x60bab0`.
	//
	// The state is one 32-bit word that lives inside the world object (at
	// `world+0x16062`, which `FUN_005eeee0` reaches through `0x5ef046`), and
	// every generation pass draws from it. One draw is
	// `state = state * 0x41c64e6d + 0x3039`, and the value handed out is the
	// state's bits 16..30 divided by 32768 - the function reads the state's
	// high word, masks it with `0x7fff` and multiplies by the double at
	// `0x6716c8`, which is 2^-15. `rand_int(n)` masks its argument to sixteen
	// bits, multiplies the draw by it and truncates toward zero.
	//
	// Reproducing the recurrence makes the port's draws identical to the
	// original's *for the same state*. The state is not the same: the original
	// has been drawing from it since world creation, and every earlier
	// generation pass has advanced it, so re-deriving it would mean re-running
	// the whole generator's draw history. The port seeds the same recurrence
	// from the map seed with an offset of its own, which keeps the sequence -
	// thirty-two draws while the start array is cleared, then the candidate
	// shuffle, then the final reshuffle - intact.
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
