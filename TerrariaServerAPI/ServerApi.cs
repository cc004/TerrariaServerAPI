using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using GameLauncher;
using Newtonsoft.Json;
using Terraria;

namespace TerrariaApi.Server
{
    public static partial class ServerApi
    {
        public static class Program
        {
            public static void Main(string[] args)
            {
                AppDomain.CurrentDomain.UnhandledException += (_, a) => Console.WriteLine(a.ExceptionObject.ToString());
                var config = JsonConvert.DeserializeObject<ServerConfig>(File.ReadAllText(args[0]));
                ServerPluginsDirectoryPath = args[1];
                var otapi = typeof(Main).Assembly;
                var targs = config.CreateArgs(args[2]);
                var parent = Process.GetProcessById(int.Parse(args[4]));
                parent.EnableRaisingEvents = true;
                parent.Exited += (_, __) => Environment.Exit(1);

                TerrariaApi.Server.Program.InitialiseInternals();

                Hooks.AttachOTAPIHooks(targs);
                if (targs.Any(x => x == "-skipassemblyload"))
                {
                    Terraria.Main.SkipAssemblyLoad = true;
                }

                AppDomain.CurrentDomain.AssemblyResolve += (sender, sargs) =>
                {
                    var resourceName = new AssemblyName(sargs.Name).Name + ".dll";
                    var text = Array.Find(typeof(Main).Assembly.GetManifestResourceNames(),
                        element => element.EndsWith(resourceName));
                    if (text == null) return null;
                    Assembly result;
                    using (Stream manifestResourceStream = typeof(Main).Assembly.GetManifestResourceStream(text))
                    {
                        var array = new byte[manifestResourceStream.Length];
                        manifestResourceStream.Read(array, 0, array.Length);
                        result = Assembly.Load(array);
                    }

                    return result;
                };

                OTAPI.Hooks.Command.StartCommandThread = null;
                OTAPI.Hooks.Game.PreInitialize = () =>
                {
                    try
                    {
                        Console.WriteLine("TerrariaAPI Version: {0} (Protocol {1} ({2}), OTAPI {3})",
                            ApiVersion,
                            Terraria.Main.versionNumber2, 244, otapi.GetName()?.Version);
                        Initialize(args, Terraria.Main.instance);
                    }
                    catch (Exception ex)
                    {
                        var logWriter = LogWriter;
                        var str = "Startup aborted due to an exception in the Server API initialization:\n";
                        var ex2 = ex;
                        logWriter.ServerWriteLine(str + ((ex2 != null) ? ex2.ToString() : null), TraceLevel.Error);
                        Console.ReadLine();
                    }

                    Hooks.GameInitialize.Invoke(EventArgs.Empty);
                };

                void Initialize(string[] commandLineArgs, Main game)
                {
                    Profiler.BeginMeasureServerInitTime();
                    LogWriter.ServerWriteLine($"TerrariaApi - Server v{ApiVersion} started.",
                        TraceLevel.Verbose);
                    LogWriter.ServerWriteLine("\tCommand line: " + Environment.CommandLine,
                        TraceLevel.Verbose);
                    LogWriter.ServerWriteLine(
                        $"\tOS: {Environment.OSVersion} (64bit: {Environment.Is64BitOperatingSystem})",
                        TraceLevel.Verbose);
                    LogWriter.ServerWriteLine("\tMono: " + RunningMono.ToString(),
                        TraceLevel.Verbose);
                    ServerApi.game = game;
                    HandleCommandLine(commandLineArgs);

					AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;
                    Console.InputEncoding = Encoding.UTF8;
                    Console.OutputEncoding = Encoding.UTF8;
                    Console.SetIn(new TextWrapper(Console.In));
                    Console.ReadLine();
                    LoadPlugins();
                }

                void LoadPlugins()
                {
                    var list2 = config.plugins.Select(p => new FileInfo($"{Path.Combine(ServerPluginsDirectoryPath, p)}.dll"))
                        .ToList();
                    var dictionary = new Dictionary<TerrariaPlugin, Stopwatch>();
                    foreach (var fileInfo in list2)
                    {
                        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileInfo.Name);
                        try
                        {
                            Assembly assembly;
                            if (!loadedAssemblies.TryGetValue(fileNameWithoutExtension, out assembly))
                            {
                                try
                                {
                                    assembly = Assembly.LoadFrom(fileInfo.FullName);
                                }
                                catch (BadImageFormatException e)
                                {
                                    LogWriter.ServerWriteLine($"failed to load plugin {e}", TraceLevel.Error);
                                }

                                loadedAssemblies.Add(fileNameWithoutExtension, assembly);
                            }

                            if (InvalidateAssembly(assembly, fileInfo.Name))
                            {
                                foreach (var type in assembly.GetExportedTypes())
                                {
                                    if (type.IsSubclassOf(typeof(TerrariaPlugin)) && type.IsPublic && !type.IsAbstract)
                                    {
                                        var customAttributes =
                                            type.GetCustomAttributes(typeof(ApiVersionAttribute), false);
                                        if (customAttributes.Length != 0)
                                        {
                                            if (!IgnoreVersion)
                                            {
                                                var apiVersion = ((ApiVersionAttribute) customAttributes[0]).ApiVersion;
                                                if (apiVersion.Major != ApiVersion.Major ||
                                                    apiVersion.Minor != ApiVersion.Minor)
                                                {
                                                    LogWriter.ServerWriteLine(
                                                        string.Format(
                                                            "Plugin \"{0}\" is designed for a different Server API version ({1}) and was ignored.",
                                                            type.FullName, apiVersion.ToString(2)), TraceLevel.Warning);
                                                    goto IL_28B;
                                                }
                                            }

                                            TerrariaPlugin terrariaPlugin;
                                            try
                                            {
                                                var stopwatch = new Stopwatch();
                                                stopwatch.Start();
                                                terrariaPlugin =
                                                    (TerrariaPlugin) Activator.CreateInstance(type, game);
                                                stopwatch.Stop();
                                                dictionary.Add(terrariaPlugin, stopwatch);
                                            }
                                            catch (Exception innerException)
                                            {
                                                throw new InvalidOperationException(
                                                    string.Format(
                                                        "Could not create an instance of plugin class \"{0}\".",
                                                        type.FullName), innerException);
                                            }

                                            plugins.Add(new PluginContainer(terrariaPlugin));
                                        }
                                    }

                                    IL_28B: ;
                                }
                            }
                        }
                        catch (Exception innerException2)
                        {
                            throw new InvalidOperationException(
                                string.Format("Failed to load assembly \"{0}\".", fileInfo.Name), innerException2);
                        }
                    }

                    foreach (var pluginContainer in from x in Plugins
                             orderby x.Plugin.Order, x.Plugin.Name
                             select x)
                    {
                        var stopwatch2 = dictionary[pluginContainer.Plugin];
                        stopwatch2.Start();
                        try
                        {
                            pluginContainer.Initialize();
                        }
                        catch (Exception innerException3)
                        {
                            throw new InvalidOperationException(
                                string.Format("Plugin \"{0}\" has thrown an exception during initialization.",
                                    pluginContainer.Plugin.Name), innerException3);
                        }

                        stopwatch2.Stop();
                        LogWriter.ServerWriteLine(
                            string.Format("Plugin {0} v{1} (by {2}) initiated.", pluginContainer.Plugin.Name,
                                pluginContainer.Plugin.Version, pluginContainer.Plugin.Author), TraceLevel.Info);
                    }

                    if (Profiler.WrappedProfiler != null)
                    {
                        foreach (var keyValuePair in dictionary)
                        {
                            var key = keyValuePair.Key;
                            var value = keyValuePair.Value;
                            Profiler.InputPluginInitTime(key, value.Elapsed);
                        }
                    }
                }

                WindowsLaunch.Main(targs);
            }
        }
    }
}
