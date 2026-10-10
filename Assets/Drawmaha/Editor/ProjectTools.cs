using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Drawmaha.Editor
{
    public static class ProjectTools
    {
        const string ScenePath = "Assets/Scenes/Drawmaha.unity";
        [InitializeOnLoadMethod]
        static void InitializeThemes() { EditorApplication.delayCall += EnsureThemes; }
        static void EnsureThemes()
        {
            Directory.CreateDirectory("Assets/Resources/TableThemes");
            string[] names = { "Emerald", "Midnight", "Burgundy" };
            for (int i = 0; i < names.Length; i++)
            {
                string path = "Assets/Resources/TableThemes/" + names[i] + ".asset";
                if (File.Exists(path)) continue;
                var theme = ScriptableObject.CreateInstance<TableTheme>(); theme.DisplayName = names[i]; theme.CardDealSeconds = 0.14f;
                if (i == 1) { theme.Felt = new Color(0.075f, 0.16f, 0.28f); theme.Background = new Color(0.025f, 0.03f, 0.07f); theme.CardBack = new Color(0.15f, 0.14f, 0.31f); }
                if (i == 2) { theme.Felt = new Color(0.28f, 0.075f, 0.12f); theme.Background = new Color(0.07f, 0.025f, 0.035f); theme.CardBack = new Color(0.28f, 0.09f, 0.12f); }
                AssetDatabase.CreateAsset(theme, path);
            }
            AssetDatabase.SaveAssets();
        }
        [MenuItem("Tools/Drawmaha/Open Table")]
        public static void OpenTable()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EnsureScene(); EditorSceneManager.OpenScene(ScenePath);
        }
        [MenuItem("Tools/Drawmaha/Preview Table UI")]
        public static void PreviewTableUI()
        {
            if (EditorApplication.isPlaying || !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EnsureScene(); EditorSceneManager.OpenScene(ScenePath);
            EditorPrefs.SetBool("Drawmaha.VisualPreview", true);
            EditorApplication.isPlaying = true;
        }
        public static void EnsureScene()
        {
            if (!File.Exists(ScenePath))
            {
                Directory.CreateDirectory("Assets/Scenes");
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                EditorSceneManager.SaveScene(scene, ScenePath);
            }
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        }
        [MenuItem("Tools/Drawmaha/Run Logic Checks")]
        public static void RunChecks() { Debug.Log(LogicChecks.Run()); }
        [MenuItem("Tools/Drawmaha/Build Windows Client")]
        public static void BuildWindows()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EnsureScene(); Directory.CreateDirectory("Builds/Windows");
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                scenes = new[] { ScenePath }, locationPathName = "Builds/Windows/Drawmaha.exe",
                target = BuildTarget.StandaloneWindows64, options = BuildOptions.None
            });
            Debug.Log("Drawmaha build: " + report.summary.result + " · " + report.summary.totalErrors + " errors");
        }
    }
}
