using System.IO;
using UnityEditor;
using UnityEngine;

// Сборка AssetBundle с шейдером тумана.
// Меню: Silent Fog -> Build Bundle
// Из командной строки: -executeMethod SilentFogBundleBuilder.BuildFromCommandLine
public static class SilentFogBundleBuilder
{
    private const string ShaderPath = "Assets/SilentFogFog.shader";
    private const string ParticleShaderPath = "Assets/SilentFogParticle.shader";
    private const string BundleName = "silentfogfog";
    private const string OutDir = "Build";

    [MenuItem("Silent Fog/Build Bundle")]
    public static void Build()
    {
        string outDir = Path.Combine(Directory.GetCurrentDirectory(), OutDir);
        if (!Directory.Exists(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        AssetDatabase.Refresh();

        var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
        if (shader == null)
        {
            Debug.LogError("[SilentFog] Не найден шейдер " + ShaderPath);
            if (Application.isBatchMode) EditorApplication.Exit(2);
            return;
        }

        var particleShader = AssetDatabase.LoadAssetAtPath<Shader>(ParticleShaderPath);
        if (particleShader == null)
        {
            Debug.LogError("[SilentFog] Не найден шейдер " + ParticleShaderPath);
            if (Application.isBatchMode) EditorApplication.Exit(2);
            return;
        }

        var build = new AssetBundleBuild
        {
            assetBundleName = BundleName,
            assetNames = new[] { ShaderPath, ParticleShaderPath }
        };

        BuildPipeline.BuildAssetBundles(
            outDir,
            new[] { build },
            BuildAssetBundleOptions.ChunkBasedCompression,
            BuildTarget.StandaloneWindows64);

        Debug.Log("[SilentFog] Bundle собран: " + Path.Combine(outDir, BundleName));
        if (Application.isBatchMode) EditorApplication.Exit(0);
    }

    public static void BuildFromCommandLine()
    {
        Build();
    }
}
