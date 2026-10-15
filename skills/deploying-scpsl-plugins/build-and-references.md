# Build and references

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

## The CS1061 trap

The server runs Unity's Mono with a corefx-derived `mscorlib` (4.6.x). That runtime contains many members that .NET Framework 4.8 does not. A project that targets `net48` and takes the SDK's default Framework reference pack compiles against Framework 4.8 metadata, so these members fail at compile time with CS1061 although they exist on the server.

The common wrong conclusion is "the server lacks the method, rewrite the code". It does not lack it. Change the references.

Reference these four from the server's `Managed/` folder, with implicit framework references disabled:

`mscorlib.dll`, `System.dll`, `System.Core.dll`, `netstandard.dll`

LabAPI itself builds this way. SlKit's `Directory.Build.props` (tools/SlKit) is a working example. Besides `DisableImplicitFrameworkReferences`, it sets `AutomaticallyUseReferenceAssemblyPackages=false` and `FrameworkPathOverride` to the Managed folder, because otherwise the SDK silently resolves `mscorlib` from the desktop pack anyway.

## csproj template

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net48</TargetFramework>
    <AssemblyName>MyPlugin</AssemblyName>
    <LangVersion>latest</LangVersion>
    <DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences>
    <AutomaticallyUseReferenceAssemblyPackages>false</AutomaticallyUseReferenceAssemblyPackages>
    <FrameworkPathOverride>$(SL_REFERENCES)</FrameworkPathOverride>
    <AddAdditionalExplicitAssemblyReferences>false</AddAdditionalExplicitAssemblyReferences>
    <GenerateDependencyFile>false</GenerateDependencyFile>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="mscorlib"><HintPath>$(SL_REFERENCES)/mscorlib.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="System"><HintPath>$(SL_REFERENCES)/System.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="System.Core"><HintPath>$(SL_REFERENCES)/System.Core.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="netstandard"><HintPath>$(SL_REFERENCES)/netstandard.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="LabApi"><HintPath>$(SL_REFERENCES)/LabApi.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="Assembly-CSharp"><HintPath>$(SL_REFERENCES)/Assembly-CSharp.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="Assembly-CSharp-firstpass"><HintPath>$(SL_REFERENCES)/Assembly-CSharp-firstpass.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="UnityEngine.CoreModule"><HintPath>$(SL_REFERENCES)/UnityEngine.CoreModule.dll</HintPath><Private>false</Private></Reference>
  </ItemGroup>
