# Sources and decompiling

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Part of researching-scpsl-internals. Commands tested with ilspycmd 11.0 on Linux; `<Managed>` below is `<server install>/SCPSL_Data/Managed`.

## Where the answer lives

| Question is about | Read | Cost |
|---|---|---|
| A LabAPI type, event, wrapper, loader behaviour | LabAPI source (GitHub `northwood-studios/LabAPI`, LGPL-3.0). Clone the tag that matches `LabApiProperties.CompiledVersion` of the installed `LabApi.dll` | Free to read, may be quoted briefly |
| What the game does when LabAPI raises an event, what a wrapper really calls | `Assembly-CSharp.dll`, decompiled locally | One method is 1-2 KB, one type 5-200 KB |
| Coroutines (`MEC.Timing`) | `Assembly-CSharp-firstpass.dll` (MEC and the Discord game SDK are there) | `MEC.Timing` alone is about 175 KB of C# [M] |
| Networking primitives (`NetworkServer`, SyncVar plumbing, transport) | `Mirror.dll` | |
| Pools, `ActionDispatcher` | `NorthwoodLib.dll`; `GameObjectPools` is `Pooling.dll` | |
| "Does this BCL API exist on the server?" | The server's own `mscorlib.dll`, `System.*.dll` in `<Managed>`, never docs | Seconds |
| What another plugin does | Its source, or the DLL decompiled the same way. EXILED: GitHub `ExMod-Team/EXILED`; always compare the installed DLL version with the source tag | |
| Harmony behaviour | The `0Harmony.dll` that sits in `LabAPI/dependencies/<port|global>/`, not the version you build against | |
| What the server does at runtime | A probe plugin or the server log (see reverifying-a-build.md) | Needs a server |

The installed build is in the Steam manifest of the dedicated-server app (id 996560): `buildid` in `steamapps/appmanifest_996560.acf`. `GameCore.Version` in the decompile carries the human-readable `14.2.7`.

**EULA.** SCP:SL EULA section 12 forbids distributing non-public Northwood data. Decompiled output stays on your machine: never commit it, never paste bodies into a skill, issue or PR. Describe behaviour, name public types and members.

## ilspycmd, three granularities

Install: `dotnet tool install -g ilspycmd`. Always pass `--disable-updatecheck` in scripts. Write the output to a file (`-o dir`) and grep it; printing a type to the terminal is the most common way to burn tokens.

| Need | Command shape | Result |
|---|---|---|
| One method | `ilspycmd --disable-updatecheck -m "M:Namespace.Type.Method(Param.Type)" -o <out> <dll>` | `<out>/<Assembly>.decompiled.cs`, only that method (136 bytes for `MEC.Timing.WaitForSeconds`, 61 lines for the damage entry method) [M] |
| One type | `... -t Namespace.Type -o <out> <dll>` | `<out>/Namespace.Type.decompiled.cs`. Nested types are included. Generic types need the backtick arity, in single quotes: `-t 'System.Collections.Generic.Dictionary`2'` |
| Whole assembly | `... -p -o <out> <dll>` | A project directory: one folder per namespace, one `.cs` per type, root-namespace types at the top. Re-running with the same ilspycmd version is byte-identical except the `.csproj` [M] |
| Names only | `... -l c -o <out> <dll>` (or `-l s`, `i`, `e`, `d`) | `<out>/<Assembly>.list.txt`, one `Class Full.Name` per line. Without `-o` it prints |

Doc-id forms for `-m` : `M:Ns.Type.Method(System.Single)`, constructors `M:Ns.Type.#ctor` (add the parameter list when overloaded), property getters `M:Ns.Type.get_Name` (the output is the bare body, no signature line). If a doc id fails, list the type with `-t` and read the exact signature.

**`-il` ignores `-t` and `-m`.** It dumps the whole assembly's IL: 2 MB for `Assembly-CSharp-firstpass`, 40 MB for `Assembly-CSharp` [M]. Use it once per build into a file and grep that file (grep-recipes.md, IL size).

Never run a plain `ls` on the decompiled root (thousands of entries). `ls -d */` or the namespace map is enough. The tree is flat: `PlayerRoles.PlayableScps.Scp079` is one directory name with dots.

## Preparing a local research tree (once per build)

1. Decompile `Assembly-CSharp.dll` with `-p` into a directory outside every git repository. Repeat for `Mirror.dll` if networking questions come up. A project decompile is quick enough (not timed here) to redo per build instead of patching an old tree.
2. Clone LabAPI at the matching tag next to it. Clone EXILED only when porting or auditing.
3. Record the build id, the LabAPI version and the ilspycmd version in a README in that directory. Two trees can be compared only if the ilspycmd versions match.
4. Add the directory to your global gitignore, and point `tools/check-publishable.py --corpus` at the `Assembly-CSharp` tree before every release of this library.

## Decompiler traps

- Decompiler output is a reconstruction. Compiler-generated closures, `yield` state machines and `foreach` over arrays are rewritten; IL size and inlining questions need IL, not C#.
- Mirror-weaved classes show generated `Network_` properties, `GeneratedSyncVarSetter` and invoke helpers next to your `[SyncVar]`/`[Command]` source. The attribute is the contract; the generated parts tell you the dirty bit each SyncVar uses.
- Zero C# call sites do not prove a type unused: MonoBehaviours and `NetworkBehaviour`s are attached in scenes and prefabs, which the decompile does not contain. Say "no call site", not "dead code".

## Re-verifying on a new build

- `ilspycmd --version` equal to the one that produced the old tree; if not, regenerate both trees with the new version before diffing.
- Re-run `-l c` for each assembly you rely on and compare type counts and namespaces with the old list.
- Check that `-m`, `-t`, `-l` and `-p` still behave as described (flag names, output file names). The `-il` whole-assembly behaviour is the one most likely to be fixed upstream; if it is, replace the grep-the-whole-dump recipe.
- Confirm the doc-id example still resolves on the new build, then fix the example in this file if a type moved.
