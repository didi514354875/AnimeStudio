using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AnimeStudio.CLI.Properties;
using Newtonsoft.Json;
using static AnimeStudio.CLI.Studio;

namespace AnimeStudio.CLI 
{
    public class Program
    {
        public static void Main(string[] args) => CommandLine.Init(args);

        public static void Run(Options o)
        {
            try
            {
                var game = GameManager.GetGame(o.GameName);

                // See https://github.com/Eleiyas/Z3-Asset-Map 
                var paths = File.Exists("./Maps/Z3-AssetIndex-Eleiyas.json")
                    ? JsonConvert.DeserializeObject<Dictionary<ulong, string>>(File.ReadAllText("./Maps/Z3-AssetIndex-Eleiyas.json"))
                    : new Dictionary<ulong, string>();

                Studio.Paths = paths;
                AssetsHelper.Paths = paths;

                if (game == null)
                {
                    Console.WriteLine("Invalid Game !!");
                    Console.WriteLine(GameManager.SupportedGames());
                    return;
                }

                if (game is UnityCNGame unityCNGame)
                {
                    UnityCN.SetKey(unityCNGame.Key);
                    Logger.Info($"[UnityCN] Selected Key is {unityCNGame.Key.Name} - {unityCNGame.Key.Key}");
                }

                Studio.Game = game;
                Logger.Default = new ConsoleLogger();
                Logger.Flags = o.LoggerFlags.Aggregate((e, x) => e |= x);
                Logger.FileLogging = Settings.Default.enableFileLogging;
                AssetsHelper.Minimal = Settings.Default.minimalAssetMap;
                AssetsHelper.SetUnityVersion(o.UnityVersion);

                TypeFlags.SetTypes(JsonConvert.DeserializeObject<Dictionary<ClassIDType, (bool, bool)>>(Settings.Default.types));

                var classTypeFilter = Array.Empty<ClassIDType>();
                if (!o.TypeFilter.IsNullOrEmpty())
                {
                    var exportTexture2D = false;
                    var exportMaterial = false;
                    var classTypeFilterList = new List<ClassIDType>();
                    for (int i = 0; i < o.TypeFilter.Length; i++)
                    {
                        foreach (var typeStrRaw in o.TypeFilter[i].Split(','))
                        {
                        var typeStr = typeStrRaw.Trim();
                        if (typeStr.Length == 0)
                        {
                            continue;
                        }
                        var type = ClassIDType.UnknownType;
                        var flag = TypeFlag.Both;

                        try
                        {
                            if (typeStr.Contains(':'))
                            {
                                var param = typeStr.Split(':');

                                flag = (TypeFlag)Enum.Parse(typeof(TypeFlag), param[1], true);

                                typeStr = param[0];
                            }

                            type = (ClassIDType)Enum.Parse(typeof(ClassIDType), typeStr, true);

                            if (type == ClassIDType.Texture2D)
                            {
                                exportTexture2D = flag.HasFlag(TypeFlag.Export);
                            }
                            else if (type == ClassIDType.Material)
                            {
                                exportMaterial = flag.HasFlag(TypeFlag.Export);
                            }
                    
                            TypeFlags.SetType(type, flag.HasFlag(TypeFlag.Parse), flag.HasFlag(TypeFlag.Export));

                            classTypeFilterList.Add(type);
                        }
                        catch(Exception e)
                        {
                            Logger.Error($"{typeStr} has invalid format, skipping...");
                            continue;
                        }
                        }
                    }

                    classTypeFilter = classTypeFilterList.ToArray();

                    if (ClassIDType.GameObject.CanExport() || ClassIDType.Animator.CanExport())
                    {
                        TypeFlags.SetType(ClassIDType.Texture2D, true, exportTexture2D);
                        if (Settings.Default.exportMaterials)
                        {
                            TypeFlags.SetType(ClassIDType.Material, true, exportMaterial);
                        }
                        if (ClassIDType.GameObject.CanExport())
                        {
                            TypeFlags.SetType(ClassIDType.Animator, true, false);
                        }
                        else if(ClassIDType.Animator.CanExport())
                        {
                            TypeFlags.SetType(ClassIDType.GameObject, true, false);
                        }
                    }
                }

                if (o.GroupAssetsType == AssetGroupOption.ByContainer)
                {
                    TypeFlags.SetType(ClassIDType.AssetBundle, true, false);
                }

                assetsManager.Silent = o.Silent;
                assetsManager.Game = game;
                assetsManager.SpecifyUnityVersion = o.UnityVersion;
                o.Output.Create();

                if (o.Key != default)
                {
                    MiHoYoBinData.Encrypted = true;
                    MiHoYoBinData.Key = o.Key;
                }

                if (o.AIFile != null && game.Type.IsGISubGroup())
                {
                    ResourceIndex.FromFile(o.AIFile.FullName);
                }

                if (o.DummyDllFolder != null)
                {
                    assemblyLoader.Load(o.DummyDllFolder.FullName);
                }

                Logger.Info("Scanning for files...");
                var files = o.Input.Attributes.HasFlag(FileAttributes.Directory) ? Directory.GetFiles(o.Input.FullName, "*.*", SearchOption.AllDirectories).OrderBy(x => x.Length).ToArray() : new string[] { o.Input.FullName };
                Logger.Info($"Found {files.Length} files");

                // ACL 探针：ANIMESTUDIO_ACL_PROBE=<out.json> 时只跑探针并退出
                var aclProbeOut = Environment.GetEnvironmentVariable("ANIMESTUDIO_ACL_PROBE");
                if (!string.IsNullOrEmpty(aclProbeOut))
                {
                    AclProbe.Run(files, aclProbeOut);
                    return;
                }

                if (o.MapOp.HasFlag(MapOpType.CABMap))
                {
                    // NOTE: this if/else was inverted upstream (CABMap alone loaded a map that
                    // was never built, and CABMap|Load rebuilt it every time).  Aligned with the
                    // AssetMap block below: Load bit => load the map, otherwise build it.
                    if (o.MapOp.HasFlag(MapOpType.Load))
                    {
                        AssetsHelper.LoadCABMapInternal(o.MapName);
                        assetsManager.ResolveDependencies = true;
                    }
                    else
                    {
                        AssetsHelper.BuildCABMap(files, o.MapName, o.Input.FullName, game);
                    }
                }
                if (o.MapOp.HasFlag(MapOpType.AssetMap))
                {
                    if (o.MapOp.HasFlag(MapOpType.Load))
                    {
                        files = AssetsHelper.ParseAssetMap(o.MapName, o.MapType, classTypeFilter, o.NameFilter, o.ContainerFilter);
                    }
                    else
                    {
                        Task.Run(() => AssetsHelper.BuildAssetMap(files, o.MapName, game, o.Output.FullName, o.MapType, classTypeFilter, o.NameFilter, o.ContainerFilter)).Wait();
                    }
                }
                if (o.MapOp.HasFlag(MapOpType.Both))
                {
                    Task.Run(() => AssetsHelper.BuildBoth(files, o.MapName, o.Input.FullName, game, o.Output.FullName, o.MapType, classTypeFilter, o.NameFilter, o.ContainerFilter)).Wait();
                }
                if (o.MapOp.Equals(MapOpType.None) || o.MapOp.HasFlag(MapOpType.Load))
                {
                    // ── 预扫：汇总所有 Avatar 的 m_TOS（hash → 骨骼全路径）。
                    // 逐文件导出时 clip 与 Avatar 常不在同一 bundle，局部 FindTOS 会拿不到路径，
                    // 曲线 path 会退化成 path_<hash>（Unity 无法绑定）。
                    if (Environment.GetEnvironmentVariable("ANIMESTUDIO_TOS_PREPASS") == "1")
                    {
                        Logger.Info("TOS pre-pass: collecting Avatar m_TOS...");
                        var pre = 0;
                        foreach (var pf in files)
                        {
                            assetsManager.LoadFiles(pf);
                            if (assetsManager.assetsFileList.Count > 0)
                            {
                                foreach (var af in assetsManager.assetsFileList)
                                {
                                    foreach (var obj in af.Objects)
                                    {
                                        if (obj is Avatar av)
                                        {
                                            AnimationClipExtensions.AddGlobalTOS(av);
                                        }
                                    }
                                }
                            }
                            assetsManager.Clear();
                            if (++pre % 500 == 0)
                            {
                                Logger.Info($"[tos-prepass] {pre}/{files.Length}  (paths={AnimationClipExtensions.GlobalTOS.Count})");
                            }
                        }
                        Logger.Info($"[tos-prepass] done: {AnimationClipExtensions.GlobalTOS.Count} bone paths");
                    }

                    var i = 0;

                    var path = Path.GetDirectoryName(Path.GetFullPath(files[0]));
                    ImportHelper.MergeSplitAssets(path);
                    var toReadFile = ImportHelper.ProcessingSplitFiles(files.ToList());

                    var fileList = new List<string>(toReadFile);
                    foreach (var file in fileList)
                    {
                        assetsManager.LoadFiles(file);
                        if (assetsManager.assetsFileList.Count > 0)
                        {
                            BuildAssetData(classTypeFilter, o.NameFilter, o.ContainerFilter, ref i);
                            ExportAssets(o.Output.FullName, exportableAssets, o.GroupAssetsType, o.AssetExportType);
                        }
                        exportableAssets.Clear();
                        assetsManager.Clear();
                    }
                }
                if (Properties.Settings.Default.scrapeMonos)
                {
                    File.WriteAllLines("./Maps/PathStrings_Sorted.txt", PathStrings.Distinct().OrderBy(p => p));
                    File.WriteAllLines("./Maps/VOStrings_Sorted.txt", VOStrings.Distinct().OrderBy(p => p));
                    File.WriteAllLines("./Maps/EventStrings_Sorted.txt", EventStrings.Distinct().OrderBy(p => p));
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }
    }
}