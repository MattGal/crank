﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using McMaster.Extensions.CommandLineUtils;

namespace Microsoft.Crank.JobConsumer
{
    public class Program
    {
        private static readonly TimeSpan DriverTimeout = TimeSpan.FromMinutes(10);

        private static string JobsPath { get; set; }
        private static string RepoPath { get; set; }
        private static string DriverPath { get; set; }
        private static string ServerUrl { get; set; }
        private static string ClientUrl { get; set; }

        private static string ProcessingPath => Path.Combine(JobsPath, "processing");
        private static string ProcessedPath => Path.Combine(JobsPath, "processed");

        public static int Main(string[] args)
        {
            var app = new CommandLineApplication();

            app.HelpOption("-h|--help");
            var jobsPath = app.Option("-j|--jobs-path <PATH>", "The path where jobs are created", CommandOptionType.SingleValue).IsRequired();
            var repoPath = app.Option("-r|--repo-path <PATH>", "The path to the repo being benchmarked", CommandOptionType.SingleValue).IsRequired();
            var driverPath = app.Option("-d|--driver <PATH>", "The controller assembly file path", CommandOptionType.SingleValue).IsRequired();
            var serverUrl = app.Option("-s|--server <URL>", "The server url", CommandOptionType.SingleValue).IsRequired();
            var clientUrl = app.Option("-c|--client <URL>", "The client url", CommandOptionType.SingleValue).IsRequired();

            app.OnExecuteAsync(async cancellationToken =>
            {
                JobsPath = jobsPath.Value();
                RepoPath = repoPath.Value();
                DriverPath = driverPath.Value();
                ServerUrl = serverUrl.Value();
                ClientUrl = clientUrl.Value();

                var jobsDirectory = new DirectoryInfo(JobsPath);

                if (!jobsDirectory.Exists)
                {
                    Console.WriteLine($"The path doesn't exist: '{jobsDirectory.FullName}'");
                    return -1;
                }

                if (!File.Exists(DriverPath))
                {
                    Console.WriteLine($"The driver could not be found at: '{DriverPath}'");
                    return -1;
                }

                // Create the target folders if they don't exist
                Directory.CreateDirectory(ProcessingPath);
                Directory.CreateDirectory(ProcessedPath);

                Directory.SetCurrentDirectory(RepoPath);

                Console.WriteLine("Press enter to exit.");

                while (true)
                {
                    FileInfo nextFile = null;

                    // Get oldest file
                    try
                    {
                        var candidateFile = jobsDirectory
                            .GetFiles()
                            .OrderByDescending(f => f.LastWriteTime)
                            .FirstOrDefault();

                        if (candidateFile != null && await WaitForCompleteJsonFile(candidateFile))
                        {
                            nextFile = candidateFile;
                        }
                    }
                    catch (IOException ex)
                    {
                        Console.WriteLine($"Could not enumerate files from jobs directory. Will try again in 1 second. {ex}");
                    }
                    catch (JsonException ex)
                    {
                        Console.WriteLine($"Could not parse JSON job file within 5 seconds. Will try again in 1 second. {ex}");
                    }

                    // If no file was found, wait some time
                    if (nextFile is null)
                    {
                        if (Console.KeyAvailable)
                        {
                            if (Console.ReadKey().Key == ConsoleKey.Enter)
                            {
                                return 0;
                            }
                        }

                        await Task.Delay(1000);
                        continue;
                    }

                    Console.WriteLine($"Found '{nextFile.Name}'");

                    // Attempting to move the file to the processing folder in order to lock it
                    var session = nextFile.Name.Split('.', 2)[0];
                    var processingFilePath = Path.Combine(ProcessingPath, nextFile.Name);
                    var processingFile = new FileInfo(processingFilePath);

                    nextFile.MoveTo(processingFilePath);

                    try
                    {
                        var benchmarkResult = await BenchmarkPR(processingFile, session);
                        await PublishResult(processingFile, benchmarkResult);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Could not successfully benchmark PR. Writing error to comment: {ex}");
                        await PublishError(processingFile, ex);
                    }
                }
            });

            return app.Execute(args);
        }

