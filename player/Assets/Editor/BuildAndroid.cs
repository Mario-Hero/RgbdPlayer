using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class BuildAndroid
{
    /// <summary>
    /// 命令行构建入口: Unity.exe -batchmode -quit -projectPath ... -executeMethod BuildAndroid.Build
    /// 自动切 Android / ARM64 / Mono / 写包名 / 加场景 / 构建 APK
    /// XR loader 请在 Unity Editor 里手动勾选一次 (Edit→Project Settings→XR).
    /// </summary>
    public static void Build()
    {
        string outDir = Path.Combine(Directory.GetCurrentDirectory(), "Build");
        Directory.CreateDirectory(outDir);
        string apk = Path.Combine(outDir, "VR180Player.apk");

        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
        {
            Console.WriteLine("[Build] Switching to Android...");
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
        }

        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.Mono2x);
        PlayerSettings.applicationIdentifier = "com.vr180.player";
        PlayerSettings.bundleVersion = "1.0.0";
        PlayerSettings.Android.bundleVersionCode = 1;

        EditorBuildSettings.scenes = new[]
        {
            new EditorBuildSettingsScene("Assets/main.unity", enabled: true)
        };

        Console.WriteLine("[Build] Building APK → " + apk);
        var options = new BuildPlayerOptions
        {
            scenes = new[] { "Assets/main.unity" },
            locationPathName = apk,
            target = BuildTarget.Android,
            options = BuildOptions.None
        };
        BuildReport report = BuildPipeline.BuildPlayer(options);
        if (report.summary.result == BuildResult.Succeeded)
            Console.WriteLine("[Build] SUCCESS: " + apk + " (" + report.summary.totalSize + " bytes)");
        else
        {
            Console.WriteLine("[Build] FAILED: " + report.summary.result);
            EditorApplication.Exit(1);
        }
    }
}
