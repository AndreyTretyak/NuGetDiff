# NuGetDiff

A Blazor WebAssembly app that browses the contents of any NuGet package and diffs
two versions side‑by‑side, including decompiled C# of assemblies via
[ILSpy](https://github.com/icsharpcode/ILSpy)'s `ICSharpCode.Decompiler`.

Runs entirely in the browser — no server required. Once a package has been
loaded it is cached in IndexedDB so the next visit is fully offline.

## URL contract

- `mysite.com/` — landing page with package lookup and `.nupkg` upload zone
- `mysite.com/{id}` — list of versions for `{id}` from nuget.org
- `mysite.com/{id}/{version}` — browse the contents of one package
- `mysite.com/{id}/{version}/file?path={packagePath}` — view a single file
  (Markdown rendered, text plain, assemblies decompiled to C#)
- `mysite.com/{id}/{v1}/{v2}` — full diff of two versions (file tree + per‑file)
- `mysite.com/{id}/{v1}/{v2}/file?path={packagePath}` — diff one file
- `mysite.com/local/{contentHash}` — same browse view backed by a `.nupkg`
  uploaded into the current session

The path inside a package is passed via `?path=` so that reserved URL
characters like `#`, `+` and `%` round‑trip correctly.

## Solution layout

```
NuGetDiff.slnx
├── src/
│   ├── NuGetDiff.Core/    # Pure managed library: package reader, decompiler, differ
│   ├── NuGetDiff.Web/     # Blazor WebAssembly client (the actual UI)
│   └── NuGetDiff.Server/  # ASP.NET Core host that serves the WASM client
└── tests/
    └── NuGetDiff.Core.Tests/  # xUnit
```

`NuGetDiff.Core` has no Blazor or filesystem dependencies, so it works
unmodified in WebAssembly and is easy to unit‑test on desktop .NET. All
package access is via streams; ILSpy's decompiler is wrapped with a custom
in‑memory `IAssemblyResolver` that resolves sibling assemblies inside the
package without touching disk.

`NuGetDiff.Server` is a minimal ASP.NET Core process whose only job is to
serve the WASM client with a SPA fallback that handles dotted URL segments
(e.g. `Newtonsoft.Json` and `13.0.1`) which the default Blazor WASM dev
server treats as static‑file requests and 404s.

## Building and testing

```pwsh
dotnet build NuGetDiff.slnx
dotnet test  NuGetDiff.slnx
dotnet run   --project src/NuGetDiff.Server
```

The host listens on `http://localhost:5180` (see `launchSettings.json`).

To produce a deployable artifact:

```pwsh
dotnet publish src/NuGetDiff.Server -c Release
```

The output under `bin/Release/net8.0/publish/` is a self‑contained ASP.NET
Core app that hosts the WASM client. To deploy to a pure static host
(GitHub Pages, S3, etc.) you can publish only the WASM client
(`dotnet publish src/NuGetDiff.Web -c Release`) and serve
`bin/Release/net8.0/publish/wwwroot/` — but the host **must** be configured
to fall back to `index.html` for unknown paths, including those with dots,
or routes like `/Newtonsoft.Json/13.0.1` will 404. On GitHub Pages the
standard `404.html` redirect trick is the usual answer.

## Offline use

1. Drop a `.nupkg` into the upload zone on the home page. The app reads it,
   navigates to `/local/{contentHash}`, and adds the bytes to the in‑memory
   `UploadedPackageSource` so the package can be browsed without network
   access.
2. Online packages are cached in IndexedDB after the first download. The next
   visit reuses the cached bytes.
3. The PWA service worker caches the app shell and Blazor runtime, so the
   site itself loads even with the network completely off.

## Security note

NuGet packages are untrusted input. Markdown READMEs are rendered with
Markdig configured to disable raw HTML, package text files are shown as
plain text inside `<pre>`, and decompiled C# is treated as text. Embedded
HTML/SVG inside packages is never injected into the DOM.

## Limitations (v1)

- Symbol packages (`.snupkg`) and PDB‑aware decompilation are not supported.
- Decompilation produces C# only; no IL view toggle.
- Default Blazor WebAssembly is single‑threaded, so very large packages
  produce noticeable busy time on the UI thread (a top‑level overlay shows
  progress between chunks).
- Only nuget.org is queried for online versions and downloads.

## License

MIT — see [LICENSE](LICENSE).
