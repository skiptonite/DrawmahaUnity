// Test-only Unity surface for running the real network/profile code without modifying real PlayerPrefs.
namespace UnityEngine
{
    public class MonoBehaviour { }
    public enum RuntimeInitializeLoadType { BeforeSceneLoad }
    public sealed class RuntimeInitializeOnLoadMethodAttribute : Attribute
    {
        public RuntimeInitializeLoadType LoadType { get; }
        public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType when) { LoadType = when; }
    }
    public static class Time { public static float unscaledTime; }
    public static class Mathf { public static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max); }
    public static class PlayerPrefs
    {
        static readonly Dictionary<string, string> strings = new();
        static readonly Dictionary<string, int> numbers = new();
        public static string GetString(string key, string fallback = "") => strings.GetValueOrDefault(key, fallback);
        public static int GetInt(string key, int fallback = 0) => numbers.GetValueOrDefault(key, fallback);
        public static bool HasKey(string key) => strings.ContainsKey(key) || numbers.ContainsKey(key);
        public static void SetString(string key, string value) => strings[key] = value;
        public static void SetInt(string key, int value) => numbers[key] = value;
        public static void DeleteKey(string key) { strings.Remove(key); numbers.Remove(key); }
        public static void Save() { }
    }
    public static class JsonUtility
    {
        static readonly System.Text.Json.JsonSerializerOptions Json = new() { IncludeFields = true };
        public static string ToJson<T>(T value) => System.Text.Json.JsonSerializer.Serialize(value, Json);
        public static T FromJson<T>(string value) => System.Text.Json.JsonSerializer.Deserialize<T>(value, Json);
    }
}
