﻿using System.Diagnostics;
using System.Xml;
using Microsoft.Build.Locator;
using NuGet.Frameworks;

namespace NugetInspector;

internal static class Program
{
    public static void Main(string[] args)
    {
        try
        {
            if (Config.TRACE) Console.WriteLine("Registering MSBuild defaults.");
            MSBuildLocator.RegisterDefaults();
        }
        catch (Exception e)
        {
            Console.WriteLine($"Failed to register MSBuild defaults: {e}");
            Environment.Exit(exitCode: -1);
        }

        var exit_code = 0;
        var options = ParseCliArgs(args: args);

        if (options.Success)
        {
            var execution = ExecuteInspector(options: options.Options);
            if (execution.ExitCode != 0) exit_code = execution.ExitCode;
        }
        else
        {
            exit_code = options.ExitCode;
        }

        Environment.Exit(exitCode: exit_code);
    }

    /// <summary>
    /// Return True if there is an error or warning in the results.
    /// </summary>
    public static bool Has_errors_or_warnings(OutputFormatJson output)
    {
        var has_top_level = output.scan_result.warnings.Any() || output.scan_result.errors.Any();
        if (has_top_level)
            return true;
        bool has_package_level =  output.scan_result.project_package.warnings.Any() || output.scan_result.project_package.errors.Any();
        if (has_package_level)
            return true;
        bool has_dep_level = false;
        foreach (var dep in output.scan_output.Dependencies)
        {
            if (dep.warnings.Any() || dep.errors.Any())
                has_dep_level = true;
                break;
        }
        return has_dep_level;
    }
 
    private static ExecutionResult ExecuteInspector(Options options)
    {
        if (Config.TRACE)
        {
            Console.WriteLine("\nnuget-inspector options:");
            options.Print(indent: 4);
        }

        try
        {
            var project_options = new ProjectScannerOptions(options: options);

            (string? framework_warning, NuGetFramework project_framework) = FrameworkFinder.GetFramework(
                RequestedFramework: options.TargetFramework,
                ProjectFilePath: options.ProjectFilePath);

            project_options.ProjectFramework = project_framework.GetShortFolderName();

            var nuget_api_service = new NugetApi(
                nuget_config_path: options.NugetConfigPath,
                project_root_path: project_options.ProjectDirectory,
                project_framework: project_framework);

            var scanner = new ProjectScanner(
                options: project_options,
                nuget_api_service: nuget_api_service,
                project_framework: project_framework);

            Stopwatch scan_timer = Stopwatch.StartNew();

            Stopwatch deps_timer = Stopwatch.StartNew();
            ScanResult scan_result = scanner.RunScan();
            deps_timer.Stop();

            Stopwatch meta_timer = Stopwatch.StartNew();
            scanner.FetchDependenciesMetadata(
                scan_result: scan_result,
                with_details: options.WithDetails);
            meta_timer.Stop();

            scan_timer.Stop();

            if (framework_warning != null)
                scan_result.warnings.Add(framework_warning);

            if (Config.TRACE)
            {
                Console.WriteLine("Run summary:");
                Console.WriteLine($"    Dependencies resolved in: {deps_timer.ElapsedMilliseconds} ms.");
                Console.WriteLine($"    Metadata collected in:    {meta_timer.ElapsedMilliseconds} ms.");
                Console.WriteLine($"    Scan completed in:        {scan_timer.ElapsedMilliseconds} ms.");
            }

            var output_formatter = new OutputFormatJson(scan_result: scan_result);
            output_formatter.Write();

            BasePackage project_package = scan_result.project_package;

            bool success = scan_result.Status == ScanResult.ResultStatus.Success;
            // also consider other errors
            if (success && Has_errors_or_warnings(output_formatter))
                success = false;

            if (success)
            {
                Console.WriteLine($"Scan Result: success: JSON file created at: {scan_result.Options!.OutputFilePath}");
                return ExecutionResult.Succeeded();
            }
            else
            {
                Console.WriteLine($"Scan Result: Error or Warning: JSON file created at: {scan_result.Options!.OutputFilePath}");
                if (scan_result.warnings.Any()) Console.WriteLine("   WARNING: " + string.Join(", ", scan_result.warnings));
                if (scan_result.errors.Any()) Console.WriteLine("   ERROR: " + string.Join(", ", scan_result.errors));
 
                Console.WriteLine("Error or Warning at the package level");
                Console.WriteLine($"   {project_package.name}@{project_package.version} with purl: {project_package.purl}");
                if (project_package.warnings.Any()) Console.WriteLine("        WARNING: " + string.Join(", ", project_package.warnings));
                if (project_package.errors.Any()) Console.WriteLine("        ERROR: " + string.Join(", ", project_package.errors));
                Console.WriteLine("Error or Warning at the dependencies level");
                foreach (var dep in project_package.GetFlatDependencies())
                {
                    if (dep.warnings.Any() || dep.errors.Any())
                    {
                        Console.WriteLine($"   {dep.name}@{dep.version} with purl: {dep.purl}");
                        if (dep.warnings.Any()) Console.WriteLine("        WARNING: " + string.Join(", ", dep.warnings));
                        if (dep.errors.Any()) Console.WriteLine("        ERROR: " + string.Join(", ", dep.errors));
                    }
                }
                return ExecutionResult.Failed();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: scan failed:  {ex}");
            return ExecutionResult.Failed();
        }
    }

    private static ParsedOptions ParseCliArgs(string[] args)
    {
        try
        {
            var options = Options.ParseArguments(args: args);

            if (options == null)
            {
                return ParsedOptions.Failed();
            }

            if (string.IsNullOrWhiteSpace(value: options.ProjectFilePath))
            {
                if (Config.TRACE)
                {
                    Console.WriteLine("Failed to parse options: missing ProjectFilePath");
                }

                return ParsedOptions.Failed();
            }

            if (options.Verbose)
            {
                Config.TRACE = true;
            }

            if (Config.TRACE_ARGS)
                Console.WriteLine($"argument: with-details: {options.WithDetails}");

            return ParsedOptions.Succeeded(options: options);
        }
        catch (Exception e)
        {
            if (Config.TRACE)
            {
                Console.WriteLine("Failed to parse options.");
                Console.WriteLine(e.Message);
                Console.WriteLine(e.StackTrace);
            }

            return ParsedOptions.Failed();
        }
    }

    private class ParsedOptions
    {
        public int ExitCode;
        public Options Options = null!;
        public bool Success;

        public static ParsedOptions Failed(int exitCode = -1)
        {
            return new ParsedOptions
            {
                ExitCode = exitCode,
                Options = new Options(),
                Success = false
            };
        }

        public static ParsedOptions Succeeded(Options options)
        {
            return new ParsedOptions
            {
                Options = options,
                Success = true
            };
        }
    }

    private class ExecutionResult
    {
        public int ExitCode;
        public bool Success;

        public static ExecutionResult Failed(int exit_code = -1)
        {
            return new ExecutionResult
            {
                Success = false,
                ExitCode = exit_code
            };
        }

        public static ExecutionResult Succeeded()
        {
            return new ExecutionResult
            {
                Success = true
            };
        }
    }
}