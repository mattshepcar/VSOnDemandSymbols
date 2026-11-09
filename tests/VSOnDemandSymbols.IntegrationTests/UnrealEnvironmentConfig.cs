using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace VSOnDemandSymbols.IntegrationTests;

/// <summary>
/// Resolves and validates paths to a local Unreal Engine 5 + Lyra installation for profiling and integration tests.
/// Supports configuration via CLI (<c>--ue-root &lt;path&gt;</c>), environment variables (<c>VSONDEMANDSYMBOLS_UE_ROOT</c> or <c>UE_ROOT</c>),
/// local JSON settings (<c>testsettings.local.json</c> / <c>testsettings.json</c>), or default fallback (<c>D:\UnrealEngine</c>).
/// </summary>
public sealed class UnrealEnvironmentConfig
{
    public const string EnvVarName = "VSONDEMANDSYMBOLS_UE_ROOT";
    public const string FallbackEnvVarName = "UE_ROOT";
    public const string DefaultUnrealRoot = @"D:\UnrealEngine";

    public string UnrealEngineRoot { get; }
    public string ConfigurationSource { get; }

    public string EngineBinariesWin64Dir => Path.Combine(UnrealEngineRoot, "Engine", "Binaries", "Win64");
    public string UnrealEditorCmdExePath => Path.Combine(EngineBinariesWin64Dir, "UnrealEditor-Cmd.exe");
    public string UnrealEditorDebugGameCmdExePath => Path.Combine(EngineBinariesWin64Dir, "UnrealEditor-Win64-DebugGame-Cmd.exe");

    public string LyraDirectory => Path.Combine(UnrealEngineRoot, "Samples", "Games", "Lyra");
    public string LyraBinariesWin64Dir => Path.Combine(LyraDirectory, "Binaries", "Win64");
    public string LyraUProjectPath => Path.Combine(LyraDirectory, "Lyra.uproject");
    public string LyraEditorEngineSourcePath => Path.Combine(LyraDirectory, "Source", "LyraEditor", "LyraEditorEngine.cpp");
    public string LyraGameplayTagsSourcePath => Path.Combine(LyraDirectory, "Source", "LyraGame", "LyraGameplayTags.cpp");

    public bool HasDebugGameTarget =>
        File.Exists(UnrealEditorDebugGameCmdExePath) &&
        File.Exists(Path.Combine(LyraBinariesWin64Dir, "UnrealEditor-LyraEditor-Win64-DebugGame.dll")) &&
        File.Exists(Path.Combine(LyraBinariesWin64Dir, "UnrealEditor-LyraGame-Win64-DebugGame.dll"));

    public string PreferredEditorCmdExePath =>
        HasDebugGameTarget ? UnrealEditorDebugGameCmdExePath : UnrealEditorCmdExePath;

    public string ExpectedLyraEditorDllName =>
        HasDebugGameTarget ? "UnrealEditor-LyraEditor-Win64-DebugGame.dll" : "UnrealEditor-LyraEditor.dll";

    public string ExpectedLyraGameDllName =>
        HasDebugGameTarget ? "UnrealEditor-LyraGame-Win64-DebugGame.dll" : "UnrealEditor-LyraGame.dll";

    public bool IsUnrealRootPresent => Directory.Exists(UnrealEngineRoot);
    public bool IsLyraAvailable =>
        IsUnrealRootPresent &&
        File.Exists(PreferredEditorCmdExePath) &&
        File.Exists(LyraUProjectPath) &&
        File.Exists(LyraEditorEngineSourcePath) &&
        File.Exists(LyraGameplayTagsSourcePath);

    public string? UnavailableReason
    {
        get
        {
            if (!Directory.Exists(UnrealEngineRoot))
            {
                return $"Unreal Engine root directory not found at '{UnrealEngineRoot}' (configured via {ConfigurationSource}). " +
                       $"Specify --ue-root <path> or set {EnvVarName}.";
            }

            if (!File.Exists(PreferredEditorCmdExePath))
            {
                return $"UnrealEditor command binary not found at '{PreferredEditorCmdExePath}'.";
            }

            if (!File.Exists(LyraUProjectPath))
            {
                return $"Lyra sample project not found at '{LyraUProjectPath}'.";
            }

            if (!File.Exists(LyraEditorEngineSourcePath))
            {
                return $"Lyra source file not found at '{LyraEditorEngineSourcePath}'.";
            }

            return null;
        }
    }

    public UnrealEnvironmentConfig(string unrealEngineRoot, string configurationSource)
    {
        UnrealEngineRoot = Path.GetFullPath(unrealEngineRoot);
        ConfigurationSource = configurationSource;
    }

    public static UnrealEnvironmentConfig Resolve(string? cliOverride, string repoRoot)
    {
        if (!string.IsNullOrWhiteSpace(cliOverride))
        {
            return new UnrealEnvironmentConfig(cliOverride!.Trim(), "CLI (--ue-root)");
        }

        string? envPrimary = Environment.GetEnvironmentVariable(EnvVarName);
        if (!string.IsNullOrWhiteSpace(envPrimary))
        {
            return new UnrealEnvironmentConfig(envPrimary!.Trim(), $"Environment ({EnvVarName})");
        }

        string? envFallback = Environment.GetEnvironmentVariable(FallbackEnvVarName);
        if (!string.IsNullOrWhiteSpace(envFallback))
        {
            return new UnrealEnvironmentConfig(envFallback!.Trim(), $"Environment ({FallbackEnvVarName})");
        }

        string integrationTestDir = Path.Combine(repoRoot, "tests", "VSOnDemandSymbols.IntegrationTests");
        foreach (string settingsFileName in new[] { "testsettings.local.json", "testsettings.json" })
        {
            string settingsPath = Path.Combine(integrationTestDir, settingsFileName);
            if (File.Exists(settingsPath))
            {
                try
                {
                    var json = JObject.Parse(File.ReadAllText(settingsPath));
                    string? configuredPath = json["UnrealEngineRoot"]?.Value<string>();
                    if (!string.IsNullOrWhiteSpace(configuredPath))
                    {
                        return new UnrealEnvironmentConfig(configuredPath!.Trim(), $"File ({settingsFileName})");
                    }
                }
                catch
                {
                    // Ignore malformed settings file and fall through
                }
            }
        }

        return new UnrealEnvironmentConfig(DefaultUnrealRoot, $"Default ({DefaultUnrealRoot})");
    }
}
