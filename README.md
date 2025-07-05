# NuGet Package Diff

A Blazor WebAssembly application that compares different versions of NuGet packages, showing differences between files and decompiled .NET assemblies.

## Features

- **Package Comparison**: Compare any two versions of NuGet packages
- **Assembly Decompilation**: Automatically decompiles .NET assemblies using ILSpy
- **Multiple Diff Views**: 
  - File Tree View: Overview of all changed files
  - Side-by-Side View: Traditional side-by-side comparison
  - Unified View: Git-style unified diff format
- **URL Support**: Direct linking to comparisons via URL parameters
- **PWA Support**: Installable as a Progressive Web App for offline use
- **WebAssembly**: All processing happens client-side in the browser

## Usage

### Web Interface

1. Navigate to the application
2. Enter the package name and versions you want to compare
3. Optionally specify custom NuGet sources
4. Click "Compare Versions" to start the comparison

### URL Parameters

You can also access comparisons directly via URL:

**Full format:**
```
https://your-domain/?name=Newtonsoft.Json&newName=Newtonsoft.Json&version=13.0.1&newVersion=13.0.3&source=https://api.nuget.org/v3/index.json&newSource=https://api.nuget.org/v3/index.json
```

**Simplified format:**
```
https://your-domain/?name=Newtonsoft.Json&version=13.0.1&newVersion=13.0.3
```

### Parameters

- `name`: Package name (required)
- `version`: Old version (required)
- `newVersion`: New version (required)
- `newName`: New package name (optional, defaults to `name`)
- `source`: NuGet source URL (optional, defaults to official NuGet.org)
- `newSource`: New package source URL (optional, defaults to `source`)

## Building

### Prerequisites

- .NET 10 SDK (preview)
- Modern web browser with WebAssembly support

### Development

```bash
# Clone the repository
git clone https://github.com/AndreyTretyak/NuGetDiff.git
cd NuGetDiff

# Build the solution
dotnet build

# Run the development server
cd NuGetDiff.Client
dotnet run
```

The application will be available at `http://localhost:5002`

### Production Build

```bash
# Build for production
dotnet publish NuGetDiff.Client -c Release -o ./publish

# Deploy the contents of ./publish/wwwroot to your web server
```

## How It Works

1. **Package Download**: Downloads NuGet packages from the specified source using the NuGet API
2. **Extraction**: Extracts files from the NuGet package (which is a ZIP archive)
3. **Decompilation**: Uses ICSharpCode.Decompiler (ILSpy) to decompile .NET assemblies to C# source code
4. **Comparison**: Uses DiffPlex to compare file contents and generate detailed diffs
5. **Visualization**: Displays results in multiple formats for easy analysis

## Architecture

- **Frontend**: Blazor WebAssembly with Bootstrap for UI
- **Services**:
  - `NuGetService`: Downloads and extracts NuGet packages
  - `DecompilerService`: Decompiles .NET assemblies using ILSpy
  - `DiffService`: Compares files and generates diff results
- **Components**:
  - `FileTreeView`: Shows overview of file changes
  - `SideBySideDiffView`: Side-by-side comparison view
  - `UnifiedDiffView`: Unified diff format view

## Dependencies

- **ICSharpCode.Decompiler**: For decompiling .NET assemblies
- **DiffPlex**: For text diffing algorithms
- **Microsoft.AspNetCore.WebUtilities**: For URL query parsing
- **Bootstrap**: For responsive UI components

## Progressive Web App

The application includes PWA support:

- **Manifest**: Configured for installation on devices
- **Service Worker**: Provides offline caching for framework files
- **Icons**: Includes app icons for various device sizes

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for information on contributing to this project.

## License

This project is licensed under the [MIT license](LICENSE).