</Project>
```

Build with `dotnet build -c Release -p:SL_REFERENCES="/path/to/SCPSL_Data/Managed"`. Never commit the game DLLs to a public repository.

## What to reference, and what not to ship

| Need | Reference | Ship it? |
|---|---|---|
| LabAPI types | `LabApi.dll` | No. `Private=false` |
| Game types, `Player` internals, `ReferenceHub` | `Assembly-CSharp.dll` | No |
| MEC (`Timing`, `CoroutineHandle`) | `Assembly-CSharp-firstpass.dll` (there is no `MEC.dll`) | No |
| Unity, Mirror | `UnityEngine.*Module.dll`, `Mirror.dll` | No |
| Harmony | `0Harmony.dll` from `LabAPI/dependencies/<port>` (LabAPI does not bundle it; it is 2.2.2.0 there, and EXILED loads a byte-identical copy) | No: reference it, do not put a copy in your plugin package; the operator keeps one copy in `dependencies/` |
| YamlDotNet | game's copy in `Managed/` | No |
| Your own shared library (for example SlKit) | build output | Yes, to `dependencies/`, once |

A stale core DLL left in `plugins/<port>/` (for example an old `LabApi.dll`) loads silently as a "plugin" with no plugin type. It is dead weight and a possible binding hazard (unverified which copy plugin types bind to). Delete it.

## Runtime BCL surface (measured on the installed 14.2.7 server assemblies)

| API | At runtime |
|---|---|
| `KeyValuePair.Deconstruct`, tuple deconstruction, `ValueTuple` | present |
| `Dictionary.Remove(k, out v)`, `TryAdd`, `EnsureCapacity` | present |
| `ArraySegment<T>` indexer, `Slice` | present |
| `HashSet<T>.TryGetValue`, `RemoveWhere` | present |
| `string.Contains(char)`, `StartsWith(char)`, `Split(char, options)`, `Replace(.., StringComparison)` | present |
| `Span<T>`, `ReadOnlySpan<T>`, `Memory<T>`, `Index`, `Range`, `HashCode`, `MathF`, `ArrayPool<T>`, `Lazy<T>`, `ValueTask` | present |
| `Math.Clamp`, `Array.Fill`, `Enum.TryParse<T>`, `Stack/Queue.TryPop/TryDequeue` | present |
| `Enumerable.ToHashSet/Append/Prepend/TakeLast/SkipLast` | present |
| `Path.Join("a", "b")` with strings | compiles and runs against Managed (string converts to `ReadOnlySpan<char>`); fails against net48 reference assemblies |
| `HttpClient` | present (separate `System.Net.Http.dll`) |
| `System.Text.Json` | DLL present (9.0.0.0); that it runs on this Mono is UNVERIFIED |
| `Enumerable.MinBy/MaxBy/DistinctBy/ExceptBy/UnionBy/IntersectBy/Chunk/Order/OrderDescending/CountBy` | ABSENT |
| `Stopwatch.GetElapsedTime`, `Environment.ProcessId`, `Random.Shared` | ABSENT |
| `IsExternalInit`, `CallerArgumentExpressionAttribute`, `RequiredMemberAttribute`, `ModuleInitializerAttribute`, `DefaultInterpolatedStringHandler` | ABSENT as types |

Absent attribute types: `init`, `record`, `required` and `[CallerArgumentExpression]` compile only if your project declares the attribute type itself (a small internal polyfill). Nullable annotation attributes and `ConditionalWeakTable` are present. Replace absent LINQ with a loop or a two-line extension.

Compiling against the Managed set ties the build to that game version. A plugin built against 14.2.7 may throw `MissingMethodException` on an older server image (UNVERIFIED across versions).

## LabAPI from NuGet (Northwood.LabAPI)

Northwood publishes LabAPI as the NuGet package `Northwood.LabAPI`, and its wiki recommends it. For version 1.1.7:

| Question | Answer |
|---|---|
| What is inside | `lib/net48/LabApi.dll` and `lib/net48/LabApi.xml` (XML documentation, about 5,500 documented members). One dependency: `YamlDotNet` 11.0.1 |
| Versions | 1.0.2 through 1.1.7 (1.1.6.1 included). The package version is the LabAPI version |
| Is it the server's DLL | The 1.1.7 package DLL is byte-identical (same SHA-256) to the `LabApi.dll` in the 14.2.7 dedicated server's `Managed/` folder. A different package version is a different build |
| What it gives you | The LabAPI reference without an installed server, and IntelliSense: the XML file sits next to the DLL in the package cache, so the editor shows the summaries. A `HintPath` to `Managed/LabApi.dll` has no XML beside it and shows none |
| What it does NOT give you | The game assemblies (`Assembly-CSharp`, Unity, Mirror): LabAPI's wrappers expose game types such as `ReferenceHub`, so most plugins still need the Managed folder. Nor the server's runtime BCL: a `net48` project that takes the package alone gets the net48 reference pack, and the CS1061 trap above applies in full |

**Version-match rule.** Use the package version equal to the LabAPI that your target server runs, and set `RequiredApiVersion` to the same literal. The server loads its own `Managed/LabApi.dll` at runtime whatever you compiled against, so a newer package than the server compiles but can throw `MissingMethodException` on the server (UNVERIFIED across versions). Ship neither `LabApi.dll` nor `YamlDotNet.dll`.

**Recommended combination:** NuGet for LabAPI and IntelliSense, the Managed folder for the BCL and the game types.

```xml
<PropertyGroup>
  <TargetFramework>net48</TargetFramework>
  <DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences>
  <AutomaticallyUseReferenceAssemblyPackages>false</AutomaticallyUseReferenceAssemblyPackages>
  <FrameworkPathOverride>$(SL_REFERENCES)</FrameworkPathOverride>
  <AddAdditionalExplicitAssemblyReferences>false</AddAdditionalExplicitAssemblyReferences>
