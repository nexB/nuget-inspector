﻿using NuGet.LibraryModel;
using NuGet.ProjectModel;

namespace NugetInspector;

/// <summary>
/// Handles legacy project.json format
/// See https://learn.microsoft.com/en-us/nuget/archive/project-json
/// </summary>
internal class LegacyProjectJsonHandler : IDependencyResolver
{
    public const string DatasourceId = "dotnet-project.json";
    private readonly string ProjectJsonPath;
    private readonly string? ProjectName;

    public LegacyProjectJsonHandler(string? projectName, string projectJsonPath)
    {
        ProjectName = projectName;
        ProjectJsonPath = projectJsonPath;
    }

    public DependencyResolution Resolve()
    {
        var result = new DependencyResolution();
        var model = JsonPackageSpecReader.GetPackageSpec(name: ProjectName, packageSpecPath: ProjectJsonPath);
        IList<LibraryDependency> packages = model.Dependencies;
        foreach (var package in packages)
        {
            var bpwd = new BasePackage(
                name: package.Name,
                version: package.LibraryRange.VersionRange.OriginalString
            );
            result.Packages.Add(item: bpwd);
            result.Dependencies.Add(item: bpwd);
        }

        return result;
    }
}