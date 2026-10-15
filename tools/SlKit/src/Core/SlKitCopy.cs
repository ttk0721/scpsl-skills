namespace SlKit.Core;

/// <summary>One other assembly in the process that carries SlKit's types.</summary>
public struct SlKitCopy
{
    /// <summary>Creates an entry.</summary>
    /// <param name="assemblyName">Simple assembly name.</param>
    /// <param name="version">Assembly version as text.</param>
    /// <param name="location">File path, or empty when the assembly was loaded from bytes.</param>
    /// <param name="isSharedLibrary">True when the assembly is a separate <c>SlKit.dll</c>, false when SlKit was compiled into another assembly.</param>
    public SlKitCopy(string assemblyName, string version, string location, bool isSharedLibrary)
    {
        AssemblyName = assemblyName;
        Version = version;
        Location = location;
        IsSharedLibrary = isSharedLibrary;
    }

    /// <summary>Simple assembly name.</summary>
    public string AssemblyName { get; }

    /// <summary>Assembly version as text.</summary>
    public string Version { get; }

    /// <summary>File path, or empty when the assembly was loaded from bytes (LabAPI loads every DLL that way).</summary>
    public string Location { get; }

    /// <summary>True for a separate <c>SlKit.dll</c>; false when SlKit was source-included into another assembly.</summary>
    public bool IsSharedLibrary { get; }
}
