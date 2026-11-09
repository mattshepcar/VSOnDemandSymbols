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

    public string UnrealEditorExePath => Path.Combine(UnrealEngineRoot, "Engine", "Binaries", "Win64", "UnrealEditor.exe");
    public string UnrealEditorCmdExePath => Path.Combine(UnrealEngineRoot, "Engine", "Binaries", "Win64", "UnrealEditor-Cmd.exe");
    public string LyraDirectory => Path.Combine(UnrealEngineRoot, "Samples", "Games", "Lyra");
    public string LyraUProjectPath => Path.Combine(LyraDirectory, "Lyra.uproject");
    public string LyraGameModuleSourcePath => Path.Combine(LyraDirectory, "Source", "LyraGame", "LyraGameModule.cpp");
    public string LyraGameplayAbilitySourcePath => Path.Combine(LyraDirectory, "Source", "LyraGame", "AbilitySystem", "Abilities", "LyraGameplayAbility.cpp");

    public bool IsUnrealRootPresent => Directory.Exists(UnrealEngineRoot);
    public bool IsLyraAvailable =>
        IsUnrealRootPresent &&
        File.Exists(UnrealEditorCmdExePath) &&
        File.Exists(LyraUProjectPath) &&
        File.Exists(LyraGameModuleSourcePath);

    public string? UnavailableReason
    {
        get
        {
            if (!Directory.Exists(UnrealEngineRoot))
            {
                return $"Unreal Engine root directory not found at '{UnrealEngineRoot}' (configured via {ConfigurationSource}). " +
                       $"Specify --ue-root <path> or set {EnvVarName}.";
            }

            if (!File.Exists(UnrealEditorCmdExePath))
            {
                return $"UnrealEditor-Cmd.exe not found at '{UnrealEditorCmdExePath}'.";
            }

            if (!File.Exists(LyraUProjectPath))
            {
                return $"Lyra sample project not found at '{LyraUProjectPath}'.";
            }

            if (!File.Exists(LyraGameModuleSourcePath))
            {
                return $"Lyra source file not found at '{LyraGameModuleSourcePath}'.";
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

