﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RunTests.Cache;
using Newtonsoft.Json.Linq;
using RestSharp;
using System.Collections.Immutable;

namespace RunTests
{
    internal sealed class Program
    {
        internal const int ExitSuccess = 0;
        internal const int ExitFailure = 1;

        internal static int Main(string[] args)
        {
            var options = Options.Parse(args);
            if (options == null)
            {
                Options.PrintUsage();
                return ExitFailure;
            }

            // Setup cancellation for ctrl-c key presses
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += delegate
            {
                cts.Cancel();
            };

            return RunCore(options, cts.Token).GetAwaiter().GetResult();
        }

        private static async Task<int> RunCore(Options options, CancellationToken cancellationToken)
        {
            if (!CheckAssemblyList(options))
            { 
                return ExitFailure;
            }

            var testExecutor = CreateTestExecutor(options);
            var testRunner = new TestRunner(options, testExecutor);
            var start = DateTime.Now;
            var assemblyInfoList = GetAssemblyList(options);

            Console.WriteLine($"Data Storage: {testExecutor.DataStorage.Name}");
            Console.WriteLine($"Running {options.Assemblies.Count()} test assemblies in {assemblyInfoList.Count} partitions");

            var result = await testRunner.RunAllAsync(assemblyInfoList, cancellationToken).ConfigureAwait(true);
            var elapsed = DateTime.Now - start;

            Console.WriteLine($"Test execution time: {elapsed}");

            Logger.Finish(Path.GetDirectoryName(options.Assemblies.FirstOrDefault() ?? ""));

            DisplayResults(options.Display, result.TestResults);

            if (CanUseWebStorage())
            {
                await SendRunStats(options, testExecutor.DataStorage, elapsed, result, assemblyInfoList.Count, cancellationToken).ConfigureAwait(true);
            }

            if (!result.Succeeded)
            {
                ConsoleUtil.WriteLine(ConsoleColor.Red, $"Test failures encountered");
                return ExitFailure;
            }

            Console.WriteLine($"All tests passed");
            return ExitSuccess;
        }

        /// <summary>
        /// Quick sanity check to look over the set of assemblies to make sure they are valid and something was 
        /// specified.
        /// </summary>
        private static bool CheckAssemblyList(Options options)
        {
            var anyMissing = false;
            foreach (var assemblyPath in options.Assemblies)
            {
                if (!File.Exists(assemblyPath))
                {
                    ConsoleUtil.WriteLine(ConsoleColor.Red, $"The file '{assemblyPath}' does not exist, is an invalid file name, or you do not have sufficient permissions to read the specified file.");
                    anyMissing = true;
                }
            }

            if (anyMissing)
            {
                return false;
            }

            if (options.Assemblies.Count == 0)
            {
                Console.WriteLine("No test assemblies specified.");
                return false;
            }

            return true;
        }

        private static List<AssemblyInfo> GetAssemblyList(Options options)
        {
            var scheduler = new AssemblyScheduler(options);
            var list = new List<AssemblyInfo>();

            foreach (var assemblyPath in options.Assemblies.OrderByDescending(x => new FileInfo(x).Length))
            {
                var name = Path.GetFileName(assemblyPath);

                // As a starting point we will just schedule the items we know to be a performance 
                // bottleneck.  Can adjust as we get real data.
                if (name == "Roslyn.Compilers.CSharp.Emit.UnitTests.dll" ||
                    name == "Roslyn.Services.Editor.UnitTests.dll" ||
                    name == "Roslyn.Services.Editor.UnitTests2.dll" ||
                    name == "Roslyn.VisualStudio.Services.UnitTests.dll" ||
                    name == "Roslyn.Services.Editor.CSharp.UnitTests.dll" ||
                    name == "Roslyn.Services.Editor.VisualBasic.UnitTests.dll")
                {
                    list.AddRange(scheduler.Schedule(assemblyPath));
                }
                else
                {
                    list.Add(scheduler.CreateAssemblyInfo(assemblyPath));
                }
            }

            return list;
        }

