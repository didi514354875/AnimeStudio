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
                        text = ((Shader)asset).Convert();
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

        private static void WriteMeta(string path, string guid)
        {
            File.WriteAllText(path + ".meta", "fileFormatVersion: 2\nguid: " + guid + "\n");
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

            // text assets written by the converters use fixed main-object fileIDs
            long fileID = target switch
            {
                AnimationClip => 7400000,
                Shader => 4800000,
                _ => entry.FileID,
            };
            var node = new YAMLMappingNode(MappingStyle.Flow);
            node.Add("fileID", fileID);
            node.Add("guid", entry.Guid);
            node.Add("type", 2);
            return node;
        }

        private static bool Pullable(Object target)
        {
            return target is Material or Mesh or Texture2D or Sprite or Shader or AnimationClip or AudioClip;
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
            return sw.ToString();
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
        //  Material (.mat)
        // ------------------------------------------------------------------

        private string MaterialToYaml(Material material)
        {
            var doc = new YAMLDocument();
            var root = doc.CreateMappingRoot();
            root.Tag = "21";
            root.Anchor = material.m_PathID.ToString();

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

        private string MeshToYaml(Mesh mesh)
        {
            var doc = new YAMLDocument();
            var root = doc.CreateMappingRoot();
            root.Tag = "43";
            root.Anchor = mesh.m_PathID.ToString();

            var m = new YAMLMappingNode();
            root.Add("Mesh", m);
            m.Add("m_ObjectHideFlags", 0);
            m.Add("m_CorrespondingSourceObject", NullReference());
            m.Add("m_PrefabInstance", NullReference());
            m.Add("m_PrefabAsset", NullReference());
            m.Add("m_Name", SanitizeName(mesh.m_Name));
            m.Add("serializedVersion", 11);

            var subMeshes = new YAMLSequenceNode();
            foreach (var sub in mesh.m_SubMeshes ?? new List<SubMesh>())
            {
                var subNode = new YAMLMappingNode();
                subMeshes.Add(subNode);
                subNode.Add("serializedVersion", 2);
                subNode.Add("firstByte", sub.firstByte);
                subNode.Add("indexCount", sub.indexCount);
                subNode.Add("topology", (int)sub.topology);
                subNode.Add("triangleCount", sub.topology == GfxPrimitiveType.Triangles ? sub.indexCount / 3 : 0);
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

            m.Add("m_Bones", new YAMLSequenceNode());
            m.Add("m_IndexBuffer", HexIndices(mesh.m_IndexBuffer ?? Array.Empty<uint>(), mesh.m_Use16BitIndices));

            var skin = new YAMLSequenceNode();
            foreach (var w in mesh.m_Skin ?? new List<BoneWeights4>())
            {
                var wNode = new YAMLMappingNode();
                skin.Add(wNode);
                wNode.Add("serializedVersion", 2);
                wNode.Add("weight", Vector4Node(w.weight[0], w.weight[1], w.weight[2], w.weight[3]));
                wNode.Add("boneIndex", Vector4Node(w.boneIndex[0], w.boneIndex[1], w.boneIndex[2], w.boneIndex[3]));
            }
            m.Add("m_Skin", skin);

            var vd = mesh.m_VertexData;
            var vdNode = new YAMLMappingNode();
            m.Add("m_VertexData", vdNode);
            vdNode.Add("serializedVersion", 3);
            var channelsNode = new YAMLSequenceNode();
            foreach (var ch in vd?.m_Channels ?? new List<ChannelInfo>())
            {
                var chNode = new YAMLMappingNode();
                channelsNode.Add(chNode);
                chNode.Add("stream", ch.stream);
                chNode.Add("offset", ch.offset);
                chNode.Add("format", ch.format);
                chNode.Add("dimension", ch.dimension);
            }
            vdNode.Add("m_Channels", channelsNode);
            vdNode.Add("m_VertexCount", vd?.m_VertexCount ?? 0);
            vdNode.Add("m_DataSize", Hex(vd?.m_DataSize));

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

            var metrics = new YAMLMappingNode();
            m.Add("m_Metrics", metrics);
            metrics.Add("m_VertexAllocated", 0);
            metrics.Add("m_VertexCount", 0);
            metrics.Add("m_IndexAllocated", 0);
            metrics.Add("m_IndexCount", 0);
            metrics.Add("m_MeshMemory", 0);
            metrics.Add("m_MeshOther", 0);

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
            node.Add("m_Avatar", NullReference());
            node.Add("m_Controller", Reference(animator.m_Controller));
            node.Add("m_CullingMode", 0);
            node.Add("m_UpdateMode", 0);
            node.Add("m_ApplyRootMotion", 0);
            node.Add("m_LinearVelocityBlending", 0);
            node.Add("m_WarningMessage", string.Empty);
            node.Add("m_HasTransformHierarchy", animator.m_HasTransformHierarchy ? 1 : 0);
            node.Add("m_AllowConstantClipSamplingOptimization", 0);
            node.Add("m_KeepAnimatorControllerStateOnDisable", 0);
            return doc;
        }

        private YAMLDocument AnimationDoc(Animation animation)
        {
            var doc = new YAMLDocument();
            var root = doc.CreateMappingRoot();
            root.Tag = "111";
            root.Anchor = animation.m_PathID.ToString();

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
