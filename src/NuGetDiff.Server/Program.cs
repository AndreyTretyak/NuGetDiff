var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.UseRouting();

// Use an explicit catch-all "{**path}" instead of the default
// "{*path:nonfile}" used by MapFallbackToFile(filePath). The default constraint
// rejects URLs whose last segment contains a dot (e.g. "Newtonsoft.Json" or
// "13.0.1"), which would 404 every NuGet id and every semver version.
app.MapFallbackToFile("{**path}", "index.html");

app.Run();
