# Deploy checklist

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Copy this file next to your plugin; fill in the paths once.

Facts behind each step: skill `deploying-scpsl-plugins` (loader-and-config.md, verify-and-brand.md). Finding or starting a server: skill `running-a-test-server`.

## 0. Is there a server to deploy to?

- [ ] The user has a dedicated server and WANTS a local test. If not, or unsure: stop here. Hand over the build and say nothing ran on a server. Never push a test on someone who did not ask.
- [ ] Install folder (`SCPSL_Data/Managed` for the build) and config root (`<root>/LabAPI`) taken from the defaults in `running-a-test-server/paths.md`. A different layout: ask, reuse the answer.
- [ ] Port chosen with the user. Their running or populated ports are off limits unless they say yes for that port. Default offer: our own instance on a free port (for example 9999). Is it private? (`contact_email` unset, "won't be visible on the public server list" in the boot log.)

## 1. Build

- [ ] `dotnet build -c Release -p:SL_REFERENCES="<install>/SCPSL_Data/Managed"` gives 0 errors, 0 warnings.
- [ ] A branded build goes to its own folder: `-p:Client=acme -o out/acme`. Without `-o` the next variant's build deletes the previous variant's DLL from `bin/`.
- [ ] The output folder holds only your DLL (and `.pdb`). Any game DLL there means a missing `Private=false`.
- [ ] `RequiredApiVersion` is a literal in every variant.

## 2. Place the files

- [ ] Plugin DLL: `plugins/<port>` for one server, `plugins/global` for every server on the machine. Tests go in `<port>`, never `global`.
- [ ] Libraries: `dependencies/`, one copy of each, never `plugins/`.
- [ ] **A plugin in `plugins/global` needs every non-game library in `dependencies/global`.** Each port loads only `dependencies/global` plus its own `dependencies/<port>`, and a `global` plugin runs on every port. A global plugin whose library (for example `0Harmony`) sits only in one port's `dependencies/` fails with `ReflectionTypeLoadException` on the other ports. Move the library to `dependencies/global`. Likewise a library inside `plugins/<port>` is invisible to a `global` plugin (global types are instantiated before `<port>` DLLs load).
- [ ] Old build removed; backups renamed `.bak`, not `*.dll`, not in an `old/` folder (everything below the scanned folders is loaded).
- [ ] `bash verify-deploy.sh layout "<root>/LabAPI" <port>` prints `clean`.

## 3. Restart

- [ ] Stop and start the game process. No code change is picked up otherwise; `labapi reload configs` reloads configs only.

## 4. Verify the boot

- [ ] `bash verify-deploy.sh log "<LocalAdmin log of THIS boot>" <Plugin.Name> [...]` prints `clean`: every plugin has `Successfully enabled '<Name>'` and the failure strings are absent.
- [ ] The plugin's own first line shows the intended build: `build=<client> scope=<global|port> port=<port> file=<path>`, prefixed `[<AssemblyName>]`.
- [ ] `configs/<port>/<Name>/config.yml` exists and has your keys.
- [ ] Not tested: write down what this boot did not exercise (events, players, rounds). A boot check proves loading, not behaviour.

## 5. Clean up what you created

- [ ] Stop your own instance. Remove only files you created (birth time, not modification time: configs are rewritten on every load). Never remove `Round Logs Archive` zips.
