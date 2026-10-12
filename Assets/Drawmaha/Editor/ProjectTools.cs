using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Drawmaha.Editor
{
    public static class ProjectTools
    {
        const string ScenePath = "Assets/Scenes/Drawmaha.unity";
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
