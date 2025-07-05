using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using NuGetDiff.Client.Models;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace NuGetDiff.Client.Services;

public class DecompilerService
{
    public async Task<PackageInfo> DecompileAssembliesAsync(PackageInfo packageInfo)
    {
        var decompilerSettings = new DecompilerSettings
        {
            ThrowOnAssemblyResolveErrors = false,
            RemoveDeadCode = true,
            RemoveDeadStores = true,
            UseDebugSymbols = false,
            ShowXmlDocumentation = false
        };

        foreach (var file in packageInfo.Files.ToList())
        {
            if (IsAssembly(file.Key))
            {
                try
                {
                    var decompiledContent = await DecompileAssemblyAsync(file.Value, file.Key, decompilerSettings);
                    if (!string.IsNullOrEmpty(decompiledContent))
                    {
                        packageInfo.DecompiledFiles[file.Key] = decompiledContent;
                    }
                }
                catch (Exception ex)
                {
                    // Log the error but continue with other files
                    packageInfo.DecompiledFiles[file.Key] = $"// Error decompiling: {ex.Message}";
                }
            }
        }

        return packageInfo;
    }

    private bool IsAssembly(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension == ".dll" || extension == ".exe";
    }

    private async Task<string> DecompileAssemblyAsync(byte[] assemblyBytes, string fileName, DecompilerSettings settings)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var stream = new MemoryStream(assemblyBytes);
                using var peFile = new PEFile(fileName, stream);
                
                var resolver = new UniversalAssemblyResolver(fileName, false, peFile.DetectTargetFrameworkId());
                var decompiler = new CSharpDecompiler(peFile, resolver, settings);
                
                return decompiler.DecompileWholeModuleAsString();
            }
            catch (BadImageFormatException)
            {
                // Not a valid .NET assembly
                return $"// {fileName} is not a valid .NET assembly";
            }
            catch (Exception ex)
            {
                return $"// Error decompiling {fileName}: {ex.GetType().Name} - {ex.Message}";
            }
        });
    }

    private class UniversalAssemblyResolver : IAssemblyResolver
    {
        private readonly string _fileName;
        private readonly bool _throwOnError;
        private readonly string _targetFramework;

        public UniversalAssemblyResolver(string fileName, bool throwOnError, string targetFramework)
        {
            _fileName = fileName;
            _throwOnError = throwOnError;
            _targetFramework = targetFramework;
        }

        public PEFile? Resolve(IAssemblyReference reference)
        {
            // For Blazor WebAssembly, we'll return null for unresolved assemblies
            // This prevents errors when assemblies reference framework assemblies
            return null;
        }

        public PEFile? ResolveModule(PEFile mainModule, string moduleName)
        {
            return null;
        }

        public Task<PEFile?> ResolveAsync(IAssemblyReference reference)
        {
            return Task.FromResult(Resolve(reference));
        }

        public Task<PEFile?> ResolveModuleAsync(PEFile mainModule, string moduleName)
        {
            return Task.FromResult(ResolveModule(mainModule, moduleName));
        }
    }
}
