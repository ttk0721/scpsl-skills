# The deploy template

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

`examples/deploy-template/` is a complete, compiled plugin project (builds with 0 errors, lints clean). Copy it, rename, delete what you do not need. It has no SlKit dependency.

| File | Job |
|---|---|
| `Directory.Build.props` | Reference setup: Managed BCL, `FrameworkPathOverride`, `Private=false`, default install path per OS, a guard for a wrong path |
| `DeployTemplate.csproj` | Per-client branding from one property |
| `DeployTemplate.cs` | Config, plugin, build/scope log line, Shutdown flush |
| `verify-deploy.sh` | `layout` check before the restart, `log` check after it |
| `DEPLOY-CHECKLIST.md` | The step list, from "does this user want a server" to cleanup |

## Directory.Build.props: the reference pattern

```xml
<SL_REFERENCES Condition="'$(SL_REFERENCES)' == '' and '$(OS)' != 'Windows_NT'">$(HOME)/.local/share/Steam/steamapps/common/SCP Secret Laboratory Dedicated Server/SCPSL_Data/Managed</SL_REFERENCES>
<DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences>
<AutomaticallyUseReferenceAssemblyPackages>false</AutomaticallyUseReferenceAssemblyPackages>
<FrameworkPathOverride>$(SL_REFERENCES)</FrameworkPathOverride>
<AddAdditionalExplicitAssemblyReferences>false</AddAdditionalExplicitAssemblyReferences>
```

Why each line: without `FrameworkPathOverride` the SDK resolves `mscorlib` from the desktop pack even when you list `Managed/mscorlib.dll` yourself, and the CS1061 trap stays (build-and-references.md). `SL_REFERENCES` can come from `-p:` or the environment (an environment variable is an MSBuild property). The Linux default is the Steam client library; the Windows default (`$(MSBuildProgramFiles32)\Steam\...`) is unverified like every Windows path. A SteamCMD or other-library install has no default: the user gives the path, you pass it with `-p:SL_REFERENCES=`.

MSBuild takes the NEAREST `Directory.Build.props` going up. The template's props shadow `examples/Directory.Build.props` on purpose, which is why the template builds without SlKit.

## DeployTemplate.csproj: branding from one property

```xml
<Client Condition="'$(Client)' == ''">default</Client>
<AssemblyName Condition="'$(Client)' == 'default'">DeployTemplate</AssemblyName>
<AssemblyName Condition="'$(Client)' != 'default'">DeployTemplate.$(Client)</AssemblyName>
...
<AssemblyMetadata Include="Client" Value="$(Client)" />
```

```
dotnet build -c Release -p:Client=acme -o out/acme    -> DeployTemplate.acme.dll
dotnet build -c Release               -o out/default  -> DeployTemplate.dll
```

- `AssemblyName` is what the operator sees: file name and the `[Prefix]` of every `Logger` line (the prefix is the calling assembly's simple name).
- `AssemblyMetadata` bakes the client id into the DLL; `Branding.Client` reads it back with reflection, so the log line can name the build without a config key.
- Always pass `-o` per client. Both variants share `obj/`, and the next build's incremental clean deleted the previous variant's DLL from the shared `bin/` (measured).
- `Plugin.Name` stays a constant, so variants share one config directory. Decide that on purpose (verify-and-brand.md).
- `Client` is a file name and a log prefix: path-safe characters only.

## DeployTemplate.cs: what the plugin proves at boot

```csharp
public override Version RequiredApiVersion => new Version(1, 1, 7);   // literal, never CompiledVersion

public override void Enable()
{
    Config ??= new DeployTemplateConfig();            // empty config.yml gives null
    if (!Config.IsEnabled || _subscribed) return;     // kill switch, idempotent

    Logger.Info("build=" + Branding.Client
        + " scope=" + (DeployScope.FromFilePath(FilePath) ?? "unknown")
        + " port=" + Server.Port
        + " file=" + FilePath);

    ServerEvents.Shutdown += OnShutdown;              // subscribe last
    _subscribed = true;
}
```

The first line in the boot log names the build, the folder scope (`global` or the port: the segment after the last `plugins`) and the file. After a deploy that answers "which DLL is actually running, and from where" without opening the file system. `FilePath` is valid in `Enable()` (set after the constructor); `Assembly.Location` is empty for plugins. `Server.Port` is readable in `Enable()` (set from the launch arguments before plugins load).

## verify-deploy.sh

```
bash verify-deploy.sh layout "<root>/LabAPI" <port>             # before the restart
bash verify-deploy.sh log "<LocalAdmin log of this boot>" DeployTemplate Other   # after
```

`layout` fails on: the same DLL name twice anywhere below the four scanned folders; a game or runtime assembly (`LabApi.dll`, `Assembly-CSharp*.dll`, `Mirror.dll`, `UnityEngine*.dll`, ...); and a `plugins/global` DLL that seems to reference a library present only in `dependencies/<port>`. It warns on backup-looking names. The global check is a heuristic: assembly references are plain names in the DLL metadata, so it greps the library's file stem. A hit is a candidate to confirm, a miss is not proof (a reference to a differently named library is not seen).

`log` fails when a named plugin has no `Successfully enabled '<Name>'` line or when any loader failure string from verify-and-brand.md appears. Names are `Plugin.Name`, not file names. Exit code 0 clean, 1 findings, 2 usage. Tested on Linux; Windows only under Git Bash or WSL, untested.

## Global plugins and their libraries

A plugin in `plugins/global` runs on every port; each port loads only `dependencies/global` plus its own `dependencies/<port>`. So the plugin's non-game libraries belong in `dependencies/global`. A global plugin needing `0Harmony` that sits only in one port's `dependencies/` loads on that port and throws `ReflectionTypeLoadException` on the other port. Move the library (one copy; a second copy in `global` plus the port folder trips the duplicate check), restart, re-verify every port that uses the plugin. The mirror case: a library in `plugins/<port>` is invisible to a `global` plugin, because global plugin types are instantiated before `<port>` DLLs are loaded.

## Common Mistakes

- Building variants into the same `bin/`: only the last survives.
- Building with the default `SL_REFERENCES` where the server was installed through SteamCMD: the default path does not exist. The template stops with one error naming the variable (without that guard, a wrong path gives dozens of `CS0518 System.Object is not defined`). Pass the path.
- Treating `verify-deploy.sh layout` "clean" as proof for the global-dependency case: it is a string match.
- Deploying the template to the user's own port to "just see it load". Ask first, offer a separate port (running-a-test-server).

## Re-verifying on a new build

- Build both variants (`-p:Client=acme -o ...` and default) against the new `Managed/`; confirm 0 errors and that each output folder holds only its own DLL and `.pdb`.
- Confirm `AssemblyMetadataAttribute` still resolves from the server's `mscorlib` (the compile does).
- Boot a server with the branded DLL in `plugins/<port>`; confirm the log prefix is the branded assembly name and the `scope=` value is right. Move the same DLL to `plugins/global` and confirm `scope=global`.
- Reproduce the global-dependency failure: a global plugin that references `0Harmony`, the library only in one port's `dependencies/`, boot the other port.
- Run `verify-deploy.sh` against a tree with a deliberate duplicate, a stray `LabApi.dll` and a port-only dependency; expect three findings.
- Check `Server.Port` is still readable in `Enable()` and `FilePath` still set after the constructor.