        private static async Task<BenchmarkResult> BenchmarkPR(FileInfo processingFile, string session)
        {
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            var buildRules = await GetBuildInstructions(processingFile);
            var sdkVersion = await GetSdkVersionOrNull();

            RunCommand("git clean -xdf");
            RunCommand("git fetch");
            RunCommand($"git checkout {buildRules.BaselineSHA}");
            RunBuildCommands(buildRules);

            var baselineArguments = GetDriverArguments(processingFile.FullName, session, sdkVersion, buildRules, isBaseline: true);

            outputBuilder.AppendLine($"Starting baseline run on '{buildRules.BaselineSHA}'...");
            var baselineSuccess = await RunDriver(baselineArguments, outputBuilder, errorBuilder);

            if (!baselineSuccess)
            {
                errorBuilder.AppendLine($"Baseline benchmark run on '{buildRules.BaselineSHA}' failed.");
                return new BenchmarkResult
                {
                    Success = false,
                    BaselineStdout = outputBuilder.ToString(),
                    BaselineStderr = errorBuilder.ToString(),
                };
            }

            var baselineStdout = outputBuilder.ToString();
            var baselinseStderr = errorBuilder.ToString();
            outputBuilder.Clear();
            errorBuilder.Clear();

            RunCommand($"git fetch origin pull/{buildRules.PullRequestNumber}/head:{session}");
            RunCommand($"git checkout {session}");
            RunBuildCommands(buildRules);

            var prArguments = GetDriverArguments(processingFile.FullName, session, sdkVersion, buildRules, isBaseline: false);

            outputBuilder.AppendLine($"Starting PR run on '{buildRules.PullRequestSHA}'...");
            var prSuccess = await RunDriver(prArguments, outputBuilder, errorBuilder);

            if (!prSuccess)
            {
                errorBuilder.AppendLine($"PR benchmark run on '{buildRules.PullRequestSHA}' failed.");
            }

            return new BenchmarkResult
            {
                Success = prSuccess,
                BaselineStdout = baselineStdout,
                BaselineStderr = baselinseStderr,
                PullRequestStdout = outputBuilder.ToString(),
                PullRequestStderr = errorBuilder.ToString(),
            };
        }

        private static async Task<bool> RunDriver(string arguments, StringBuilder outputBuilder, StringBuilder errorBuilder)
        {
            Console.WriteLine($"Running driver with arguments: {arguments}");

            // Don't let the repo's global.json interfere with running the driver
            File.Move("global.json", "global.json~");

            try
            {
                using var process = new Process()
                {
                    StartInfo =
                    {
                        FileName = GetDotNetExecutable(),
                        Arguments = arguments,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                    },
                };

                var sawErrorOutput = false;

                process.OutputDataReceived += (_, e) =>
                {
                    outputBuilder.AppendLine(e.Data);
                    Console.WriteLine(e.Data);
                };

                process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        sawErrorOutput = true;
                    }

                    // Don't omit all newlines, but if there has been nothing but
                    // whitespace so far, ignore the error output.
                    if (sawErrorOutput)
                    {
                        errorBuilder.AppendLine(e.Data);
                        Console.Error.WriteLine(e.Data);
                    }
                };

                process.Start();

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                var start = Environment.TickCount64;

                while (true)
                {
                    if (process.HasExited)
                    {
                        break;
                    }

                    if (Environment.TickCount64 - start > DriverTimeout.TotalMilliseconds)
                    {
                        Console.WriteLine("Driver timed out, skipping job");
                        errorBuilder.AppendLine("Driver timed out, skipping job");
                        process.Kill();

                        return false;
                    }

                    await Task.Delay(1000);
                }

