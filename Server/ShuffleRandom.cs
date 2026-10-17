using System.Security.Cryptography;
using Drawmaha;

namespace Drawmaha.Server;

public sealed class SecureShuffleRandom : IRandomSource
{
    public int NextBelow(int exclusiveUpperBound) => RandomNumberGenerator.GetInt32(exclusiveUpperBound);
}
