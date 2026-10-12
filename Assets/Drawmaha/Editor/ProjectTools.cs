using System.IO;
using System.IO.Compression;
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
        [MenuItem("Tools/Drawmaha/Build macOS Client")]
        public static void BuildMacOS()
        {
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneOSX))
            {
                Debug.LogError("Install the macOS Build Support module for this Unity Editor in Unity Hub, then retry.");
                return;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EnsureScene(); Directory.CreateDirectory("Builds/macOS");
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                scenes = new[] { ScenePath }, locationPathName = "Builds/macOS/Drawmaha.app",
                target = BuildTarget.StandaloneOSX, options = BuildOptions.None
            });
            Debug.Log("Drawmaha macOS build: " + report.summary.result + " · " + report.summary.totalErrors + " errors");
            if (report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                CreateMacArchive("Builds/macOS/Drawmaha.app");
                Debug.Log("Drawmaha macOS release ZIP: Builds/Drawmaha-macOS.zip");
            }
        }
        static void CreateMacArchive(string appPath)
        {
            string archivePath = "Builds/Drawmaha-macOS.zip";
            if (File.Exists(archivePath)) File.Delete(archivePath);
            using (var stream = new FileStream(archivePath, FileMode.Create))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                string fullAppPath = Path.GetFullPath(appPath);
                foreach (string file in Directory.GetFiles(fullAppPath, "*", SearchOption.AllDirectories))
                {
                    string relativePath = file.Substring(fullAppPath.Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Replace('\\', '/');
                    string entryPath = "macOS/Drawmaha.app/" + relativePath;
                    var entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
                    bool executable = relativePath.StartsWith("Contents/MacOS/", System.StringComparison.Ordinal)
                        || relativePath.EndsWith(".dylib", System.StringComparison.OrdinalIgnoreCase);
                    entry.ExternalAttributes = executable
                        ? unchecked((int)0x81ED0000u)
                        : unchecked((int)0x81A40000u);
                    using (var input = File.OpenRead(file))
                    using (var output = entry.Open()) input.CopyTo(output);
                }
            }
            SetZipPlatformUnix(archivePath);
        }
        static void SetZipPlatformUnix(string archivePath)
        {
            byte[] bytes = File.ReadAllBytes(archivePath);
            int minimumOffset = System.Math.Max(0, bytes.Length - 65557);
            int endRecord = -1;
            for (int i = bytes.Length - 22; i >= minimumOffset; i--)
            {
                if (ReadUInt32(bytes, i) == 0x06054b50u) { endRecord = i; break; }
            }
            if (endRecord < 0) throw new InvalidDataException("ZIP end record was not found.");
            int entryCount = ReadUInt16(bytes, endRecord + 10);
            uint centralDirectoryOffset = ReadUInt32(bytes, endRecord + 16);
            if (centralDirectoryOffset == uint.MaxValue)
                throw new InvalidDataException("ZIP64 macOS archives are not supported.");
            int offset = (int)centralDirectoryOffset;
            for (int i = 0; i < entryCount; i++)
            {
                if (ReadUInt32(bytes, offset) != 0x02014b50u)
                    throw new InvalidDataException("ZIP central directory is invalid.");
                WriteUInt16(bytes, offset + 4, 0x0314);
                int nameLength = ReadUInt16(bytes, offset + 28);
                int extraLength = ReadUInt16(bytes, offset + 30);
                int commentLength = ReadUInt16(bytes, offset + 32);
                offset += 46 + nameLength + extraLength + commentLength;
            }
            File.WriteAllBytes(archivePath, bytes);
        }
        static int ReadUInt16(byte[] bytes, int offset)
        {
            return bytes[offset] | bytes[offset + 1] << 8;
        }
        static uint ReadUInt32(byte[] bytes, int offset)
        {
            return (uint)bytes[offset] | (uint)bytes[offset + 1] << 8
                | (uint)bytes[offset + 2] << 16 | (uint)bytes[offset + 3] << 24;
        }
        static void WriteUInt16(byte[] bytes, int offset, int value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
        }
    }
}