                // Job succeeded?
                return process.ExitCode == 0;
            }
            finally
            {
                File.Move("global.json~", "global.json");
            }
        }

        private static async Task<BuildInstructions> GetBuildInstructions(FileInfo processingFile)
        {
            using var processingJsonStream = File.OpenRead(processingFile.FullName);
            using var jsonDocument = await JsonDocument.ParseAsync(processingJsonStream);

            if (!jsonDocument.RootElement.TryGetProperty(nameof(BuildInstructions), out var buildInstructionsElement))
            {
                throw new InvalidDataException($"Job file {processingFile.Name} doesn't include a top-level '{nameof(BuildInstructions)}' property.");
            }

            var buildInstructions = JsonSerializer.Deserialize<BuildInstructions>(buildInstructionsElement.GetRawText());

            if (!jsonDocument.RootElement.TryGetProperty(buildInstructions.ScenarioName, out var scenarioElement))
            {
                throw new InvalidDataException($"Job file {processingFile.Name} doesn't include a top-level '{buildInstructions.ScenarioName}' property for the specified scenario.");
            }

            if (scenarioElement.TryGetProperty(nameof(BuildInstructions.ExtraDriverArgs), out var extraDriverArgsElement) && extraDriverArgsElement.ValueKind == JsonValueKind.String)
            {
                if (string.IsNullOrEmpty(buildInstructions.ExtraDriverArgs))
                {
                    buildInstructions.ExtraDriverArgs = extraDriverArgsElement.GetString();
                }
                else
                {
                    buildInstructions.ExtraDriverArgs = $"{buildInstructions.ExtraDriverArgs} {extraDriverArgsElement.GetString()}";
                }
            }

            return buildInstructions;
        }

        private static void RunBuildCommands(BuildInstructions buildRules)
        {
            foreach (var buildCommand in buildRules.BuildCommands)
            {
                RunCommand(buildCommand);
            }
        }

        private static async Task PublishResult(FileInfo processingFile, BenchmarkResult jobResult)
        {
            using (var processingJsonStream = File.Open(processingFile.FullName, FileMode.Open))
            {
                var jsonDictionary = await JsonSerializer.DeserializeAsync<Dictionary<string, object>>(processingJsonStream);

                jsonDictionary[nameof(BenchmarkResult)] = jobResult;

                // Clear file and reset position to 0
                processingJsonStream.SetLength(0);
                await JsonSerializer.SerializeAsync(processingJsonStream, jsonDictionary, new JsonSerializerOptions
                {
                    WriteIndented = true,
                });
            }

            processingFile.MoveTo(Path.Combine(ProcessedPath, processingFile.Name));
        }

        private static Task PublishError(FileInfo processingFile, Exception error)
        {
            var errorResult = new BenchmarkResult
            {
                Success = false,
                BaselineStderr = error.ToString()
            };

            return PublishResult(processingFile, errorResult);
        }

        private static string GetDotNetExecutable()
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "dotnet.exe"
                : "dotnet"
                ;
        }

        private static void RunCommand(string command)
        {
            Console.WriteLine($"Running command: '{command}'");

            var outputBuilder = new StringBuilder();

            var splitCommand = command.Split(' ', 2);
            var fileName = splitCommand[0];
            var arguments = splitCommand.Length == 2 ? splitCommand[1] : string.Empty;

            using var process = new Process()
            {
                StartInfo =
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };

            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    outputBuilder.AppendLine($"stdout: {e.Data}");
                    Console.WriteLine(e.Data);
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    outputBuilder.AppendLine($"stderr: {e.Data}");
                    Console.Error.WriteLine(e.Data);
                }
            };

            process.Start();

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new Exception($"Process '{fileName} {arguments}' exited with exit code '{process.ExitCode}' and the following output:\n\n{outputBuilder}");
            }
        }

        private static async Task<bool> WaitForCompleteJsonFile(FileInfo nextFile)
        {
            // Wait up to 5 seconds for the Json file to be fully parsable.
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    using var processedJsonStream = File.OpenRead(nextFile.FullName);
                    using var jsonDocument = await JsonDocument.ParseAsync(processedJsonStream);

                    return true;
                }
                catch (JsonException)
                {
                    if (i == 4)
                    {
                        throw;
                    }

                    await Task.Delay(1000);
                }
            }

            return false;
        }

        private static async Task<string> GetSdkVersionOrNull()
        {
            if (!File.Exists("global.json"))
            {
                return null;
            }

            using var globalJsonStream = File.OpenRead("global.json");
            using var jsonDocument = await JsonDocument.ParseAsync(globalJsonStream);

            if (jsonDocument.RootElement.TryGetProperty("sdk", out var sdkElement) && sdkElement.ValueKind == JsonValueKind.Object)
            {
                if (sdkElement.TryGetProperty("version", out var sdkVersionElement) && sdkVersionElement.ValueKind == JsonValueKind.String)
                {
                    return sdkVersionElement.GetString();
                }
            }

            return null;
        }

        private static string GetDriverArguments(
            string jobsFilePath,
            string sessionId,
            string sdkVersion,
            BuildInstructions buildInstructions,
            bool isBaseline)
        {
            var argumentsBuilder = new StringBuilder($"{DriverPath} --server {ServerUrl} --client {ClientUrl} --jobs {jobsFilePath} --session {sessionId}");

            argumentsBuilder.Append(" --self-contained --aspNetCoreVersion Latest --runtimeVersion Latest --quiet");

            if (!string.IsNullOrWhiteSpace(buildInstructions.ScenarioName) && !string.Equals("Default", buildInstructions.ScenarioName, StringComparison.OrdinalIgnoreCase))
            {
                argumentsBuilder.Append(" --scenario ");
                argumentsBuilder.Append(buildInstructions.ScenarioName);
            }

            if (!string.IsNullOrWhiteSpace(sdkVersion))
            {
                argumentsBuilder.Append(" --sdk ");
                argumentsBuilder.Append(sdkVersion);
            }

            if (!string.IsNullOrWhiteSpace(buildInstructions.ExtraDriverArgs))
            {
                argumentsBuilder.Append(" ");
                argumentsBuilder.Append(buildInstructions.ExtraDriverArgs);
            }

            if (isBaseline)
            {
                argumentsBuilder.Append(" --save baseline --description Before");
            }
            else
            {
                argumentsBuilder.Append(" --diff baseline --description After");
            }

            return argumentsBuilder.ToString();
        }

        // REVIEW: What's the best way to share these DTOs in this repo?
        private class BuildInstructions
        {
            public string[] BuildCommands { get; set; }
            public string ExtraDriverArgs { get; set; }

            public int PullRequestNumber { get; set; }
            public string BaselineSHA { get; set; }
            public string PullRequestSHA { get; set; }

            public string ScenarioName { get; set; }
        }

        private class BenchmarkResult
        {
            public bool Success { get; set; }
            public string BaselineStdout { get; set; }
            public string BaselineStderr { get; set; }
            public string PullRequestStdout { get; set; }
            public string PullRequestStderr { get; set; }
        }
    }
}
