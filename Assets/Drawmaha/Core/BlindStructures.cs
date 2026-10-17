namespace Drawmaha
{
    public static class BlindStructures
    {
        public static readonly (float SmallBlind, float BigBlind)[] Options =
        {
            (0.25f, 0.50f),
            (0.50f, 1.00f),
            (1.00f, 2.00f),
            (2.00f, 5.00f),
            (5.00f, 10.00f),
            (10.00f, 20.00f),
            (20.00f, 50.00f),
            (50.00f, 100.00f),
            (100.00f, 200.00f)
        };
    }
}