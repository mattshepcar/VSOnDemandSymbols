using System;

namespace VSOnDemandSymbols.Core.Modules;

public enum ModuleKind
{
    Unreal,
    MsBuild,
    External
}

public readonly struct ModuleKey : IEquatable<ModuleKey>
{
    public ModuleKind Kind { get; }
    public string Name { get; }

    public ModuleKey(ModuleKind kind, string name)
    {
        Kind = kind;
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }

    public bool Equals(ModuleKey other)
    {
        return Kind == other.Kind && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);
    }

    public override bool Equals(object? obj) => obj is ModuleKey other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = (int)Kind * 397;
            hash ^= StringComparer.OrdinalIgnoreCase.GetHashCode(Name);
            return hash;
        }
    }

    public override string ToString() => $"{Kind}:{Name}";

    public static bool operator ==(ModuleKey left, ModuleKey right) => left.Equals(right);
    public static bool operator !=(ModuleKey left, ModuleKey right) => !left.Equals(right);
}

