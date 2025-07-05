# NugetDiff

This would be Blasor Web Assembly application that display diffirence between diffirent version of nuget packages.
For example when opening
https://nugetdiff.lab?name=Newtonsoft.Json&newName=Newtonsoft.Json&version=13.0.1&newVersion=13.0.3&source=https://api.nuget.org/v3/index.json&newSource=https://api.nuget.org/v3/index.json
Or simplified version
https://nugetdiff.lab?name=Newtonsoft.Json&version=13.0.1&newVersion=13.0.3
It would display three view of the diffirence between between files in the version 13.0.1 and 13.0.3 of Newtonsoft.Json. Nuget packages could be donwloaded using regular web client, then unzipped as regular zip archive, for comparing .NET assemblies they should be decomplied using ILSpy library.
Ideally I would like this web assembly app to be installable to run locally in browser, so all the work should be happening in web assembly.

## Building

TODO

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for information on contributing to this project.

This project has adopted the code of conduct defined by the [Contributor Covenant](http://contributor-covenant.org/) 
to clarify expected behavior in our community. For more information, see the [.NET Foundation Code of Conduct](http://www.dotnetfoundation.org/code-of-conduct).

## License

This project is licensed with the [MIT license](LICENSE).