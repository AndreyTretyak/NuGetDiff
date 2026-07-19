# NuGetDiff

NuGetDiff is a Blazor WebAssembly progressive web app for understanding changes
between NuGet package versions. The repository currently provides the lightweight
application shell; the package comparison workflow has not been implemented yet.

## Requirements

- .NET 10 SDK

## Run locally

```powershell
dotnet run --project .\YewCone.NuGetDiff\YewCone.NuGetDiff\YewCone.NuGetDiff.csproj
```

## Build and publish

```powershell
dotnet build .\YewCone.NuGetDiff\YewCone.NuGetDiff.sln -c Release
dotnet publish .\YewCone.NuGetDiff\YewCone.NuGetDiff\YewCone.NuGetDiff.csproj -c Release
```

The development service worker always uses the network so local changes are visible
immediately. Release publishes generate a versioned offline cache containing the
Blazor runtime and the small NuGetDiff application shell.

## Project layout

- `YewCone.NuGetDiff\YewCone.NuGetDiff` - Blazor WebAssembly application
- `YewCone.NuGetDiff\YewCone.NuGetDiff\wwwroot` - static and PWA assets

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) and [CODE-OF-CONDUCT.md](CODE-OF-CONDUCT.md).

## License

NuGetDiff is licensed under the [MIT License](LICENSE).
