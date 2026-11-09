using System;
using System.IO;
using System.Security;

namespace VSOnDemandSymbols.IntegrationTests;

/// <summary>
/// Generates a self-contained, zero-write Visual Studio harness solution in <c>%TEMP%</c>
/// that launches the configured <c>UnrealEditor*-Cmd.exe</c> against <c>Lyra.uproject</c>
/// in headless mode (<c>-nullrhi -unattended</c>) with redirected <c>-UserDir</c> and <c>-abslog</c>
/// so zero files are created or modified inside the Unreal Engine root directory.
/// </summary>
public static class LyraHarnessGenerator
{
    public const string ProjectGuid = "{F0000000-0000-0000-0000-000000000001}";

    public static string Generate(UnrealEnvironmentConfig config)
    {
        if (config == null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        string harnessDir = Path.Combine(Path.GetTempPath(), "VSOnDemandSymbols_LyraHarness");
        string userDir = Path.Combine(harnessDir, "UserDir");
        string logsDir = Path.Combine(harnessDir, "Logs");
        Directory.CreateDirectory(harnessDir);
        Directory.CreateDirectory(userDir);
        Directory.CreateDirectory(logsDir);

        string uprojectMarkerPath = Path.Combine(harnessDir, "LyraHarness.uproject");
        string slnPath = Path.Combine(harnessDir, "LyraHarness.sln");
        string vcxprojPath = Path.Combine(harnessDir, "LyraHarness.vcxproj");
        string vcxprojUserPath = Path.Combine(harnessDir, "LyraHarness.vcxproj.user");
        string logFilePath = Path.Combine(logsDir, "LyraProfile.log");

        string editorExe = config.PreferredEditorCmdExePath;
        string workingDir = config.EngineBinariesWin64Dir;
        string arguments =
            $"\"{config.LyraUProjectPath}\" -nullrhi -unattended -nosound -nosplash -nopause " +
            $"-NoShaderCompile -UserDir=\"{userDir}\" -abslog=\"{logFilePath}\"";

        string escapedExe = SecurityElement.Escape(editorExe);
        string escapedArgs = SecurityElement.Escape(arguments);
        string escapedWorkDir = SecurityElement.Escape(workingDir);

        File.WriteAllText(
            uprojectMarkerPath,
            """
            {
              "FileVersion": 3,
              "EngineAssociation": "5.5",
              "Category": "",
              "Description": "Zero-write profiling harness for VSOnDemandSymbols against Lyra."
            }
            """);

        File.WriteAllText(
            slnPath,
            $$"""
            Microsoft Visual Studio Solution File, Format Version 12.00
            # Visual Studio Version 17
            VisualStudioVersion = 17.14.37411.7
            MinimumVisualStudioVersion = 10.0.40219.1
            Project("{8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942}") = "LyraHarness", "LyraHarness.vcxproj", "{{ProjectGuid}}"
            EndProject
            Global
            	GlobalSection(SolutionConfigurationPlatforms) = preSolution
            		Debug|x64 = Debug|x64
            	EndGlobalSection
            	GlobalSection(ProjectConfigurationPlatforms) = postSolution
            		{{ProjectGuid}}.Debug|x64.ActiveCfg = Debug|x64
            	EndGlobalSection
            	GlobalSection(SolutionProperties) = preSolution
            		HideSolutionNode = FALSE
            	EndGlobalSection
            EndGlobal
            """);

        File.WriteAllText(
            vcxprojPath,
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Project DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <ItemGroup Label="ProjectConfigurations">
                <ProjectConfiguration Include="Debug|x64">
                  <Configuration>Debug</Configuration>
                  <Platform>x64</Platform>
                </ProjectConfiguration>
              </ItemGroup>
              <PropertyGroup Label="Globals">
                <VCProjectVersion>17.0</VCProjectVersion>
                <Keyword>MakeFileProj</Keyword>
                <ProjectGuid>{ProjectGuid}</ProjectGuid>
                <RootNamespace>LyraHarness</RootNamespace>
                <WindowsTargetPlatformVersion>10.0</WindowsTargetPlatformVersion>
              </PropertyGroup>
              <Import Project="$(VCTargetsPath)\Microsoft.Cpp.Default.props" />
              <PropertyGroup Condition="'$(Configuration)|$(Platform)'=='Debug|x64'" Label="Configuration">
                <ConfigurationType>Makefile</ConfigurationType>
                <UseDebugLibraries>true</UseDebugLibraries>
                <PlatformToolset Condition="'$(VisualStudioVersion)' == '18.0'">v145</PlatformToolset>
                <PlatformToolset Condition="'$(VisualStudioVersion)' != '18.0'">v143</PlatformToolset>
              </PropertyGroup>
              <Import Project="$(VCTargetsPath)\Microsoft.Cpp.props" />
              <PropertyGroup Condition="'$(Configuration)|$(Platform)'=='Debug|x64'">
                <NMakeOutput>{escapedExe}</NMakeOutput>
                <LocalDebuggerCommand>{escapedExe}</LocalDebuggerCommand>
                <LocalDebuggerCommandArguments>{escapedArgs}</LocalDebuggerCommandArguments>
                <LocalDebuggerWorkingDirectory>{escapedWorkDir}</LocalDebuggerWorkingDirectory>
                <DebuggerFlavor>WindowsLocalDebugger</DebuggerFlavor>
              </PropertyGroup>
              <Import Project="$(VCTargetsPath)\Microsoft.Cpp.targets" />
            </Project>
            """);

        File.WriteAllText(
            vcxprojUserPath,
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="Current" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup Condition="'$(Configuration)|$(Platform)'=='Debug|x64'">
                <LocalDebuggerCommand>{escapedExe}</LocalDebuggerCommand>
                <LocalDebuggerCommandArguments>{escapedArgs}</LocalDebuggerCommandArguments>
                <LocalDebuggerWorkingDirectory>{escapedWorkDir}</LocalDebuggerWorkingDirectory>
                <DebuggerFlavor>WindowsLocalDebugger</DebuggerFlavor>
              </PropertyGroup>
            </Project>
            """);

        return slnPath;
    }
}

