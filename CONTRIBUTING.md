# Contributing

Contributions should stay focused, preserve the app's offline behavior, and avoid
adding dependencies without a clear product need.

Before opening a pull request, run:

```powershell
dotnet build .\YewCone.NuGetDiff\YewCone.NuGetDiff.sln -c Release
dotnet publish .\YewCone.NuGetDiff\YewCone.NuGetDiff\YewCone.NuGetDiff.csproj -c Release
```

Describe user-visible changes and any effect on the published WebAssembly or
service-worker cache size.
