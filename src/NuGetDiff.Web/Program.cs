using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using NuGetDiff.Core.Decompilation;
using NuGetDiff.Core.Diffing;
using NuGetDiff.Core.Packages;
using NuGetDiff.Web;
using NuGetDiff.Web.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient());

builder.Services.AddSingleton<UploadedPackageSource>();
builder.Services.AddSingleton<IPackageCache, IndexedDbPackageCache>();

builder.Services.AddSingleton<NuGetOrgPackageSource>(sp =>
    new NuGetOrgPackageSource(sp.GetRequiredService<HttpClient>()));

builder.Services.AddSingleton<IPackageSource>(sp =>
    new CachingPackageSource(sp.GetRequiredService<NuGetOrgPackageSource>(), sp.GetRequiredService<IPackageCache>()));

builder.Services.AddSingleton<PackageResolver>();
builder.Services.AddSingleton(sp =>
    new NuGetRegistrationClient(sp.GetRequiredService<HttpClient>()));

builder.Services.AddSingleton<MarkdownRenderer>();
builder.Services.AddScoped<BusyState>();

builder.Services.AddTransient<Decompiler>();
builder.Services.AddTransient<Differ>();

var host = builder.Build();

// Make any unobserved task exception visible in the unhandled error UI rather
// than silently swallowed. WebAssembly is single-threaded so this fires on the
// finalizer pass; it's still useful for diagnosis even if rare.
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    Console.Error.WriteLine($"UnobservedTaskException: {e.Exception}");
    e.SetObserved();
};

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    Console.Error.WriteLine($"UnhandledException: {e.ExceptionObject}");
};

await host.RunAsync();
