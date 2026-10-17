using System;

namespace Drawmaha
{
    public interface IRandomSource
    {
        int NextBelow(int exclusiveUpperBound);
    }

    // Explicitly reproducible, non-secure randomness for tests only.
    public sealed class DeterministicTestRandom : IRandomSource
    {
        readonly Random random;
        public DeterministicTestRandom(int seed) { random = new Random(seed); }
        public int NextBelow(int exclusiveUpperBound) { return random.Next(exclusiveUpperBound); }
    }
}
