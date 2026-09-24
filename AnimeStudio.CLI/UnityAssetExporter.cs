using AnimeStudio;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AnimeStudio.CLI
{
    /// <summary>
    /// Exports assets in Unity-native asset formats (.mat, .asset, .anim, .prefab)
    /// plus one .meta file per exported asset, and wires the cross references
    /// (material → shader/textures, prefab → meshes/materials/animations) through
    /// deterministic GUIDs so the output folder can be dropped into a Unity
    /// project with the references intact.
    ///
    /// References are only written when the target was (or gets) exported as well;
    /// anything unresolvable stays {fileID: 0}.
    /// </summary>
    internal sealed class UnityAssetExporter
    {
        private class Entry
        {
            public string Guid;
            public long FileID;
        }

        private readonly Dictionary<(SerializedFile, long), Entry> entries = new();
        private readonly HashSet<(SerializedFile, long)> inProgress = new();
        private readonly HashSet<(SerializedFile, long)> failed = new();
        private string savePath;

        /// <summary>Deterministic GUID so re-runs keep the same references.</summary>
        private static string GuidFor(SerializedFile file, long pathID)
        {
            var seed = $"{file.originalPath}|{pathID}";
            using var md5 = MD5.Create();
            return ToGuidString(md5.ComputeHash(Encoding.UTF8.GetBytes(seed)));
        }

        private static string ToGuidString(byte[] hash)
        {
            var sb = new StringBuilder(32);
            foreach (var b in hash)
            {
                sb.Append(b.ToString("x2"));
            }
            return sb.ToString(0, 32);
        }

        private static string SanitizeName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
            {
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            }
            var result = sb.ToString();
            if (result.Length == 0)
            {
                return "unnamed";
            }
            return result.Length > 100 ? result.Substring(0, 100) : result;
        }

        private static string ExtensionOf(ClassIDType type)
        {
            return type switch
            {
                ClassIDType.GameObject or ClassIDType.Animator => ".prefab",
                ClassIDType.Material => ".mat",
                ClassIDType.Mesh => ".asset",
                ClassIDType.AnimationClip => ".anim",
                ClassIDType.Texture2D => ".png",
                ClassIDType.Sprite => ".png",
                ClassIDType.Shader => ".shader",
                ClassIDType.AudioClip => ".fsb",
                // Not a Unity-importable asset: the bundle holds only the baked runtime
                // controller, so this is a transcription with hashes resolved to names.
                ClassIDType.AnimatorController or ClassIDType.RuntimeAnimatorController => ".controller.json",
                _ => ".asset",
            };
        }

        private static string FolderOf(ClassIDType type)
        {
            return type switch
            {
                ClassIDType.GameObject or ClassIDType.Animator => "Prefab",
                ClassIDType.Material => "Material",
                ClassIDType.Mesh => "Mesh",
                ClassIDType.AnimationClip => "AnimationClip",
                ClassIDType.Texture2D => "Texture2D",
                ClassIDType.Sprite => "Sprite",
                ClassIDType.Shader => "Shader",
                ClassIDType.AudioClip => "AudioClip",
                ClassIDType.AnimatorController or ClassIDType.RuntimeAnimatorController => "AnimatorController",
                _ => type.ToString(),
            };
        }

        /// <summary>Main entry: export every asset in Unity format with meta files.</summary>
        public static int Export(string savePath, List<AssetItem> assets)
        {
            var exporter = new UnityAssetExporter { savePath = savePath };
            var count = 0;
            foreach (var asset in assets)
            {
                try
                {
                    if (exporter.ExportAsset(asset))
                    {
                        count++;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"UnityAssetExport {asset.Type}:{asset.Text} error\r\n{ex.Message}\r\n{ex.StackTrace}");
                }
            }
            return count;
        }

        private Entry Register(Object asset)
        {
            var key = (asset.assetsFile, asset.m_PathID);
            if (!entries.TryGetValue(key, out var entry))
            {
                entry = new Entry
                {
                    Guid = GuidFor(key.Item1, key.Item2),
                    FileID = key.Item2,
                };
                entries[key] = entry;
            }
            return entry;
        }

        /// <summary>
        /// Export one asset (unless already exported).  Assets referenced by the
        /// exported one are pulled in automatically so the references resolve.
        /// </summary>
        private bool ExportAsset(AssetItem item)
        {
            var asset = item.Asset;
            var key = (asset.assetsFile, asset.m_PathID);
            if (failed.Contains(key) || inProgress.Contains(key))
            {
                return false;
            }

            var folder = Path.Combine(savePath, FolderOf(item.Type));
            Directory.CreateDirectory(folder);

            Register(asset);
            inProgress.Add(key);

            bool ok;
            string text = null;
            try
            {
                switch (item.Type)
                {
                    case ClassIDType.Material:
                        text = MaterialToYaml((Material)asset);
                        break;
                    // ★ Avatar 之前没有分支 ⇒ 落到「原样写二进制」，Unity 完全不认。
                    //   endfield 的 prefab 里 Animator.m_Avatar 是**真实 PPtr**，
                    //   所以这里必须产出真正的 Unity `!u!90 Avatar` 资源。
                    case ClassIDType.Avatar:
                        text = AvatarToYaml((Avatar)asset);
                        break;
                    case ClassIDType.Mesh:
                        text = MeshToYaml((Mesh)asset);
                        break;
                    case ClassIDType.AnimationClip:
                        text = ((AnimationClip)asset).Convert();
                        break;
                    case ClassIDType.GameObject:
                    case ClassIDType.Animator:
                        text = PrefabToYaml(item);
                        break;
                    case ClassIDType.Shader:
                        // 默认只导 Properties + 占位实现；ANIMESTUDIO_SHADER_FULL=1 恢复旧行为
                        text = ShaderConverter.PlaceholderOnly
                            ? ((Shader)asset).ConvertPlaceholder()
                            : ((Shader)asset).Convert();
                        break;
                    case ClassIDType.AnimatorController:
                    case ClassIDType.RuntimeAnimatorController:
                        text = AnimatorControllerExporter.ToJson((AnimatorController)asset, ClipReference);
                        break;
                }

                if (text != null)
                {
                    var baseName = SanitizeName(item.Text);
                    var path = Path.Combine(folder, baseName + ExtensionOf(item.Type));
                    for (var i = 1; File.Exists(path); i++)
                    {
                        path = Path.Combine(folder, $"{baseName} ({i}){ExtensionOf(item.Type)}");
                    }
                    File.WriteAllText(path, text);
                    WriteMeta(path, entries[key].Guid);
                    // 若开了 ANIMESTUDIO_FBX=1，顺带把同一个 GameObject 导成 .fbx
                    // （Unity 只能从「带骨骼 + 蒙皮的模型文件」自动生成 Avatar；OBJ 不行）
                    if (Environment.GetEnvironmentVariable("ANIMESTUDIO_FBX") == "1"
                        && asset is GameObject fbxGo)
                    {
                        // 只对「层级里真有 SkinnedMeshRenderer」的根导 FBX ——
                        // 否则每个子 GameObject 都会产一个碎片文件。
                        if (HasSkinnedMesh(fbxGo))
                        {
                            try { WriteGameObjectFbx(fbxGo, Path.ChangeExtension(path, ".fbx")); }
                            catch (Exception ex) { Logger.Warning("FBX 导出失败 " + SanitizeName(item.Text) + ": " + ex.Message); }
                        }
                    }
                    ok = true;
                }
                else
                {
                    ok = ExportBinaryWithMeta(item, folder, entries[key].Guid);
                }
            }
            finally
            {
                inProgress.Remove(key);
            }
            return ok;
        }

        /// <summary>
        /// Texture/sprite/audio go through the regular exporters, which pick their
        /// own file names; find what they produced by diffing the folder and attach
        /// the meta to every new file.
        /// </summary>
        private bool ExportBinaryWithMeta(AssetItem item, string folder, string guid)
        {
            var before = new HashSet<string>(Directory.GetFiles(folder));
            var ok = item.Type switch
            {
                ClassIDType.Texture2D => Exporter.ExportTexture2D(item, folder + Path.DirectorySeparatorChar),
                ClassIDType.Sprite => Exporter.ExportSprite(item, folder + Path.DirectorySeparatorChar),
                ClassIDType.AudioClip => Exporter.ExportAudioClip(item, folder + Path.DirectorySeparatorChar),
                _ => false,
            };
            if (ok)
            {
                foreach (var file in Directory.GetFiles(folder))
                {
                    if (!before.Contains(file) && !file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                    {
                        WriteMeta(file, guid);
                    }
                }
            }
            return ok;
        }

        /// <summary>
        /// 写 `.meta`。★ 必须带 **importer 块 + mainObjectFileID**，只有 guid 是不够的：
        /// Unity 靠 mainObjectFileID 认定「文件里的主对象是哪个」，与 prefab 里
        /// `{fileID: 4300000, guid: ...}` 的 fileID 对应；缺了它引用会解析成 null。
        /// 形制对齐 Unity 自带的 `Capsule.controller.meta`。
        /// </summary>
        private static void WriteMeta(string path, string guid)
        {
            File.WriteAllText(path + ".meta", MetaText(path, guid));
        }

        private static string MetaText(string path, string guid)
        {
            var known = new Dictionary<string, int>
            {
                { ".mat", 2100000 }, { ".asset", 0 }, { ".anim", 7400000 },
                { ".controller", 9100000 }, { ".prefab", 0 },
            };
            string ext = Path.GetExtension(path).ToLowerInvariant();
            int mainId = 0;
            if (ext == ".asset")
            {
                // .asset 里可能是 Mesh / Avatar / 其它，按类 ID 区分
                mainId = path.IndexOf("\\Avatar\\", StringComparison.OrdinalIgnoreCase) >= 0
                      || Path.GetFileName(path).EndsWith("Avatar.asset")
                    ? 9000000 : 4300000;
            }
            else known.TryGetValue(ext, out mainId);

            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".shader")
            {
                // ★★ 必须带 **importer 块**！只写 guid 会让 Unity **导入失败**：
                //    batchmode 实测 `LoadAssetAtPath<Texture2D>(png)` 返回 null、
                //    Editor.log 报 `Unknown error occurred while loading '...png'`
                //    ⇒ 材质里 _BaseMap 等贴图引用全部为空。
                //    A/B 实验：同一张 PNG 去掉 .meta 后能正常导入 ⇒ 问题在 meta，不在 PNG 字节。
                //    importer 字段太多，直接取 Unity 自己写的模板（tools/meta_template_*.txt）。
                var tplDir = Environment.GetEnvironmentVariable("CHAREXPORT_META_TEMPLATES");
                string tpl = null;
                if (!string.IsNullOrEmpty(tplDir))
                {
                    var fp = Path.Combine(tplDir, ext == ".shader" ? "meta_template_shader.txt" : "meta_template_png.txt");
                    if (File.Exists(fp))
                    {
                        var lines = File.ReadAllText(fp).Replace("\r\n", "\n")
                                        .Split('\n')
                                        .Where(l => !l.StartsWith("fileFormatVersion:"))
                                        .ToArray();
                        tpl = string.Join("\n", lines).TrimStart('\n');
                    }
                }
                return tpl != null
                    ? "fileFormatVersion: 2\nguid: " + guid + "\n" + tpl
                    : "fileFormatVersion: 2\nguid: " + guid + "\n";
            }
            if (mainId == 0) return "fileFormatVersion: 2\nguid: " + guid + "\n";

            return "fileFormatVersion: 2\nguid: " + guid
                 + "\nNativeFormatImporter:\n  externalObjects: {}\n  mainObjectFileID: "
                 + mainId + "\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n";
        }

        // ------------------------------------------------------------------
        //  reference resolution
        // ------------------------------------------------------------------

        private static YAMLMappingNode NullReference()
        {
            var node = new YAMLMappingNode(MappingStyle.Flow);
            node.Add("fileID", 0);
            return node;
        }

        private YAMLMappingNode ObjectReference(Object target)
        {
            if (target == null)
            {
                return NullReference();
            }

            var key = (target.assetsFile, target.m_PathID);
            if (!entries.TryGetValue(key, out var entry))
            {
                // pull the referenced asset in so the GUID target exists
                if (!inProgress.Contains(key) && !failed.Contains(key) && Pullable(target))
                {
                    ExportAsset(new AssetItem(target));
                }
                if (!entries.TryGetValue(key, out entry))
                {
                    return NullReference();
                }
            }

            // ★★ Unity 的外部引用**两个字段都有规范值**（由本工程里 Unity 自己生成的资源实测统计得出）：
            //   ① `fileID` 必须是**该类资产的规范主对象 ID**，不是包内 pathID。
            //      写成 pathID 时 Unity 会去目标资产文件里找一个不存在的 localID ⇒ **引用悬空**
            //      （表现：材质的贴图空、prefab 的 m_Mesh 空）。
            //   ② `type`：2 = 原生序列化资产（.mat/.asset/.anim/.controller），
            //             3 = 由导入器产出的资产（.png/.shader/.cs/.fbx 子对象）以及 prefab 内对象。
            //   实测搭配：Material 2100000/2 · Mesh 4300000/2 · AnimationClip 7400000/2 ·
            //             AnimatorController 9100000/2 · Texture2D 2800000/3 ·
            //             Sprite 21300000/3 · Shader 4800000/3 · MonoScript 11500000/3
            string typeName = target.GetType().Name;
            long fileID;
            int refType;
            switch (typeName)
            {
                case "Material": fileID = 2100000; refType = 2; break;
                case "Mesh": fileID = 4300000; refType = 2; break;
                case "AnimationClip": fileID = 7400000; refType = 2; break;
                case "AnimatorController":
                case "RuntimeAnimatorController": fileID = 9100000; refType = 2; break;
                case "Avatar": fileID = 9000000; refType = 2; break;
                case "AudioClip": fileID = 8300000; refType = 3; break;
                case "Sprite": fileID = 21300000; refType = 3; break;
                case "Shader": fileID = 4800000; refType = 3; break;
                case "Texture2D":
                case "Texture":
                case "Cubemap": fileID = 2800000; refType = 3; break;
                case "TextAsset": fileID = 4900000; refType = 3; break;
                default: fileID = entry.FileID; refType = 3; break;
            }
            var node = new YAMLMappingNode(MappingStyle.Flow);
            node.Add("fileID", fileID);
            node.Add("guid", entry.Guid);
            node.Add("type", refType);
            return node;
        }

        private static bool Pullable(Object target)
        {
            // ★ Avatar/AnimatorController 必须在这里，否则外部引用解析失败会**静默写成 {fileID: 0}**
            //   （实测：prefab 的 Animator.m_Avatar 因此一直是 0，而 Avatar 本体却是导出成功的）。
            return target is Material or Mesh or Texture2D or Sprite or Shader or AnimationClip or AudioClip
                   or AnimatorController or Avatar;
        }

        /// <summary>
        /// JSON-shaped reference to an AnimationClip, pulling the clip into the export so the
        /// GUID in the controller dump actually resolves to an exported <c>.anim</c>.
        /// </summary>
        private IDictionary<string, object> ClipReference(AnimationClip clip)
        {
            var key = (clip.assetsFile, clip.m_PathID);
            if (!entries.TryGetValue(key, out var entry))
            {
                if (!inProgress.Contains(key) && !failed.Contains(key))
                {
                    ExportAsset(new AssetItem(clip));
                }
                if (!entries.TryGetValue(key, out entry))
                {
                    return null;
                }
            }
            return new Dictionary<string, object>
            {
                ["fileID"] = 7400000,
                ["guid"] = entry.Guid,
                ["type"] = 2,
            };
        }

        private YAMLMappingNode Reference<T>(PPtr<T> pptr) where T : Object
        {
            return pptr != null && pptr.TryGet(out var target) ? ObjectReference(target) : NullReference();
        }

        private static void WriteMetaIfNew(Dictionary<string, bool> before, string file, string guid)
        {
            throw new NotImplementedException();
        }

        // ------------------------------------------------------------------
        //  YAML plumbing
        // ------------------------------------------------------------------

        private static string WriteYaml(IReadOnlyList<YAMLDocument> docs)
        {
            var writer = new YAMLWriter();
            foreach (var doc in docs)
            {
                writer.AddDocument(doc);
            }
            using var sw = new StringWriter();
            writer.Write(sw);
            // ★★ TAG 命名空间必须照团结引擎：它自己写的 asset 全是
            //   `%TAG !u! tag:yousandi.cn,2023:`；YamlDotNet 默认写上游的 `tag:unity3d.com,2011`。
            //   证据：Assets/ChenImport/Anim/Models/ArmatureAvatar.asset（Unity 从 FBX 自动生成的 avatar）。
            return sw.ToString().Replace("tag:unity3d.com,2011", "tag:yousandi.cn,2023");
        }

        private static string WriteYaml(YAMLDocument doc)
        {
            return WriteYaml(new[] { doc });
        }

        private static YAMLMappingNode Vector3Node(Vector3 v)
        {
            var node = new YAMLMappingNode(MappingStyle.Flow);
            node.Add("x", v.X);
            node.Add("y", v.Y);
            node.Add("z", v.Z);
            return node;
        }

        private static YAMLMappingNode Vector2Node(float x, float y)
        {
            var node = new YAMLMappingNode(MappingStyle.Flow);
            node.Add("x", x);
            node.Add("y", y);
            return node;
        }

        private static YAMLMappingNode Vector4Node(float x, float y, float z, float w)
        {
            var node = new YAMLMappingNode(MappingStyle.Flow);
            node.Add("x", x);
            node.Add("y", y);
            node.Add("z", z);
            node.Add("w", w);
            return node;
        }

        private static YAMLSequenceNode FloatSequence(IEnumerable<float> values)
        {
            var seq = new YAMLSequenceNode();
            foreach (var v in values)
            {
                seq.Add(v);
            }
            return seq;
        }

        private static string Hex(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return string.Empty;
            }
            var sb = new StringBuilder(data.Length * 2);
            foreach (var b in data)
            {
                sb.Append(b.ToString("x2"));
            }
            return sb.ToString();
        }

        private static string HexIndices(uint[] indices, bool sixteenBit)
        {
            var sb = new StringBuilder();
            if (sixteenBit)
            {
                foreach (var i in indices)
                {
                    ushort v = (ushort)i;
                    sb.Append((v & 0xFF).ToString("x2")).Append((v >> 8).ToString("x2"));
                }
            }
            else
            {
                foreach (var i in indices)
                {
                    sb.Append((i & 0xFF).ToString("x2"));
                    sb.Append(((i >> 8) & 0xFF).ToString("x2"));
                    sb.Append(((i >> 16) & 0xFF).ToString("x2"));
                    sb.Append(((i >> 24) & 0xFF).ToString("x2"));
                }
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        //  Avatar (.asset)  —— Unity `!u!90`
        // ------------------------------------------------------------------

        private static string HexBytes(byte[] b)
        {
            if (b == null || b.Length == 0) return string.Empty;
            var sb = new StringBuilder(b.Length * 2);
            foreach (var x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>
        /// 把 Avatar 写成 Unity 能导入的 `!u!90 Avatar`。
        /// `m_Avatar` 是 AvatarConstant 的**原始字节的十六进制串**（长度 = m_AvatarSize×2），
        /// 与 `_typelessdata` 同理；m_HumanDescription 结构化写出（HumanBone/SkeletonBone）。
        /// </summary>
        private string AvatarToYaml(Avatar avatar)
        {
            var doc = new YAMLDocument();
            var root = doc.CreateMappingRoot();
            root.Tag = "90";
            root.Anchor = "9000000";      // Avatar 主对象 ID（外部引用写 9000000，必须一致）

            var a = new YAMLMappingNode();
            root.Add("Avatar", a);
            a.Add("m_ObjectHideFlags", 0);
            a.Add("m_CorrespondingSourceObject", NullReference());
            a.Add("m_PrefabInstance", NullReference());
            a.Add("m_PrefabAsset", NullReference());
            a.Add("m_Name", avatar.m_Name ?? string.Empty);

            var raw = avatar.m_AvatarRaw ?? Array.Empty<byte>();
            a.Add("m_AvatarSize", raw.Length);
            a.Add("m_Avatar", HexBytes(raw));

            // ★★ m_TOS：`hash -> 完整层级路径` 的映射表（本项目 441 条）。
            //   团结引擎自己写的 Avatar asset 在 `m_Avatar` 与 `m_HumanDescription` **之间**有这一段：
            //       m_TOS:
            //         3630723680: Root/Bip001/Bip001_Pelvis/Bip001_Spine/...
            //   外部权威证据：`Assets/ChenImport/Anim/Models/ArmatureAvatar.asset`
            //   （Unity 从 FBX 自动生成的真·人形 avatar，`%TAG !u! tag:yousandi.cn,2023`）。
            //   ★ 旧记录里早就记过「`AvatarToYaml()` 丢 `m_TOS`」，但一直没补；
            //     而 Unity 需要它把 humanoid 映射落到骨架节点上 ⇒ 缺它很可能直接导致 `isHuman=false`。
            if (avatar.m_TOS != null && avatar.m_TOS.Count > 0)
            {
                var tos = new YAMLMappingNode();
                a.Add("m_TOS", tos);
                foreach (var kv in avatar.m_TOS)
                    tos.Add(kv.Key.ToString(), kv.Value ?? string.Empty);
            }

            var hd = avatar.m_HumanDescription;
            var node = new YAMLMappingNode();
            a.Add("m_HumanDescription", node);
            // ★★ 2026-09-23 修：**必须写 serializedVersion 3 + `m_` 前缀字段名**。
            //   外部权威模板 = 团结引擎自己 `AvatarBuilder.BuildHumanAvatar` 后落盘的 asset
            //   （`SK_actor_chen_01Avatar_autogen.asset`，`%TAG !u! tag:yousandi.cn,2023`）：
            //       serializedVersion: 3
            //       - m_BoneName: Bip001
            //         m_HumanName: Hips
            //         m_Limit: {m_Min,m_Max,m_Value,m_Length,m_Modified}
            //       - m_Name: ... / m_ParentName: ... / m_Position / m_Rotation / m_Scale
            //   旧写法（v2 + 无前缀 `boneName`）Unity **读得到条数但取默认值** ⇒
            //   Editor 实测 `human=22 (空 boneName 22)` ⇒ `isHuman=false`
            //   ⇒ 肌肉曲线不驱动骨架 ⇒ 症状「只有尾巴/后发/衣服在动、Bip 主链不动」。
            node.Add("serializedVersion", 3);

            var humans = new YAMLSequenceNode();
            node.Add("m_Human", humans);
            foreach (var h in hd?.m_Human ?? new List<HumanBone>())
            {
                var m = new YAMLMappingNode();
                m.Add("m_BoneName", h.m_BoneName ?? string.Empty);
                m.Add("m_HumanName", h.m_HumanName ?? string.Empty);
                var lim = new YAMLMappingNode();
                m.Add("m_Limit", lim);
                lim.Add("m_Min", Vec3(h.m_Limit?.m_Min));
                lim.Add("m_Max", Vec3(h.m_Limit?.m_Max));
                lim.Add("m_Value", Vec3(h.m_Limit?.m_Value));
                lim.Add("m_Length", h.m_Limit?.m_Length ?? 0f);
                lim.Add("m_Modified", (h.m_Limit?.m_Modified ?? false) ? 1 : 0);
                humans.Add(m);
            }

            var skels = new YAMLSequenceNode();
            node.Add("m_Skeleton", skels);
            foreach (var b in hd?.m_Skeleton ?? new List<SkeletonBone>())
            {
                var m = new YAMLMappingNode();
                m.Add("m_Name", b.m_Name ?? string.Empty);
                // Unity 自己写时空值也照写（`m_ParentName: `），这里保持同形
                m.Add("m_ParentName", b.m_ParentName ?? string.Empty);
                m.Add("m_Position", Vec3(b.m_Position));
                m.Add("m_Rotation", Quat(b.m_Rotation));
                m.Add("m_Scale", Vec3(b.m_Scale));
                skels.Add(m);
            }

            node.Add("m_ArmTwist", hd?.m_ArmTwist ?? 0.5f);
            node.Add("m_ForeArmTwist", hd?.m_ForeArmTwist ?? 0.5f);
            node.Add("m_UpperLegTwist", hd?.m_UpperLegTwist ?? 0.5f);
            node.Add("m_LegTwist", hd?.m_LegTwist ?? 0.5f);
            node.Add("m_ArmStretch", hd?.m_ArmStretch ?? 0.05f);
            node.Add("m_LegStretch", hd?.m_LegStretch ?? 0.05f);
            node.Add("m_FeetSpacing", hd?.m_FeetSpacing ?? 0f);
            node.Add("m_GlobalScale", hd?.m_GlobalScale ?? 1f);
            node.Add("m_RootMotionBoneName", hd?.m_RootMotionBoneName ?? string.Empty);
            node.Add("m_DefaultPoseIndex", -1);          // ★ Unity 写了这条，v3 结构的一部分
            node.Add("m_HasTranslationDoF", (hd?.m_HasTranslationDoF ?? false) ? 1 : 0);
            node.Add("m_HasExtraRoot", (hd?.m_HasExtraRoot ?? false) ? 1 : 0);
            node.Add("m_SkeletonHasParents", (hd?.m_SkeletonHasParents ?? false) ? 1 : 0);

            return WriteYaml(doc);
        }

        private static YAMLMappingNode Vec3(Vector3? v)
        {
            var x = v ?? default;
            var n = new YAMLMappingNode(MappingStyle.Flow);
            n.Add("x", x.X); n.Add("y", x.Y); n.Add("z", x.Z);
            return n;
        }

        private static YAMLMappingNode Quat(Quaternion? q)
        {
            var x = q ?? default;
            var n = new YAMLMappingNode(MappingStyle.Flow);
            n.Add("x", x.X); n.Add("y", x.Y); n.Add("z", x.Z); n.Add("w", x.W);
            return n;
        }

        // ------------------------------------------------------------------
        //  Material (.mat)
        // ------------------------------------------------------------------

        private string MaterialToYaml(Material material)
        {
            var doc = new YAMLDocument();
            var root = doc.CreateMappingRoot();
            root.Tag = "21";
            root.Anchor = "2100000";      // Material 主对象 ID

            var mat = new YAMLMappingNode();
            root.Add("Material", mat);
            mat.Add("m_ObjectHideFlags", 0);
            mat.Add("m_CorrespondingSourceObject", NullReference());
            mat.Add("m_PrefabInstance", NullReference());
            mat.Add("m_PrefabAsset", NullReference());
            mat.Add("m_Name", SanitizeName(material.m_Name));
            mat.Add("m_Shader", Reference(material.m_Shader));

            var valid = new YAMLSequenceNode();
            if (material.m_ValidKeywords != null)
            {
                foreach (var kw in material.m_ValidKeywords)
                {
                    valid.Add(kw);
                }
            }
            mat.Add("m_ValidKeywords", valid);
            mat.Add("m_InvalidKeywords", new YAMLSequenceNode());
            mat.Add("m_LightmapFlags", material.m_LightmapFlags);
            mat.Add("m_EnableInstancingVariants", 0);
            mat.Add("m_DoubleSidedGI", 0);
            mat.Add("m_CustomRenderQueue", material.m_CustomRenderQueue);
            mat.Add("stringTagMap", new YAMLMappingNode());

            var disabled = new YAMLSequenceNode();
            if (material.m_DisabledShaderPasses != null)
            {
                foreach (var pass in material.m_DisabledShaderPasses)
                {
                    disabled.Add(pass);
                }
            }
            mat.Add("disabledShaderPasses", disabled);

            var saved = new YAMLMappingNode();
            saved.Add("serializedVersion", 3);
            mat.Add("m_SavedProperties", saved);

            var texEnvs = new YAMLSequenceNode();
            foreach (var kv in material.m_SavedProperties.m_TexEnvs ?? new List<KeyValuePair<string, UnityTexEnv>>())
            {
                var envEntry = new YAMLMappingNode();
                texEnvs.Add(envEntry);

                var env = new YAMLMappingNode();
                envEntry.Add(kv.Key, env);
                env.Add("m_Texture", Reference(kv.Value.m_Texture));
                env.Add("m_Scale", Vector2Node(kv.Value.m_Scale.X, kv.Value.m_Scale.Y));
                env.Add("m_Offset", Vector2Node(kv.Value.m_Offset.X, kv.Value.m_Offset.Y));
            }
            saved.Add("m_TexEnvs", texEnvs);

            var ints = new YAMLSequenceNode();
            foreach (var kv in material.m_SavedProperties.m_Ints ?? new List<KeyValuePair<string, int>>())
            {
                var entryNode = new YAMLMappingNode();
                ints.Add(entryNode);
                entryNode.Add(kv.Key, kv.Value);
            }
            saved.Add("m_Ints", ints);

            var floats = new YAMLSequenceNode();
            foreach (var kv in material.m_SavedProperties.m_Floats ?? new List<KeyValuePair<string, float>>())
            {
                var entryNode = new YAMLMappingNode();
                floats.Add(entryNode);
                entryNode.Add(kv.Key, kv.Value);
            }
            saved.Add("m_Floats", floats);

            var colors = new YAMLSequenceNode();
            foreach (var kv in material.m_SavedProperties.m_Colors ?? new List<KeyValuePair<string, Color>>())
            {
                var entryNode = new YAMLMappingNode();
                colors.Add(entryNode);
                entryNode.Add(kv.Key, Vector4Node(kv.Value.R, kv.Value.G, kv.Value.B, kv.Value.A));
            }
            saved.Add("m_Colors", colors);

            return WriteYaml(doc);
        }

        // ------------------------------------------------------------------
        //  Mesh (.asset)
        // ------------------------------------------------------------------


        /// <summary>
        /// 把「打包法线/切线」通道归一化成 Unity 能读的标准布局。
        ///
        /// 源数据里 ch1（= Unity 的 Normal）常被声明成 `format 0 (Float32), dimension 1` —— 4 字节。
        /// Unity 不认这种组合（Normal 必须 3 维）⇒ **该 mesh 导入失败** ⇒ prefab 里显示“没有 mesh 引用”。
        /// 那 4 字节其实是**打包的 uint32**，解码依据是反编译的顶点着色器
        /// `script/shader_glsl/HGRP_CharacterNPR_Skin.shader`：
        ///   bit30 = 1 表示已压缩
        ///   bits  0..9  = 八面体法线 u（10 位有符号）
        ///   bits 10..19 = 八面体法线 v
        ///   bits 20..29 = 切线绕法线的相位
        ///   bit31       = 切线手性 (±1)
        ///   缩放 = 1/511；z = (1-|u|) - |v|，z&lt;0 时按八面体规则翻转
        /// 实测：bit30 置位率 100%，解出的 |法线| 与 |切线 xyz| 都是精确 1.000000。
        ///
        /// 处理（**原始字节一个不丢**）：新增一个顶点流放 Normal(F32×3)+Tangent(F32×4)；
        /// 原来那个 4 字节槽改成 UV1 继续留在原流里（数据仍在，可用 mesh.uv2 取到）。
        /// 本来就是合法 F32×3 法线的网格原样返回。
        /// </summary>

        // ------------------------------------------------------------------
        //  FBX（ASCII 7.4）—— 让 Unity 从模型自动生成 Avatar / 可直接做动画
        // ------------------------------------------------------------------

        private void WriteGameObjectFbx(GameObject go, string filePath)
        {
            if (go.m_Transform == null) return;

            var nodeOf = new Dictionary<Transform, FbxExporter.Node>();
            FbxExporter.Node Build(Transform t)
            {
                if (t == null) return null;
                if (nodeOf.TryGetValue(t, out var cached)) return cached;
                var n = new FbxExporter.Node
                {
                    Name = t.m_GameObject.TryGet(out var g) && !string.IsNullOrEmpty(g.m_Name) ? g.m_Name : "node",
                    T = t.m_LocalPosition,
                    R = t.m_LocalRotation,
                    S = t.m_LocalScale,
                };
                nodeOf[t] = n;
                foreach (var cp in t.m_Children ?? new List<PPtr<Transform>>())
                {
                    if (!cp.TryGet(out var c)) continue;
                    var cn = Build(c);
                    if (cn != null) { cn.Parent = n; n.Children.Add(cn); }
                }
                return n;
            }

            var root = Build(go.m_Transform);
            if (root == null) return;

            var skins = new List<FbxExporter.Skin>();
            foreach (var kv in nodeOf)
            {
                var smr = FindSkinnedMeshRenderer(kv.Key);
                if (smr == null || !smr.m_Mesh.TryGet(out var mesh)) continue;
                var arrays = ExtractMeshArrays(mesh);
                if (arrays == null || arrays.VertexCount == 0) continue;

                var bones = new List<FbxExporter.Node>();
                var slots = new List<int>();
                var bind = new List<Matrix4x4>();
                var missing = new List<string>();
                int n = Math.Min(smr.m_Bones?.Count ?? 0, mesh.m_BindPose?.Length ?? 0);
                for (int i = 0; i < n; i++)
                {
                    if (!smr.m_Bones[i].TryGet(out var bt))
                    { missing.Add("#" + i); continue; }
                    if (!nodeOf.TryGetValue(bt, out var bn))
                    { missing.Add(nodeName(bt) + "#" + i); continue; }
                    bones.Add(bn);
                    slots.Add(i);
                    bind.Add(mesh.m_BindPose[i]);
                }
                if (bones.Count == 0) continue;
                if (missing.Count > 0)
                    Logger.Warning(string.Format("[FBX] {0}: {1}/{2} 根骨骼不在导出层级里（{3}）",
                        SanitizeName(mesh.m_Name), missing.Count, n, string.Join(", ", missing.Take(5))));

                // 零权重顶点的归属：SMR 的 rootBone 优先，其次第一根骨骼
                FbxExporter.Node fallback = bones[0];
                if (smr.m_RootBone != null && smr.m_RootBone.TryGet(out var rb) && nodeOf.TryGetValue(rb, out var rbn))
                    fallback = rbn;

                skins.Add(new FbxExporter.Skin
                {
                    MeshNode = kv.Value,
                    Mesh = arrays,
                    Bones = bones.ToArray(),
                    BoneSlots = slots.ToArray(),
                    Fallback = fallback,
                    Bindposes = bind.ToArray(),
                });
            }

            FbxExporter.Write(filePath, root, skins);
            Logger.Info("[FBX] " + Path.GetFileName(filePath) + "  节点 " + nodeOf.Count + "  蒙皮网格 " + skins.Count);
        }

        /// <summary>该 GameObject 的层级里是否有 SkinnedMeshRenderer（决定要不要导 FBX）。</summary>
        private static bool HasSkinnedMesh(GameObject go)
        {
            var stack = new Stack<Transform>();
            if (go.m_Transform != null) stack.Push(go.m_Transform);
            int guard = 0;
            while (stack.Count > 0 && guard++ < 20000)
            {
                var t = stack.Pop();
                if (FindSkinnedMeshRenderer(t) != null) return true;
                foreach (var cp in t.m_Children ?? new List<PPtr<Transform>>())
                    if (cp.TryGet(out var c)) stack.Push(c);
            }
            return false;
        }

        private static SkinnedMeshRenderer FindSkinnedMeshRenderer(Transform t)
        {
            if (t == null || !t.m_GameObject.TryGet(out var go)) return null;
            foreach (var cp in go.m_Components ?? new List<PPtr<Component>>())
            {
                if (cp.TryGet(out var c) && c is SkinnedMeshRenderer s) return s;
            }
            return null;
        }

        private static string nodeName(Transform t)
        {
            if (t != null && t.m_GameObject.TryGet(out var g) && !string.IsNullOrEmpty(g.m_Name)) return g.m_Name;
            return "(unresolved)";
        }

        private static int FbxFmtSize(int f)
        {
            switch (f)
            {
                case 0: return 4;
                case 1: return 2;
                case 2: return 1;
                case 3: return 1;
                case 4: return 2;
                case 5: return 2;
                case 6: return 1;
                case 7: return 1;
                case 8: return 2;
                case 9: return 2;
                case 10: return 4;
                case 11: return 4;
                default: return 4;
            }
        }

        private static long FbxReadInt(byte[] b, int o, int f)
        {
            switch (f)
            {
                case 0: return (long)BitConverter.ToSingle(b, o);
                case 4: return BitConverter.ToUInt16(b, o);
                case 5: return (short)BitConverter.ToUInt16(b, o);
                case 8: return BitConverter.ToUInt16(b, o);
                case 9: return (short)BitConverter.ToUInt16(b, o);
                default: return b[o];
            }
        }

        /// <summary>把 Mesh 解成 FBX 需要的数组（位置/法线/UV/骨骼索引与权重/三角形）。</summary>
        private static FbxExporter.MeshArrays ExtractMeshArrays(Mesh mesh)
        {
            NormalizeVertexChannels(mesh, out var data, out var chans);
            var vd = mesh.m_VertexData;
            if (vd == null || data.Length == 0 || chans == null || chans.Count < 14) return null;
            int cnt = (int)vd.m_VertexCount;
            if (cnt <= 0) return null;

            var strides = new Dictionary<int, int>();
            foreach (var c in chans)
            {
                int sz = FbxFmtSize(c.format) * c.dimension;
                if (sz <= 0) continue;
                strides.TryGetValue(c.stream, out int cur);
                strides[c.stream] = Math.Max(cur, c.offset + sz);
            }
            var bases = new Dictionary<int, int>();
            int acc = 0;
            foreach (var st in strides.Keys.OrderBy(x => x))
            {
                acc = (acc + 15) / 16 * 16;
                bases[st] = acc;
                acc += strides[st] * cnt;
            }
            if (acc > data.Length) return null;

            var pos = new Vector3[cnt];
            var nor = new Vector3[cnt];
            var uv = new Vector2[cnt];
            var bi = new byte[cnt * 4];
            var bw = new ushort[cnt * 4];
            bool hasN = chans[1].dimension > 0;
            bool hasU = chans[4].dimension > 0;
            bool hasW = chans[12].dimension > 0;
            bool hasI = chans[13].dimension > 0;
            // ★ 权重通道的两种特殊形态（陈千语面部网格实测）：
            //   ① fmt=Float32：不能走 FbxReadInt 的 (long) 截断 —— 0.63 → 0 ⇒ 整批顶点丢权重。
            //      这里读成 float 后转 UNorm16 表示（WeightFormat=4 解码回 1/65535 精度）。
            //   ② 权重通道 dimension=0（被剥离）但 BlendIndices 在 ⇒ 刚性绑定：首索引权重 1。
            bool wFloat = hasW && chans[12].format == 0;
            bool wRigid = !hasW && hasI;

            for (int i = 0; i < cnt; i++)
            {
                int o = bases[0] + i * strides[0] + chans[0].offset;
                pos[i] = new Vector3(BitConverter.ToSingle(data, o), BitConverter.ToSingle(data, o + 4), BitConverter.ToSingle(data, o + 8));
                if (hasN && chans[1].format == 0)
                {
                    o = bases[chans[1].stream] + i * strides[chans[1].stream] + chans[1].offset;
                    nor[i] = new Vector3(BitConverter.ToSingle(data, o), BitConverter.ToSingle(data, o + 4), BitConverter.ToSingle(data, o + 8));
                }
                if (hasU)
                {
                    o = bases[chans[4].stream] + i * strides[chans[4].stream] + chans[4].offset;
                    uv[i] = new Vector2(BitConverter.ToSingle(data, o), BitConverter.ToSingle(data, o + 4));
                }
                if (hasW)
                {
                    o = bases[chans[12].stream] + i * strides[chans[12].stream] + chans[12].offset;
                    if (wFloat)
                    {
                        for (int k = 0; k < 4; k++)
                        {
                            float f = BitConverter.ToSingle(data, o + 4 * k);
                            if (f < 0f) f = 0f; else if (f > 1f) f = 1f;
                            bw[i * 4 + k] = (ushort)MathF.Round(f * 65535f);
                        }
                    }
                    else
                    {
                        for (int k = 0; k < 4; k++)
                            bw[i * 4 + k] = (ushort)FbxReadInt(data, o + FbxFmtSize(chans[12].format) * k, chans[12].format);
                    }
                }
                else if (wRigid)
                {
                    bw[i * 4] = 65535;   // 首索引权重 1.0（UNorm16），索引沿用 ch13
                }
                if (hasI)
                {
                    o = bases[chans[13].stream] + i * strides[chans[13].stream] + chans[13].offset;
                    for (int k = 0; k < 4; k++)
                        bi[i * 4 + k] = (byte)FbxReadInt(data, o + FbxFmtSize(chans[13].format) * k, chans[13].format);
                }
            }

            if (Environment.GetEnvironmentVariable("ANIMESTUDIO_MESH_DEBUG") == "1")
            {
                int zw = 0;
                for (int i = 0; i < cnt; i++)
                    if (bw[i*4]==0 && bw[i*4+1]==0 && bw[i*4+2]==0 && bw[i*4+3]==0) zw++;
                var cd = string.Join(" ", chans.Select((c, i) => string.Format("[{0}]s{1}o{2}f{3}d{4}", i, c.stream, c.offset, c.format, c.dimension)));
                Logger.Warning(string.Format("[MeshDebug] {0}: verts={1} dataLen={2} zeroW={3} fmt12={4} fmt13={5}",
                    mesh.m_Name, cnt, data.Length, zw, chans[12].format, chans[13].format));
                Logger.Warning("    ch: " + cd);
                Logger.Warning("    strides=" + string.Join(",", strides.OrderBy(kv => kv.Key).Select(kv => kv.Key + ":" + kv.Value))
                    + " bases=" + string.Join(",", bases.OrderBy(kv => kv.Key).Select(kv => kv.Key + ":" + kv.Value)));
            }

            var idx = (mesh.m_IndexBuffer ?? Array.Empty<uint>()).Select(v => (int)v).ToArray();
            return new FbxExporter.MeshArrays
            {
                Name = mesh.m_Name ?? "mesh",
                VertexCount = cnt,
                Positions = pos,
                Normals = (hasN && chans[1].format == 0) ? nor : null,
                Uvs = hasU ? uv : null,
                BoneIndices = bi,
                BoneWeights = bw,
                WeightFormat = (hasW && !wFloat) ? (int)chans[12].format : 4,
                Indices = idx,
            };
        }


        private static void NormalizeVertexChannels(Mesh mesh, out byte[] data, out List<ChannelInfo> channels)
        {
            var vd = mesh.m_VertexData;
            data = vd?.m_DataSize ?? Array.Empty<byte>();
            channels = vd?.m_Channels != null ? new List<ChannelInfo>(vd.m_Channels) : new List<ChannelInfo>();
            if (vd == null || channels.Count < 14 || data.Length == 0) { data = Array.Empty<byte>(); return; }
            if (vd.m_VertexCount == 0) return;

            int FmtSize(int f) => f switch
            {
                0 => 4, 1 => 2, 2 => 1, 3 => 1, 4 => 2, 5 => 2, 6 => 1, 7 => 1, 8 => 2, 9 => 2, 10 => 4, 11 => 4, _ => 4
            };

            bool packed = channels[1].format == 0 && channels[1].dimension == 1;
            bool alreadyOk = channels[1].format == 0 && channels[1].dimension == 3;

            int cnt = (int)vd.m_VertexCount;
            var strides = new Dictionary<int, int>();
            foreach (var c in channels)
            {
                int sz = FmtSize(c.format) * c.dimension;
                if (sz <= 0) continue;
                strides.TryGetValue(c.stream, out int cur);
                strides[c.stream] = Math.Max(cur, c.offset + sz);
            }
            if (strides.Count == 0) return;

            // ★★ 流偏移规则（由 batchmode 实测确证）：**Unity 把每段流的起点向上 16 字节对齐**。
            //    源数据的 `m_Streams[].offset` 就是证据：cloth_01_lod1 的 stream1 止于 240840，
            //    而 stream2 的 offset = **240848**（8 字节是**对齐间隙**）。
            //    Unity 的 mesh YAML 里没有 m_Streams，它按同一规则重算 ⇒ 所以
            //    ① 源布局**必须原样保留**（我曾把间隙「重排掉」，那是反向的，会让 Unity 整体错位读权重）；
            //    ② 我们自己追加的流也要落在 align16 的起点上。
            var bases = new Dictionary<int, int>();
            int acc = 0;
            foreach (var st in strides.Keys.OrderBy(x => x))
            {
                acc = (acc + 15) / 16 * 16;      // 向上 16 字节对齐
                bases[st] = acc;
                acc += strides[st] * cnt;
            }
            int layout = acc;
            if (layout > data.Length || layout <= 0) return;   // 数据不足就别动
            if (data.Length > layout)
            {
                var t = new byte[layout];
                Array.Copy(data, 0, t, 0, layout);   // 只裁掉**尾部**多余字节
                data = t;
            }

            if (alreadyOk) return;      // 法线本来就合法 ⇒ 只裁过尾，直接返回
            if (!packed) return;        // 其它形态暂不处理

            const int STRIDE = 28;   // Normal 12 + Tangent 16
            int newStream = strides.Keys.Max() + 1;
            var blk = new byte[(long)cnt * STRIDE];

            // 几何法线兜底（bit30 未置位时用）
            var geo = (Vector3[])null;
            var posC = channels[0];
            var idx = mesh.m_IndexBuffer ?? Array.Empty<uint>();
            bool use16 = mesh.m_Use16BitIndices;

            for (int i = 0; i < cnt; i++)
            {
                int off = bases[channels[1].stream] + i * strides[channels[1].stream] + channels[1].offset;
                if (off + 4 > data.Length) break;
                uint p = BitConverter.ToUInt32(data, off);
                float nx, ny, nz, tx, ty, tz, tw = 1f;
                if ((p & 0x40000000u) != 0u)
                {
                    DecodePackedNormalTangent(p, out nx, out ny, out nz, out tx, out ty, out tz, out tw);
                }
                else
                {
                    if (geo == null) geo = BakeNormals(data, bases, strides, cnt, posC, idx, use16);
                    nx = geo[i].X; ny = geo[i].Y; nz = geo[i].Z;
                    tx = 1f; ty = 0f; tz = 0f;
                }
                int w = i * STRIDE;
                BitConverter.GetBytes(nx).CopyTo(blk, w + 0);
                BitConverter.GetBytes(ny).CopyTo(blk, w + 4);
                BitConverter.GetBytes(nz).CopyTo(blk, w + 8);
                BitConverter.GetBytes(tx).CopyTo(blk, w + 12);
                BitConverter.GetBytes(ty).CopyTo(blk, w + 16);
                BitConverter.GetBytes(tz).CopyTo(blk, w + 20);
                BitConverter.GetBytes(tw).CopyTo(blk, w + 24);
            }

            // 新缓冲 = 原布局区（逐字节保留，含对齐间隙） + 新流（落在 align16 起点）
            int blobBase = (layout + 15) / 16 * 16;
            var outBuf = new byte[blobBase + blk.Length];
            Array.Copy(data, 0, outBuf, 0, layout);
            Array.Copy(blk, 0, outBuf, blobBase, blk.Length);
            data = outBuf;

            // ★ 原打包槽让位给 **UV1**（ch5..ch11），并声明成 `UNorm8×4`：
            //   字节数不变（4B）且是 Unity 合法布局；数据仍在原流的原偏移，可用 mesh.uv2 读回。
            //   （不能放 ch3=Color —— Color 不允许 dim 1；也不能留 dim 1 的 UV。）
            int uv1 = -1;
            for (int i = 5; i <= 11; i++)
            {
                if (FmtSize(channels[i].format) * channels[i].dimension == 0) { uv1 = i; break; }
            }
            if (uv1 < 0) uv1 = 5;
            var keep = channels[1];
            channels[uv1] = new ChannelInfo { stream = keep.stream, offset = keep.offset, format = 2, dimension = 4 };
            channels[3] = new ChannelInfo { stream = 0, offset = 0, format = 0, dimension = 0 };
            channels[1] = new ChannelInfo { stream = (byte)newStream, offset = 0, format = 0, dimension = 3 };
            channels[2] = new ChannelInfo { stream = (byte)newStream, offset = 12, format = 0, dimension = 4 };
        }

        private static int rawLen(byte[] b) => b?.Length ?? 0;

        private static void DecodePackedNormalTangent(uint p, out float nx, out float ny, out float nz,
            out float tx, out float ty, out float tz, out float tw)
        {
            int Sext(int v, int bits)
            {
                int half = 1 << (bits - 1);
                return v >= half ? v - (1 << bits) : v;
            }
            float u = Sext((int)(p & 0x3FF), 10) / 511f;
            float v = Sext((int)((p >> 10) & 0x3FF), 10) / 511f;
            float a = Sext((int)((p >> 20) & 0x3FF), 10) / 511f;
            tw = ((p >> 31) & 1) != 0 ? 1f : -1f;
            float z = (1f - Math.Abs(u)) - Math.Abs(v);
            float ox, oy;
            if (z < 0f)
            {
                ox = (1f - Math.Abs(v)) * (u >= 0f ? 1f : -1f);
                oy = (1f - Math.Abs(u)) * (v >= 0f ? 1f : -1f);
            }
            else { ox = u; oy = v; }
            float l = (float)Math.Sqrt(ox * ox + oy * oy + z * z);
            if (l <= 1e-12f) l = 1f;
            nx = ox / l; ny = oy / l; nz = z / l;

            // 由法线造正交基，再按相位旋转
            float t0x = ny - nz, t0y = nz - nx, t0z = nx - ny;
            float d = t0x * nx + t0y * ny + t0z * nz;
            t0x -= nx * d; t0y -= ny * d; t0z -= nz * d;
            l = (float)Math.Sqrt(t0x * t0x + t0y * t0y + t0z * t0z);
            if (l <= 1e-12f) { t0x = 1f; t0y = 0f; t0z = 0f; l = 1f; }
            t0x /= l; t0y /= l; t0z /= l;
            float t1x = ny * t0z - nz * t0y, t1y = nz * t0x - nx * t0z, t1z = nx * t0y - ny * t0x;
            float ang = a;
            float cx = 1f - 2f * ang;
            float cy = (ang >= 0f ? 1f : -1f) * (1f - Math.Abs(cx));
            l = (float)Math.Sqrt(cx * cx + cy * cy);
            if (l <= 1e-12f) { cx = 1f; cy = 0f; l = 1f; }
            cx /= l; cy /= l;
            tx = t0x * cx + t1x * cy;
            ty = t0y * cx + t1y * cy;
            tz = t0z * cx + t1z * cy;
        }

        /// <summary>从索引缓冲算面积加权几何法线（bit30 未置位时的兜底）。</summary>
        private static Vector3[] BakeNormals(byte[] data, Dictionary<int, int> bases, Dictionary<int, int> strides,
            int cnt, ChannelInfo pos, uint[] idx, bool use16)
        {
            var pos3 = new Vector3[cnt];
            for (int i = 0; i < cnt; i++)
            {
                int o = bases[pos.stream] + i * strides[pos.stream] + pos.offset;
                if (o + 12 > data.Length) { pos3[i] = Vector3.Zero; continue; }
                pos3[i] = new Vector3(BitConverter.ToSingle(data, o), BitConverter.ToSingle(data, o + 4),
                                      BitConverter.ToSingle(data, o + 8));
            }
            var acc = new Vector3[cnt];
            int step = use16 ? 1 : 1;   // 索引在 m_IndexBuffer 里已按 16/32 位解过，这里只用下标
            for (int k = 0; k + 2 < idx.Length; k += 3)
            {
                int a = (int)idx[k], b = (int)idx[k + 1], c = (int)idx[k + 2];
                if (a >= cnt || b >= cnt || c >= cnt) continue;
                var u = pos3[b] - pos3[a];
                var v = pos3[c] - pos3[a];
                var n = new Vector3(u.Y * v.Z - u.Z * v.Y, u.Z * v.X - u.X * v.Z, u.X * v.Y - u.Y * v.X);
                acc[a] = acc[a] + n; acc[b] = acc[b] + n; acc[c] = acc[c] + n;
            }
            for (int i = 0; i < cnt; i++)
            {
                float l = (float)Math.Sqrt(acc[i].X * acc[i].X + acc[i].Y * acc[i].Y + acc[i].Z * acc[i].Z);
                acc[i] = l > 1e-12f ? new Vector3(acc[i].X / l, acc[i].Y / l, acc[i].Z / l) : new Vector3(0f, 0f, 1f);
            }
            return acc;
        }

        private string MeshToYaml(Mesh mesh)
        {
            var doc = new YAMLDocument();
            var root = doc.CreateMappingRoot();
            root.Tag = "43";
            root.Anchor = "4300000";      // Mesh 主对象 ID

            var m = new YAMLMappingNode();
            root.Add("Mesh", m);
            m.Add("m_ObjectHideFlags", 0);
            m.Add("m_CorrespondingSourceObject", NullReference());
            m.Add("m_PrefabInstance", NullReference());
            m.Add("m_PrefabAsset", NullReference());
            m.Add("m_Name", SanitizeName(mesh.m_Name));
            m.Add("serializedVersion", 10);   // ★ Unity 2022.3 实测为 10（不是 11）

            var subMeshes = new YAMLSequenceNode();
            foreach (var sub in mesh.m_SubMeshes ?? new List<SubMesh>())
            {
                var subNode = new YAMLMappingNode();
                subMeshes.Add(subNode);
                subNode.Add("serializedVersion", 2);
                subNode.Add("firstByte", sub.firstByte);
                subNode.Add("indexCount", sub.indexCount);
                subNode.Add("topology", (int)sub.topology);
                subNode.Add("baseVertex", sub.baseVertex);
                subNode.Add("firstVertex", sub.firstVertex);
                subNode.Add("vertexCount", sub.vertexCount);

                var aabb = new YAMLMappingNode();
                subNode.Add("localAABB", aabb);
                aabb.Add("m_Center", Vector3Node(sub.localAABB.m_Center));
                aabb.Add("m_Extent", Vector3Node(sub.localAABB.m_Extent));
            }
            m.Add("m_SubMeshes", subMeshes);

            var shapes = mesh.m_Shapes;
            var shapesNode = new YAMLMappingNode();
            m.Add("m_Shapes", shapesNode);
            if (shapes != null)
            {
                var verts = new YAMLSequenceNode();
                foreach (var v in shapes.vertices ?? new List<BlendShapeVertex>())
                {
                    var vNode = new YAMLMappingNode();
                    verts.Add(vNode);
                    vNode.Add("index", v.index);
                    vNode.Add("vertex", Vector3Node(v.vertex));
                    vNode.Add("normal", Vector3Node(v.normal));
                    vNode.Add("tangent", Vector3Node(v.tangent));
                }
                shapesNode.Add("vertices", verts);

                var shapeList = new YAMLSequenceNode();
                foreach (var sh in shapes.shapes ?? new List<MeshBlendShape>())
                {
                    var shNode = new YAMLMappingNode();
                    shapeList.Add(shNode);
                    shNode.Add("firstVertex", sh.firstVertex);
                    shNode.Add("vertexCount", sh.vertexCount);
                    shNode.Add("hasNormals", sh.hasNormals ? 1 : 0);
                    shNode.Add("hasTangents", sh.hasTangents ? 1 : 0);
                }
                shapesNode.Add("shapes", shapeList);

                var channels = new YAMLSequenceNode();
                foreach (var ch in shapes.channels ?? new List<MeshBlendShapeChannel>())
                {
                    var chNode = new YAMLMappingNode();
                    channels.Add(chNode);
                    chNode.Add("name", ch.name);
                    chNode.Add("nameHash", ch.nameHash);
                    chNode.Add("frameIndex", ch.frameIndex);
                    chNode.Add("frameCount", ch.frameCount);
                }
                shapesNode.Add("channels", channels);

                shapesNode.Add("fullWeights", FloatSequence(shapes.fullWeights ?? Array.Empty<float>()));
            }

            // ── Unity Mesh 标准字段（serializedVersion 10 的真实 schema；键名必须对）──
            //
            // ★★ 蒙皮的关键：m_BindPose = 每根骨骼**绑定姿势世界矩阵的逆**，条数必须等于
            //    SkinnedMeshRenderer.m_Bones 的条数（chen 实测 58 条，与 SMR 的 58 一致）。
            //    源 `.ab` 里这个数组是**非压缩**存的（`mesh.m_BindPose`），
            //    早前误信 `m_CompressedMesh.m_BindPoses.m_NumItems==0` 而写成空数组 ⇒ 蒙皮不工作。
            //
            // ★ 转置：AnimeStudio 的原始 16 个 float 是**列主序**（M00,M10,M20,M30,M01,…），
            //   而 Unity 的 YAML 用 **行主序** e00..e33（e{r}{c}）。所以 yaml[r][c] = raw[c*4+r]。
            var bindSeq = new YAMLSequenceNode();
            if (mesh.m_BindPose != null)
            {
                foreach (var mm in mesh.m_BindPose)
                {
                    var raw = new float[16]
                    {
                        mm.M00, mm.M10, mm.M20, mm.M30,
                        mm.M01, mm.M11, mm.M21, mm.M31,
                        mm.M02, mm.M12, mm.M22, mm.M32,
                        mm.M03, mm.M13, mm.M23, mm.M33
                    };
                    var mn = new YAMLMappingNode();
                    for (int r = 0; r < 4; r++)
                    {
                        for (int c = 0; c < 4; c++)
                        {
                            // ★ 实测校准：raw 是「M00,M10,M20,M30,M01,…」顺序，
                            //   而 Unity 的 e{r}{c} 需要**平移落在最后一列**（标准列向量约定）。
                            //   写成 raw[r*4+c] 才对；写成 raw[c*4+r] 会得到转置矩阵
                            //   （表现为平移跑到最后一行 ⇒ 蒙皮全乱）。
                            mn.Add("e" + r + c, raw[r * 4 + c]);
                        }
                    }
                    bindSeq.Add(mn);
                }
            }
            m.Add("m_BindPose", bindSeq);
            // uint 数组在 Unity YAML 里是 hex 串（对照 m_RendererFeatureMap / m_IndexBuffer）
            m.Add("m_BoneNameHashes", mesh.m_BoneNameHashes != null && mesh.m_BoneNameHashes.Length > 0
                ? (YAMLNode)new YAMLScalarNode(string.Concat(mesh.m_BoneNameHashes.Select(h => h.ToString("x8"))))
                : new YAMLScalarNode(string.Empty));
            m.Add("m_RootBoneNameHash", mesh.m_RootBoneNameHash);
            m.Add("m_BonesAABB", new YAMLSequenceNode());
            var vbwNode = new YAMLMappingNode();
            vbwNode.Add("m_Data", new YAMLScalarNode(string.Empty));
            m.Add("m_VariableBoneCountWeights", vbwNode);
            m.Add("m_MeshCompression", 0);
            m.Add("m_IsReadable", 1);
            m.Add("m_KeepVertices", 0);
            m.Add("m_KeepIndices", 0);
            m.Add("m_IndexFormat", mesh.m_Use16BitIndices ? 0 : 1);
            m.Add("m_IndexBuffer", HexIndices(mesh.m_IndexBuffer ?? Array.Empty<uint>(), mesh.m_Use16BitIndices));

            var vd = mesh.m_VertexData;
            var vdNode = new YAMLMappingNode();
            m.Add("m_VertexData", vdNode);
            vdNode.Add("serializedVersion", 3);
            vdNode.Add("m_VertexCount", vd?.m_VertexCount ?? 0);
            // ★ 顶点通道归一化：源数据的法线常是**打包 uint32**（声明成 Float32×1，Unity 不认）。
            //   不处理的话 Unity 直接拒绝该 mesh ⇒ prefab 里显示“没有 mesh 引用”。
            NormalizeVertexChannels(mesh, out var rawVerts, out var fixedChannels);
            var channelsNode = new YAMLSequenceNode();
            foreach (var ch in fixedChannels)
            {
                var chNode = new YAMLMappingNode();
                channelsNode.Add(chNode);
                chNode.Add("stream", ch.stream);
                chNode.Add("offset", ch.offset);
                chNode.Add("format", ch.format);
                chNode.Add("dimension", ch.dimension);
            }
            vdNode.Add("m_Channels", channelsNode);
            // ★ m_DataSize = 字节数（整数）；_typelessdata 必须是 **HEX**。
            //   对照 Unity 自己写的 mesh（Library/PackageCache/.../Ferns/Fern_A.asset）实测：
            //   `_typelessdata` 是十六进制串（长度 = m_DataSize×2），**不是 base64**。
            //   旧实现用 base64 ⇒ Unity 解不出顶点 ⇒ 模型不可见。
            vdNode.Add("m_DataSize", rawVerts.Length);
            vdNode.Add("_typelessdata", rawVerts.Length > 0
                ? (YAMLNode)new YAMLScalarNode(Hex(rawVerts))
                : new YAMLScalarNode(string.Empty));

            var compressed = mesh.m_CompressedMesh;
            var cNode = new YAMLMappingNode();
            m.Add("m_CompressedMesh", cNode);
            cNode.Add("m_Vertices", PackedFloatNode(compressed?.m_Vertices));
            cNode.Add("m_UV", PackedFloatNode(compressed?.m_UV));
            cNode.Add("m_BindPoses", PackedFloatNode(compressed?.m_BindPoses));
            cNode.Add("m_Normals", PackedFloatNode(compressed?.m_Normals));
            cNode.Add("m_Tangents", PackedFloatNode(compressed?.m_Tangents));
            cNode.Add("m_Weights", PackedIntNode(compressed?.m_Weights));
            cNode.Add("m_NormalSigns", PackedIntNode(compressed?.m_NormalSigns));
            cNode.Add("m_TangentsSigns", PackedIntNode(compressed?.m_TangentSigns));
            cNode.Add("m_FloatColors", PackedFloatNode(compressed?.m_FloatColors));
            cNode.Add("m_BoneIndices", PackedIntNode(compressed?.m_BoneIndices));
            cNode.Add("m_Triangles", PackedIntNode(compressed?.m_Triangles));
            cNode.Add("m_Colors", PackedIntNode(compressed?.m_Colors));
            cNode.Add("m_UVInfo", compressed?.m_UVInfo ?? 0);

            var aabbNode = new YAMLMappingNode();
            m.Add("m_LocalAABB", aabbNode);
            aabbNode.Add("m_Center", Vector3Node(mesh.m_LocalAABB.m_Center));
            aabbNode.Add("m_Extent", Vector3Node(mesh.m_LocalAABB.m_Extent));
            m.Add("m_MeshUsageFlags", mesh.m_MeshUsageFlags);
            m.Add("m_BakedConvexCollisionMesh", Hex(mesh.m_BakedConvexCollisionMesh));
            m.Add("m_BakedTriangleCollisionMesh", Hex(mesh.m_BakedTriangleCollisionMesh));

            // ★ Unity 2022.3 写的是 m_MeshMetrics[0]/[1]（两个标量键），不是 m_Metrics 映射块。
            m.Add("m_MeshMetrics[0]", 1);
            m.Add("m_MeshMetrics[1]", 1);
            m.Add("m_MeshOptimizationFlags", 1);

            var sd = mesh.m_StreamData;
            var sdNode = new YAMLMappingNode();
            m.Add("m_StreamData", sdNode);
            sdNode.Add("serializedVersion", 2);
            sdNode.Add("offset", sd?.offset ?? 0);
            sdNode.Add("size", sd?.size ?? 0);
            sdNode.Add("path", new YAMLScalarNode(sd?.path ?? string.Empty));

            return WriteYaml(doc);
        }

        private static YAMLMappingNode PackedFloatNode(PackedFloatVector v)
        {
            var node = new YAMLMappingNode();
            node.Add("m_NumItems", v?.m_NumItems ?? 0);
            node.Add("m_Range", v?.m_Range ?? 0f);
            node.Add("m_Start", v?.m_Start ?? 0f);
            node.Add("m_Data", Hex(v?.m_Data));
            node.Add("m_BitSize", v?.m_BitSize ?? 0);
            return node;
        }

        private static YAMLMappingNode PackedIntNode(PackedIntVector v)
        {
            var node = new YAMLMappingNode();
            node.Add("m_NumItems", v?.m_NumItems ?? 0);
            node.Add("m_Data", Hex(v?.m_Data));
            node.Add("m_BitSize", v?.m_BitSize ?? 0);
            return node;
        }

        // ------------------------------------------------------------------
        //  GameObject / Animator (.prefab)
        // ------------------------------------------------------------------

        private string PrefabToYaml(AssetItem item)
        {
            GameObject rootGo;
            if (item.Asset is GameObject go0)
            {
                rootGo = go0;
            }
            else if (item.Asset is Animator animator && animator.m_GameObject.TryGet(out var go1))
            {
                rootGo = go1;
            }
            else
            {
                return null;
            }

            // Collect every object that belongs into the prefab: the GameObject
            // hierarchy plus the components we can serialize.  These reference
            // each other by plain fileID (same-file convention).
            var prefabObjects = new List<Object>();
            var collected = new HashSet<(SerializedFile, long)>();

            void Collect(GameObject go)
            {
                var key = (go.assetsFile, go.m_PathID);
                if (!collected.Add(key))
                {
                    return;
                }
                prefabObjects.Add(go);
                var transform = go.m_Transform;
                if (transform != null)
                {
                    prefabObjects.Add(transform);
                    collected.Add((transform.assetsFile, transform.m_PathID));
                    foreach (var child in transform.m_Children ?? new List<PPtr<Transform>>())
                    {
                        if (child.TryGet(out var childTransform) &&
                            childTransform.m_GameObject.TryGet(out var childGo))
                        {
                            Collect(childGo);
                        }
                    }
                }
            }

            Collect(rootGo);

            foreach (var go in prefabObjects.OfType<GameObject>().ToList())
            {
                foreach (var component in go.m_Components ?? new List<PPtr<Component>>())
                {
                    if (!component.TryGet(out var c))
                    {
                        continue;
                    }
                    var supported = c is Transform or MeshFilter or MeshRenderer or SkinnedMeshRenderer or Animator or Animation;
                    if (supported && collected.Add((c.assetsFile, c.m_PathID)))
                    {
                        prefabObjects.Add(c);
                    }
                }
            }

            foreach (var obj in prefabObjects)
            {
                Register(obj);
            }

            var docs = new List<YAMLDocument>();
            foreach (var obj in prefabObjects)
            {
                switch (obj)
                {
                    case GameObject go:
                        docs.Add(GameObjectDoc(go));
                        break;
                    case Transform t:
                        docs.Add(TransformDoc(t));
                        break;
                    case MeshFilter mf:
                        docs.Add(MeshFilterDoc(mf));
                        break;
                    case MeshRenderer mr:
                        docs.Add(MeshRendererDoc(mr));
                        break;
                    case SkinnedMeshRenderer smr:
                        docs.Add(SkinnedMeshRendererDoc(smr));
                        break;
                    case Animator animator2:
                        docs.Add(AnimatorDoc(animator2));
                        break;
                    case Animation anim:
                        docs.Add(AnimationDoc(anim));
                        break;
                }
            }

            // objects collected only for this prefab must not stay registered as
            // "in progress" for other exports
            foreach (var key in collected)
            {
                inProgress.Remove(key);
            }

            return docs.Count > 0 ? WriteYaml(docs) : null;
        }

        private YAMLDocument GameObjectDoc(GameObject go)
        {
            var doc = new YAMLDocument();
            var root = doc.CreateMappingRoot();
            root.Tag = "1";
            root.Anchor = go.m_PathID.ToString();

            var node = new YAMLMappingNode();
            root.Add("GameObject", node);
            node.Add("m_ObjectHideFlags", 0);
            node.Add("m_CorrespondingSourceObject", NullReference());
            node.Add("m_PrefabInstance", NullReference());
            node.Add("m_PrefabAsset", NullReference());
            node.Add("serializedVersion", 6);
            node.Add("m_Component", ComponentsNode(go));
            node.Add("m_Layer", 0);
            node.Add("m_Name", SanitizeName(go.m_Name));
            node.Add("m_TagString", "Untagged");
            node.Add("m_Icon", NullReference());
            node.Add("m_NavMeshLayer", 0);
            node.Add("m_StaticEditorFlags", 0);
            node.Add("m_IsActive", 1);
            return doc;
        }

        private YAMLSequenceNode ComponentsNode(GameObject go)
        {
            var seq = new YAMLSequenceNode();
            foreach (var component in go.m_Components ?? new List<PPtr<Component>>())
            {
                if (component.TryGet(out var c))
                {
                    var supported = c is Transform or MeshFilter or MeshRenderer or SkinnedMeshRenderer or Animator or Animation;
                    if (supported)
                    {
                        var pair = new YAMLMappingNode();
                        seq.Add(pair);
                        pair.Add("component", SameFileRef(c));
                    }
                }
            }
            return seq;
        }

        /// <summary>Reference to an object serialized inside the same .prefab file.</summary>
        private YAMLMappingNode SameFileRef(Object target)
        {
            var node = new YAMLMappingNode(MappingStyle.Flow);
            node.Add("fileID", target.m_PathID);
            return node;
        }

        private bool InPrefab(Object target)
        {
            return entries.TryGetValue((target.assetsFile, target.m_PathID), out var entry) &&
                   entry.FileID == target.m_PathID;
        }

        private YAMLDocument TransformDoc(Transform t)
        {
            var doc = new YAMLDocument();
            var root = doc.CreateMappingRoot();
            root.Tag = "4";
            root.Anchor = t.m_PathID.ToString();

            var node = new YAMLMappingNode();
            root.Add("Transform", node);
            node.Add("m_ObjectHideFlags", 0);
            node.Add("m_CorrespondingSourceObject", NullReference());
            node.Add("m_PrefabInstance", NullReference());
            node.Add("m_PrefabAsset", NullReference());
            node.Add("m_GameObject", t.m_GameObject.TryGet(out var go) ? SameFileRef(go) : NullReference());
            var q = t.m_LocalRotation;
            node.Add("m_LocalRotation", Vector4Node(q.X, q.Y, q.Z, q.W));
            node.Add("m_LocalPosition", Vector3Node(t.m_LocalPosition));
            node.Add("m_LocalScale", Vector3Node(t.m_LocalScale));

            var children = new YAMLSequenceNode();
            foreach (var child in t.m_Children ?? new List<PPtr<Transform>>())
            {
                if (child.TryGet(out var c) && InPrefab(c))
                {
                    var item = new YAMLMappingNode(MappingStyle.Flow);
                    item.Add("fileID", c.m_PathID);
                    children.Add(item);
                }
            }
            node.Add("m_Children", children);
            node.Add("m_Father", t.m_Father.TryGet(out var father) && InPrefab(father) ? SameFileRef(father) : NullReference());
            node.Add("m_RootOrder", 0);
            node.Add("m_LocalEulerAnglesHint", Vector3Node(default));
            return doc;
        }

        private YAMLDocument MeshFilterDoc(MeshFilter mf)
        {
            var doc = new YAMLDocument();
            var root = doc.CreateMappingRoot();
            root.Tag = "33";
            root.Anchor = mf.m_PathID.ToString();

            var node = new YAMLMappingNode();
            root.Add("MeshFilter", node);
            node.Add("m_ObjectHideFlags", 0);
            node.Add("m_CorrespondingSourceObject", NullReference());
            node.Add("m_PrefabInstance", NullReference());
            node.Add("m_PrefabAsset", NullReference());
            node.Add("m_GameObject", mf.m_GameObject.TryGet(out var go) ? SameFileRef(go) : NullReference());
            node.Add("m_Mesh", Reference(mf.m_Mesh));
            return doc;
        }

        private void AddRendererHeader(YAMLMappingNode node, Component renderer, int rayTracingMode)
        {
            node.Add("m_ObjectHideFlags", 0);
            node.Add("m_CorrespondingSourceObject", NullReference());
            node.Add("m_PrefabInstance", NullReference());
            node.Add("m_PrefabAsset", NullReference());
            node.Add("m_GameObject", renderer.m_GameObject.TryGet(out var go) ? SameFileRef(go) : NullReference());
            node.Add("m_Enabled", 1);
            node.Add("m_CastShadows", 1);
            node.Add("m_ReceiveShadows", 1);
            node.Add("m_DynamicOccludee", 1);
            node.Add("m_MotionVectors", 1);
            node.Add("m_LightProbeUsage", 1);
            node.Add("m_ReflectionProbeUsage", 1);
            node.Add("m_RayTracingMode", rayTracingMode);
            node.Add("m_RenderingLayerMask", 1);
            node.Add("m_RendererPriority", 0);
        }

        private YAMLSequenceNode MaterialsNode(Renderer renderer)
        {
            var seq = new YAMLSequenceNode();
            foreach (var material in renderer.m_Materials ?? new List<PPtr<Material>>())
            {
                seq.Add(Reference(material));
            }
            return seq;
        }

        private static YAMLMappingNode StaticBatchInfo()
        {
            var node = new YAMLMappingNode(MappingStyle.Flow);
            node.Add("useSubMeshes", 0);
            node.Add("subMeshIndices", new YAMLSequenceNode());
            return node;
        }

        private YAMLDocument MeshRendererDoc(MeshRenderer mr)
        {
            var doc = new YAMLDocument();
            var root = doc.CreateMappingRoot();
            root.Tag = "23";
            root.Anchor = mr.m_PathID.ToString();

            var node = new YAMLMappingNode();
            root.Add("MeshRenderer", node);
            AddRendererHeader(node, mr, 2);
            node.Add("m_Materials", MaterialsNode(mr));
            node.Add("m_StaticBatchInfo", StaticBatchInfo());
            node.Add("m_ProbeAnchor", NullReference());
            node.Add("m_LightProbeVolumeOverride", NullReference());
            node.Add("m_SortingLayerID", 0);
            node.Add("m_SortingOrder", 0);
            node.Add("m_AdditionalVertexStreams", NullReference());
            return doc;
        }

        private YAMLDocument SkinnedMeshRendererDoc(SkinnedMeshRenderer smr)
        {
            var doc = new YAMLDocument();
            var root = doc.CreateMappingRoot();
            root.Tag = "137";
            root.Anchor = smr.m_PathID.ToString();

            var node = new YAMLMappingNode();
            root.Add("SkinnedMeshRenderer", node);
            AddRendererHeader(node, smr, 3);
            node.Add("m_Materials", MaterialsNode(smr));
            node.Add("m_StaticBatchInfo", StaticBatchInfo());
            node.Add("m_ProbeAnchor", NullReference());
            node.Add("m_LightProbeVolumeOverride", NullReference());
            // SkinnedMeshRenderer-specific: the mesh lives HERE (MeshRenderer gets it from
            // MeshFilter). Upstream omitted this field entirely, so every exported skinned
            // prefab had no geometry at all. Placed after the inherited Renderer fields,
            // following Unity's base-then-derived YAML layout.
            node.Add("m_Mesh", Reference(smr.m_Mesh));

            var aabb = new YAMLMappingNode();
            node.Add("m_AABB", aabb);
            aabb.Add("m_Center", Vector3Node(smr.m_AABB.m_Center));
            aabb.Add("m_Extent", Vector3Node(smr.m_AABB.m_Extent));
            node.Add("m_BlendShapeWeights", FloatSequence(smr.m_BlendShapeWeights ?? Array.Empty<float>()));
            node.Add("m_RootBone", smr.m_RootBone.TryGet(out var rootBone) && InPrefab(rootBone) ? SameFileRef(rootBone) : NullReference());
            node.Add("m_HasTransformHierarchy", 1);

            var bones = new YAMLSequenceNode();
            foreach (var bone in smr.m_Bones ?? new List<PPtr<Transform>>())
            {
                bones.Add(bone.TryGet(out var t) && InPrefab(t) ? SameFileRef(t) : NullReference());
            }
            node.Add("m_Bones", bones);
            return doc;
        }

        private YAMLDocument AnimatorDoc(Animator animator)
        {
            var doc = new YAMLDocument();
            var root = doc.CreateMappingRoot();
            root.Tag = "95";
            root.Anchor = animator.m_PathID.ToString();

            var node = new YAMLMappingNode();
            root.Add("Animator", node);
            node.Add("m_ObjectHideFlags", 0);
            node.Add("m_CorrespondingSourceObject", NullReference());
            node.Add("m_PrefabInstance", NullReference());
            node.Add("m_PrefabAsset", NullReference());
            node.Add("m_GameObject", animator.m_GameObject.TryGet(out var go) ? SameFileRef(go) : NullReference());
            node.Add("m_Enabled", 1);
            // ★ 之前这里硬编码成 NullReference ⇒ prefab 的 avatar 永远是空的。
            //   JSON 导出证实源数据里 m_Avatar 是**真实 PPtr**（SK_actor_chen_01Avatar），
            //   所以必须走 Reference 让它把 Avatar 资源一起拉进来并把 guid 写上。
            node.Add("m_Avatar", Reference(animator.m_Avatar));
            node.Add("m_Controller", Reference(animator.m_Controller));
            node.Add("m_CullingMode", 0);
            node.Add("m_UpdateMode", 0);
            node.Add("m_ApplyRootMotion", 0);
            node.Add("m_LinearVelocityBlending", 0);
            node.Add("m_WarningMessage", string.Empty);
            // ★ 必须写 1：源数据里是 false，但那个 false 表示「运行时被优化过」。
            //   我们导出的 prefab 是**带完整 Transform 层级**的（462 个节点），
            //   若照抄 0，Unity 会认为这是 Optimize Game Objects 过的模型并报
            //   "Editing and playback of animations on optimized game object hierarchy is not supported."
            node.Add("m_HasTransformHierarchy", 1);
            node.Add("m_AllowConstantClipSamplingOptimization", 0);
            node.Add("m_KeepAnimatorControllerStateOnDisable", 0);
            return doc;
        }

        private YAMLDocument AnimationDoc(Animation animation)
        {
            var doc = new YAMLDocument();
            var root = doc.CreateMappingRoot();
            root.Tag = "111";
            root.Anchor = "7400000";     // AnimationClip 主对象 ID

            var node = new YAMLMappingNode();
            root.Add("Animation", node);
            node.Add("m_ObjectHideFlags", 0);
            node.Add("m_CorrespondingSourceObject", NullReference());
            node.Add("m_PrefabInstance", NullReference());
            node.Add("m_PrefabAsset", NullReference());
            node.Add("m_GameObject", animation.m_GameObject.TryGet(out var go) ? SameFileRef(go) : NullReference());
            node.Add("m_Enabled", 1);
            node.Add("m_Animation", NullReference());
            var clips = new YAMLSequenceNode();
            foreach (var clip in animation.m_Animations ?? new List<PPtr<AnimationClip>>())
            {
                clips.Add(Reference(clip));
            }
            node.Add("m_Animations", clips);
            return doc;
        }
    }
}
