using System;
using System.Globalization;
using UnityEngine;

namespace Drawmaha.Network
{
    // Test profiles isolate saved identity only; server authorization and game rules stay the same.
    public static class LocalTestPlayer
    {
        public const string EditorPreferenceKey = "Drawmaha.LocalTestPlayer";
        public static int Number { get; private set; }
        public static string ServerOverride { get; private set; }
        public static string Label { get { return Number == 0 ? "Normal player" : "Test player " + Number; } }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Initialize()
        {
            int editorDefault = 0;
#if UNITY_EDITOR
            editorDefault = UnityEditor.EditorPrefs.GetInt(EditorPreferenceKey, 0);
#endif
            string[] arguments = Environment.GetCommandLineArgs();
            Number = ReadNumber(arguments, editorDefault);
            ServerOverride = ReadOption(arguments, "-drawmaha-server");
        }
        public static int ReadNumber(string[] arguments, int defaultNumber)
        {
            string option = ReadOption(arguments, "-drawmaha-test-player");
            int number = defaultNumber;
            if (option != null && !int.TryParse(option, NumberStyles.None, CultureInfo.InvariantCulture, out number))
                throw new ArgumentException("Use -drawmaha-test-player with a number from 0 to 6.");
            if (number < 0 || number > 6) throw new ArgumentOutOfRangeException("defaultNumber", "Test player must be 0 (normal) or 1–6.");
            return number;
        }
        static string ReadOption(string[] arguments, string name)
        {
            for (int i = 0; i < arguments.Length; i++)
                if (string.Equals(arguments[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= arguments.Length || arguments[i + 1].StartsWith("-", StringComparison.Ordinal))
                        throw new ArgumentException("Missing value for " + name + ".");
                    return arguments[i + 1];
                }
            return null;
        }
        public static string KeyFor(string normalKey, int playerNumber)
        {
            if (playerNumber < 0 || playerNumber > 6) throw new ArgumentOutOfRangeException("playerNumber");
            return playerNumber == 0 ? normalKey : normalKey + ".test-player." + playerNumber.ToString(CultureInfo.InvariantCulture);
        }
        public static string Key(string normalKey) { return KeyFor(normalKey, Number); }
    }
}
