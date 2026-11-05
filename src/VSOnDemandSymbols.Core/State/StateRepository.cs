using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using VSOnDemandSymbols.Core.Abstractions;
using VSOnDemandSymbols.Core.Modules;
using VSOnDemandSymbols.Core.Mru;
using VSOnDemandSymbols.Core.Policy;

namespace VSOnDemandSymbols.Core.State;

public sealed class StateRepository
{
    private readonly IFileSystem _fileSystem;
    private readonly ILogger _logger;

    public const int CurrentSchemaVersion = 1;

    public StateRepository(IFileSystem fileSystem, ILogger logger)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public SolutionState Load(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !_fileSystem.FileExists(filePath))
        {
            return new SolutionState();
        }

        try
        {
            string json = _fileSystem.ReadAllText(filePath);
            var state = JsonConvert.DeserializeObject<SolutionState>(json);
            if (state == null || state.SchemaVersion <= 0)
            {
                throw new InvalidDataException("State deserialized to null or invalid schema version.");
            }

            if (state.SchemaVersion > CurrentSchemaVersion)
            {
                _logger.Warning($"State file '{filePath}' has newer schema version {state.SchemaVersion} than supported {CurrentSchemaVersion}.");
            }

            return state;
        }
        catch (Exception ex)
        {
            _logger.Warning($"Corrupt state file '{filePath}' was detected: {ex.Message}. Resetting to fresh state.");
            RecoverCorruptFile(filePath);
            return new SolutionState();
        }
    }

    public void Save(string filePath, SolutionState state)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));
        }

        if (state == null)
        {
            throw new ArgumentNullException(nameof(state));
        }

        string dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !_fileSystem.DirectoryExists(dir))
        {
            _fileSystem.CreateDirectory(dir);
        }

        string tempPath = Path.Combine(dir ?? "", $"{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
        string json = JsonConvert.SerializeObject(state, Formatting.Indented);

        try
        {
            _fileSystem.WriteAllText(tempPath, json);
            _fileSystem.ReplaceFile(tempPath, filePath, null);
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to write state file '{filePath}': {ex.Message}", ex);
            try
            {
                if (_fileSystem.FileExists(tempPath))
                {
                    _fileSystem.DeleteFile(tempPath);
                }
            }
            catch
            {
                // Ignore cleanup error
            }
            throw;
        }
    }

    private void RecoverCorruptFile(string filePath)
    {
        try
        {
            string badPath = filePath + ".bad";
            if (_fileSystem.FileExists(badPath))
            {
                _fileSystem.DeleteFile(badPath);
            }
            _fileSystem.MoveFile(filePath, badPath);
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to backup corrupt state file '{filePath}' to .bad: {ex.Message}", ex);
        }
    }

    public static SolutionState CreateFromMru(MruStore store, int sessionCount = 0, bool? forceUnrealMode = null)
    {
        var state = new SolutionState
        {
            SchemaVersion = CurrentSchemaVersion,
            SessionCount = sessionCount,
            ForceUnrealMode = forceUnrealMode
        };

        foreach (var entry in store.GetEntries())
        {
            state.Modules[entry.Key.Name] = new ModuleStateDto
            {
                Kind = entry.Key.Kind.ToString(),
                Binaries = new List<string>(entry.Binaries),
                Pinned = entry.Pinned,
                LastTriggerUtc = entry.LastTriggerUtc,
                RunsSinceTrigger = entry.RunsSinceTrigger,
                LastReason = entry.LastReason.ToString(),
                LastReasonDetail = entry.LastReasonDetail,
                TriggerCount = entry.TriggerCount
            };
        }

        return state;
    }

    public static void PopulateMruStore(SolutionState state, MruStore store)
    {
        var entries = new List<MruEntry>();
        foreach (var kvp in state.Modules)
        {
            string name = kvp.Key;
            var dto = kvp.Value;

            var kind = ModuleKind.Unreal;
            if (Enum.TryParse<ModuleKind>(dto.Kind, true, out var parsedKind))
            {
                kind = parsedKind;
            }

            var reason = TriggerReason.Breakpoint;
            if (Enum.TryParse<TriggerReason>(dto.LastReason, true, out var parsedReason))
            {
                reason = parsedReason;
            }

            var entry = new MruEntry(new ModuleKey(kind, name))
            {
                Binaries = new List<string>(dto.Binaries),
                Pinned = dto.Pinned,
                LastTriggerUtc = dto.LastTriggerUtc,
                RunsSinceTrigger = dto.RunsSinceTrigger,
                LastReason = reason,
                LastReasonDetail = dto.LastReasonDetail,
                TriggerCount = dto.TriggerCount
            };

            entries.Add(entry);
        }

        store.LoadEntries(entries);
    }
}