</PropertyGroup>
<ItemGroup>
  <!-- BCL and game types from the server's Managed folder, never copied to the output -->
  <Reference Include="mscorlib"><HintPath>$(SL_REFERENCES)/mscorlib.dll</HintPath><Private>false</Private></Reference>
  <Reference Include="System"><HintPath>$(SL_REFERENCES)/System.dll</HintPath><Private>false</Private></Reference>
  <Reference Include="System.Core"><HintPath>$(SL_REFERENCES)/System.Core.dll</HintPath><Private>false</Private></Reference>
  <Reference Include="netstandard"><HintPath>$(SL_REFERENCES)/netstandard.dll</HintPath><Private>false</Private></Reference>
  <Reference Include="Assembly-CSharp"><HintPath>$(SL_REFERENCES)/Assembly-CSharp.dll</HintPath><Private>false</Private></Reference>
  <Reference Include="UnityEngine.CoreModule"><HintPath>$(SL_REFERENCES)/UnityEngine.CoreModule.dll</HintPath><Private>false</Private></Reference>
  <!-- LabAPI from NuGet: pin to the server's LabAPI version; the runtime asset is not copied -->
  <PackageReference Include="Northwood.LabAPI" Version="1.1.7" ExcludeAssets="runtime" PrivateAssets="all" />
</ItemGroup>
```

Do not also reference `Managed/LabApi.dll`: two copies of one assembly. A probe using tuple deconstruction in a `foreach` over a `Dictionary` (the CS1061 canary) and `Player.Count` builds with this snippet with 0 warnings and 0 errors, and the output folder holds only the plugin DLL.

**Why the examples in this repository reference the installed `LabApi.dll` instead.** (1) Exact runtime match: the examples are meant to compile against the very binary the server loads, and the file in `Managed/` is that binary by definition; with the NuGet package a version drift is possible and silent. (2) Offline builds: a build against `Managed/` needs no package restore, so it works on a machine without NuGet access and gives the same result every time. (3) One mechanism: the same `SL_REFERENCES` folder already supplies the game assemblies and the BCL, so a second source for LabAPI adds a version to keep in step. The cost is no IntelliSense XML; that is the one reason to prefer the NuGet package in your own plugin.

## Plugin class skeleton

```csharp
using System;
using LabApi.Loader.Features.Plugins;
using LabApi.Events.Handlers;

public sealed class MyPlugin : Plugin<MyConfig>
{
    public override string Name => "MyPlugin";              // config directory name; constant
    public override string Description => "Short text.";
    public override string Author => "you";
    public override Version RequiredApiVersion => new Version(1, 1, 7); // literal, never CompiledVersion

    public override void Enable()
    {
        Config ??= new MyConfig();              // empty config.yml deserialises to null
        // 1. fallible work (IO, HTTP client creation) first
        // 2. subscribe / start coroutines last
        ServerEvents.Shutdown += OnShutdown;    // Disable() is never called
    }

    public override void Disable() { }          // intentionally empty

    private void OnShutdown() { /* synchronous flush, process dies right after */ }
}
```

`Config ??=` assigns through the public setter on `Plugin<T>.Config`.

## Re-verifying on a new build

- Re-run the CS1061 comparison: compile a probe that uses one member from each "present" row against `Managed/` and against the net48 pack.
- Re-check the ABSENT rows: grep each name in a decompile of `mscorlib.dll` and `System.Core.dll` (a type or method list from any IL decompiler).
- Confirm whether `0Harmony.dll` is still expected in `LabAPI/dependencies/<port>` (and its version), and that `MEC.Timing` is still in `Assembly-CSharp-firstpass.dll`.
- Check `Managed/` still contains `netstandard.dll` and the four reference assemblies above.
- Check LabAPI's `LabApiProperties.CompiledVersion` is still `static readonly` (public source on GitHub).
- Re-check nuget.org for `Northwood.LabAPI`: the latest version, that `lib/net48` still holds the DLL and the XML, that the dependency list is still only `YamlDotNet`, and whether the package DLL still has the same SHA-256 as the server's `Managed/LabApi.dll` for the same version.
- Rebuild the NuGet snippet above against the new `Managed/` folder (tuple deconstruction canary plus one wrapper call).