        private static void DisplayResults(Display display, ImmutableArray<TestResult> testResults)
        {
            foreach (var cur in testResults)
            {
                var open = false;
                switch (display)
                {
                    case Display.All:
                        open = true;
                        break;
                    case Display.None:
                        open = false;
                        break;
                    case Display.Succeeded:
                        open = cur.Succeeded;
                        break;
                    case Display.Failed:
                        open = !cur.Succeeded;
                        break;
                }

                if (open)
                {
                    ProcessRunner.OpenFile(cur.ResultsFilePath);
                }
            }
        }

        private static bool CanUseWebStorage()
        {
            // The web caching layer is still being worked on.  For now want to limit it to Roslyn developers
            // and Jenkins runs by default until we work on this a bit more.  Anyone reading this who wants
            // to try it out should feel free to opt into this. 
            return 
                StringComparer.OrdinalIgnoreCase.Equals("REDMOND", Environment.UserDomainName) || 
                Constants.IsJenkinsRun;
        }

        private static ITestExecutor CreateTestExecutor(Options options)
        {
            var testExecutionOptions = new TestExecutionOptions(
                xunitPath: options.XunitPath,
                trait: options.Trait,
                noTrait: options.NoTrait,
                useHtml: options.UseHtml,
                test64: options.Test64);
            var processTestExecutor = new ProcessTestExecutor(testExecutionOptions);
            if (!options.UseCachedResults)
            {
                return processTestExecutor;
            }

            // The web caching layer is still being worked on.  For now want to limit it to Roslyn developers
            // and Jenkins runs by default until we work on this a bit more.  Anyone reading this who wants
            // to try it out should feel free to opt into this. 
            IDataStorage dataStorage = new LocalDataStorage();
            if (CanUseWebStorage())
            {
                dataStorage = new WebDataStorage();
            }

            return new CachingTestExecutor(testExecutionOptions, processTestExecutor, dataStorage);
        }

        /// <summary>
        /// Order the assembly list so that the largest assemblies come first.  This
        /// is not ideal as the largest assembly does not necessarily take the most time.
        /// </summary>
        /// <param name="list"></param>
        private static IOrderedEnumerable<string> OrderAssemblyList(IEnumerable<string> list)
        {
            return list.OrderByDescending((assemblyName) => new FileInfo(assemblyName).Length);
        }

        private static async Task SendRunStats(Options options, IDataStorage dataStorage, TimeSpan elapsed, RunAllResult result, int partitionCount, CancellationToken cancellationToken)
        {
            var obj = new JObject();
            obj["Cache"] = dataStorage.Name;
            obj["ElapsedSeconds"] = (int)elapsed.TotalSeconds;
            obj["IsJenkins"] = Constants.IsJenkinsRun;
            obj["Is32Bit"] = !options.Test64;
            obj["AssemblyCount"] = options.Assemblies.Count;
            obj["CacheCount"] = result.CacheCount;
            obj["ChunkCount"] = partitionCount;
            obj["PartitionCount"] = partitionCount;
            obj["Succeeded"] = result.Succeeded;

            // During the transition from ellapsed to elapsed the client needs to provide both 
            // spellings for the server.
            obj["EllapsedSeconds"] = (int)elapsed.TotalSeconds;

            var request = new RestRequest("api/testrun", Method.POST);
            request.RequestFormat = DataFormat.Json;
            request.AddParameter("text/json", obj.ToString(), ParameterType.RequestBody);

            try
            {
                var client = new RestClient(Constants.DashboardUriString);
                var response = await client.ExecuteTaskAsync(request);
                if (response.StatusCode != System.Net.HttpStatusCode.NoContent)
                {
                    Logger.Log($"Unable to send results: {response.ErrorMessage}");
                }
            }
            catch
            {
                Logger.Log("Unable to send results");
            }
        }
    }
}