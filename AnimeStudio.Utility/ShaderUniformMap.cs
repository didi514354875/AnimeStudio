using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.RegularExpressions;

namespace AnimeStudio
{
    /// <summary>
    /// Per-Pass binding context used to give decompiled GLSL real uniform names.
    ///
    /// Compiled shaders strip every debug name, so spirv-cross prints anonymous
    /// identifiers: uniform blocks are <c>_15_16 { ... } _16;</c> with members
    /// <c>_m0, _m1, ...</c> and textures are <c>_60</c>.  The serialized shader,
    /// however, knows exactly which resource sits at which binding and which
    /// member lives at which byte offset of a constant buffer:
    ///
    /// <list type="bullet">
    /// <item><see cref="SerializedPass.m_NameIndices"/> — the pass wide name table,</item>
    /// <item><see cref="SerializedProgramParameters.m_DescriptorSetParams"/> —
    /// (descriptor set, binding) → name, the Vulkan layout,</item>
    /// <item><see cref="SerializedProgramParameters.m_ConstantBufferBindings"/> /
    /// <c>m_TextureParams</c> / <c>m_BufferParams</c> — packed register words
    /// <c>(flags &lt;&lt; 24) | (set &lt;&lt; 16) | binding</c>,</item>
    /// <item><see cref="SerializedProgramParameters.m_ConstantBuffers"/> —
    /// cbuffer layouts (member name index → byte offset), the material properties
    /// of <c>UnityPerMaterial</c> included.</item>
    /// </list>
    ///
    /// Build one instance per (Pass, stage) via <see cref="Build"/>; a null result
    /// simply means "no names known, leave the GLSL untouched".
    /// </summary>
    public class ShaderBindingContext
    {
        /// <summary>Resource kind of a GLSL declaration, used to disambiguate flat
        /// binding numbers (a cbuffer and a texture may share one across sets).</summary>
        public enum ResourceKind
        {
            Cbuffer,
            Buffer,
            Texture,
            Sampler,
        }

        /// <summary>(descriptor set &lt;&lt; 24) | binding → resource name, from every
        /// binding source that could be recovered from the serialized shader.</summary>
        public readonly Dictionary<int, string> BindingNames = new Dictionary<int, string>();

        /// <summary>Kind-specific flat binding → name.  Only consulted when the SPIR-V
        /// carries no usable set, and only trusted when unambiguous.</summary>
        public readonly Dictionary<int, string> CbufferFlat = new Dictionary<int, string>();
        public readonly Dictionary<int, string> BufferFlat = new Dictionary<int, string>();
        public readonly Dictionary<int, string> TextureFlat = new Dictionary<int, string>();
        public readonly Dictionary<int, string> SamplerFlat = new Dictionary<int, string>();

        /// <summary>Binding number → resource name, ignoring descriptor sets and kinds.
        /// Legacy fallback for modules without DescriptorSet decorations.</summary>
        public readonly Dictionary<int, string> FlatBindingNames = new Dictionary<int, string>();

        /// <summary>Cbuffer name → (byte offset in that cbuffer → member name).</summary>
        public readonly Dictionary<string, Dictionary<int, string>> CbufferMembers = new Dictionary<string, Dictionary<int, string>>();

        /// <summary>Cbuffers that no binding source claims (runtime-bound material
        /// constants such as UnityPerMaterial), name → sorted member byte offsets.
        /// Matched against the SPIR-V block's own member offsets as a last resort.</summary>
        public readonly Dictionary<string, int[]> UnboundCbufferSignatures = new Dictionary<string, int[]>();

        public bool HasBindings => BindingNames.Count > 0 || CbufferFlat.Count > 0 || TextureFlat.Count > 0 || BufferFlat.Count > 0;

        /// <summary>Shader wide union of every pass' bindings, consulted when this
        /// pass' own parameters do not describe the set a module references.</summary>
        public ShaderBindingContext Fallback;

        /// <summary>Lookup by (set, binding).  When the shader knows no sets, <paramref name="set"/> is 0.</summary>
        public bool TryGetName(int set, int binding, out string name)
        {
            return BindingNames.TryGetValue((set << 24) | binding, out name);
        }

        /// <summary>Kind-aware flat lookup, rejecting ambiguous entries (two resources
        /// of the same kind behind one flat binding number).</summary>
        public bool TryGetKindFlat(ResourceKind kind, int binding, out string name)
        {
            name = null;
            var map = kind switch
            {
                ResourceKind.Cbuffer => CbufferFlat,
                ResourceKind.Buffer => BufferFlat,
                ResourceKind.Texture => TextureFlat,
                ResourceKind.Sampler => SamplerFlat,
                _ => null,
            };
            return map != null && map.TryGetValue(binding, out name) && name != null;
        }

        /// <summary>
        /// Pick the one unbound cbuffer whose member byte offsets equal
        /// <paramref name="signature"/> (the SPIR-V block's own offsets), or null.
        /// </summary>
        public string MatchUnboundCbuffer(int[] signature)
        {
            if (signature == null || signature.Length == 0)
            {
                return null;
            }
            string match = null;
            foreach (var pair in UnboundCbufferSignatures)
            {
                if (pair.Value.Length == signature.Length)
                {
                    int i = 0;
                    while (i < signature.Length && pair.Value[i] == signature[i])
                    {
                        i++;
                    }
                    if (i == signature.Length)
                    {
                        if (match != null)
                        {
                            return null; // two cbuffers with the same layout: ambiguous
                        }
                        match = pair.Key;
                    }
                }
            }
            return match;
        }

        /// <summary>Vulkan binding words pack <c>(flags &lt;&lt; 24) | (set &lt;&lt; 16) | binding</c>
        /// (observed: 0x0D020000 = set 2 binding 0 for UnityInstancing).</summary>
        private static int SetOf(uint packedIndex) => (int)((packedIndex >> 16) & 0xFF);

        private static int BindingOf(uint packedIndex) => (int)(packedIndex & 0xFFFF);

        /// <summary>
        /// Merge every binding of <paramref name="source"/> into
        /// <paramref name="target"/> (first name wins; conflicting names behind one
        /// key are nulled out so lookups fail instead of guessing).
        /// </summary>
        public static void Merge(ShaderBindingContext source, ShaderBindingContext target)
        {
            void MergeMap<T>(Dictionary<int, T> from, Dictionary<int, T> to)
            {
                foreach (var kv in from)
                {
                    if (!to.TryGetValue(kv.Key, out var existing))
                    {
                        to[kv.Key] = kv.Value;
                    }
                    else if (!Equals(existing, kv.Value))
                    {
                        to[kv.Key] = default;
                    }
                }
            }

            MergeMap(source.BindingNames, target.BindingNames);
            MergeMap(source.CbufferFlat, target.CbufferFlat);
            MergeMap(source.BufferFlat, target.BufferFlat);
            MergeMap(source.TextureFlat, target.TextureFlat);
            MergeMap(source.SamplerFlat, target.SamplerFlat);
            MergeMap(source.FlatBindingNames, target.FlatBindingNames);
            foreach (var kv in source.CbufferMembers)
            {
                if (!target.CbufferMembers.ContainsKey(kv.Key))
                {
                    target.CbufferMembers[kv.Key] = kv.Value;
                }
            }
            foreach (var kv in source.UnboundCbufferSignatures)
            {
                if (!target.UnboundCbufferSignatures.ContainsKey(kv.Key))
                {
                    target.UnboundCbufferSignatures[kv.Key] = kv.Value;
                }
            }
        }

        public static ShaderBindingContext Build(List<KeyValuePair<string, int>> nameIndices, SerializedProgramParameters parameters)
        {
            if (nameIndices == null || nameIndices.Count == 0 || parameters == null)
            {
                return null;
            }

            var names = new Dictionary<int, string>();
            foreach (var kv in nameIndices)
            {
                if (!string.IsNullOrEmpty(kv.Key))
                {
                    names[kv.Value] = kv.Key;
                }
            }
            if (names.Count == 0)
            {
                return null;
            }

            var context = new ShaderBindingContext();
            var boundCbuffers = new HashSet<string>();

            void AddFlat(ResourceKind kind, int binding, string name)
            {
                var map = kind switch
                {
                    ResourceKind.Cbuffer => context.CbufferFlat,
                    ResourceKind.Buffer => context.BufferFlat,
                    ResourceKind.Texture => context.TextureFlat,
                    ResourceKind.Sampler => context.SamplerFlat,
                    _ => null,
                };
                if (map == null)
                {
                    return;
                }
                if (map.TryGetValue(binding, out var existing) && existing != null && existing != name)
                {
                    // ambiguous flat binding: null it out so lookups fail instead of guessing
                    map[binding] = null;
                    return;
                }
                map[binding] = name;
            }

            // 2021.3 player builds ship the Vulkan descriptor set layout; its (set,
            // binding) pairs are what the SPIR-V decorations carry.
            if (parameters.m_DescriptorSetParams != null)
            {
                foreach (var set in parameters.m_DescriptorSetParams)
                {
                    if (set?.m_SetBindings == null)
                    {
                        continue;
                    }
                    if (Environment.GetEnvironmentVariable("ANIMESTUDIO_SHADER_DEBUG") == "1")
                    {
                        foreach (var dbg in set.m_SetBindings)
                        {
                            Console.Error.WriteLine($"[uniform-debug] BUILD set={set.m_SetId} binding={dbg?.m_BindingIndex} nameIdx={dbg?.m_NameIndex} -> {names.GetValueOrDefault(dbg?.m_NameIndex ?? -1)}");
                        }
                    }
                    foreach (var binding in set.m_SetBindings)
                    {
                        if (binding == null || !names.TryGetValue(binding.m_NameIndex, out var name))
                        {
                            continue;
                        }
                        context.BindingNames[(set.m_SetId << 24) | binding.m_BindingIndex] = name;
                        context.FlatBindingNames[binding.m_BindingIndex] = name;
                        // DescriptorType: 0 = sampler, 2 = sampled image, 6 = uniform buffer.
                        var kind = binding.m_DescriptorType switch
                        {
                            0 => ResourceKind.Sampler,
                            6 => ResourceKind.Cbuffer,
                            _ => ResourceKind.Texture,
                        };
                        if (kind == ResourceKind.Cbuffer)
                        {
                            boundCbuffers.Add(name);
                        }
                        AddFlat(kind, binding.m_BindingIndex, name);
                    }
                }
            }

            // Register/texture/buffer bindings are packed (flags << 24) | (set << 16) |
            // binding for Vulkan; older builds pack (space << 24) | binding.  The set
            // aware decode is stored first so it wins over the legacy one.
            if (parameters.m_ConstantBufferBindings != null)
            {
                foreach (var b in parameters.m_ConstantBufferBindings)
                {
                    if (b == null || !names.TryGetValue(b.m_NameIndex, out var name))
                    {
                        continue;
                    }
                    boundCbuffers.Add(name);
                    context.BindingNames[(SetOf((uint)b.m_Index) << 24) | BindingOf((uint)b.m_Index)] = name;
                    context.BindingNames[b.m_Index & 0xFFFFFF] = name;
                    context.FlatBindingNames[b.m_Index & 0xFFFFFF] = name;
                    AddFlat(ResourceKind.Cbuffer, b.m_Index & 0xFFFFFF, name);
                }
            }
            if (parameters.m_TextureParams != null)
            {
                foreach (var t in parameters.m_TextureParams)
                {
                    if (t == null || !names.TryGetValue(t.m_NameIndex, out var name))
                    {
                        continue;
                    }
                    context.BindingNames[(SetOf((uint)t.m_Index) << 24) | BindingOf((uint)t.m_Index)] = name;
                    context.BindingNames[t.m_Index & 0xFFFFFF] = name;
                    context.FlatBindingNames[t.m_Index & 0xFFFFFF] = name;
                    AddFlat(ResourceKind.Texture, t.m_Index & 0xFFFFFF, name);
                }
            }
            if (parameters.m_BufferParams != null)
            {
                foreach (var b in parameters.m_BufferParams)
                {
                    if (b == null || !names.TryGetValue(b.m_NameIndex, out var name))
                    {
                        continue;
                    }
                    context.BindingNames[(SetOf((uint)b.m_Index) << 24) | BindingOf((uint)b.m_Index)] = name;
                    context.BindingNames[b.m_Index & 0xFFFFFF] = name;
                    context.FlatBindingNames[b.m_Index & 0xFFFFFF] = name;
                    AddFlat(ResourceKind.Buffer, b.m_Index & 0xFFFFFF, name);
                }
            }
            if (parameters.m_UAVParams != null)
            {
                foreach (var u in parameters.m_UAVParams)
                {
                    if (u == null || !names.TryGetValue(u.m_NameIndex, out var name))
                    {
                        continue;
                    }
                    context.BindingNames[u.m_Index & 0xFFFFFF] = name;
                    context.FlatBindingNames[u.m_Index & 0xFFFFFF] = name;
                    AddFlat(ResourceKind.Buffer, u.m_Index & 0xFFFFFF, name);
                }
            }

            if (parameters.m_ConstantBuffers != null)
            {
                foreach (var cb in parameters.m_ConstantBuffers)
                {
                    if (cb == null || !names.TryGetValue(cb.m_NameIndex, out var cbName))
                    {
                        continue;
                    }

                    var members = new Dictionary<int, string>();
                    if (cb.m_MatrixParams != null)
                    {
                        foreach (var m in cb.m_MatrixParams)
                        {
                            if (m != null && names.TryGetValue(m.m_NameIndex, out var name))
                            {
                                members[m.m_Index] = name;
                            }
                        }
                    }
                    if (cb.m_VectorParams != null)
                    {
                        foreach (var v in cb.m_VectorParams)
                        {
                            if (v != null && names.TryGetValue(v.m_NameIndex, out var name))
                            {
                                members[v.m_Index] = name;
                            }
                        }
                    }
                    // StructParameter keeps its base offset only as a constructor local, so
                    // nested struct members cannot be flattened here; scalar members of the
                    // cbuffer itself (the material properties) are all that matters.
                    if (members.Count > 0)
                    {
                        context.CbufferMembers[cbName] = members;
                        if (!boundCbuffers.Contains(cbName))
                        {
                            // Runtime-bound (UnityPerMaterial and friends): remembered by
                            // layout so a GLSL block with the same member offsets can be
                            // identified even when its descriptor set is unknown.
                            context.UnboundCbufferSignatures[cbName] = members.Keys.OrderBy(x => x).ToArray();
                        }
                    }
                }
            }

            return context.HasBindings ? context : null;
        }
    }

    /// <summary>
    /// The handful of SPIR-V decorations needed to map GLSL block members back to
    /// cbuffer offsets: <c>OpDecorate Binding/DescriptorSet</c>,
    /// <c>OpMemberDecorate Offset</c>, <c>OpVariable</c> and the pointer/array type
    /// hops.  Debug names are stripped from shipped shaders, so ids are the only
    /// correlation available.  Decoration enums per SPIR-V 1.4:
    /// Binding = 33, DescriptorSet = 34, Offset = 35.
    /// </summary>
    public class SpiResourceMap
    {
        public readonly Dictionary<uint, int> BindingOf = new Dictionary<uint, int>();
        public readonly Dictionary<uint, int> SetOf = new Dictionary<uint, int>();
        public readonly Dictionary<uint, Dictionary<uint, int>> MemberOffsets = new Dictionary<uint, Dictionary<uint, int>>();
        public readonly Dictionary<uint, uint> PointerTypeOf = new Dictionary<uint, uint>();
        public readonly Dictionary<uint, uint> PointeeOf = new Dictionary<uint, uint>();

        private const uint DecorationBinding = 33;
        private const uint DecorationDescriptorSet = 34;
        private const uint DecorationOffset = 35;

        public static SpiResourceMap Parse(byte[] spirv)
        {
            var map = new SpiResourceMap();
            if (spirv == null || spirv.Length < 20 || (spirv.Length & 3) != 0)
            {
                return map;
            }

            // Use the full SpirV grammar parser instead of a hand rolled word walk:
            // a misaligned walk silently drops decorations and produces wrong names.
            try
            {
                using (var stream = new MemoryStream(spirv))
                {
                    var module = SpirV.Module.ReadFrom(stream);
                    foreach (var ins in module.Instructions)
                    {
                        var words = ins.Words;
                        switch (ins.Instruction.Name)
                        {
                            case "OpTypePointer": // <id> result, storage class, <id> type
                                if (words.Count >= 4)
                                {
                                    map.PointeeOf[words[1]] = words[3];
                                }
                                break;
                            case "OpTypeArray": // <id> result, <id> element type, <id> length
                                if (words.Count >= 3)
                                {
                                    map.PointeeOf[words[1]] = words[2];
                                }
                                break;
                            case "OpVariable": // <id> result type, <id> result, storage class
                                if (words.Count >= 3)
                                {
                                    map.PointerTypeOf[words[2]] = words[1];
                                }
                                break;
                            case "OpDecorate": // <id> target, decoration, [literal]
                                if (words.Count >= 4)
                                {
                                    if (words[2] == DecorationBinding)
                                    {
                                        map.BindingOf[words[1]] = (int)words[3];
                                    }
                                    else if (words[2] == DecorationDescriptorSet)
                                    {
                                        map.SetOf[words[1]] = (int)words[3];
                                    }
                                }
                                break;
                            case "OpMemberDecorate": // <id> type, member, decoration, [literal]
                                if (words.Count >= 5 && words[3] == DecorationOffset)
                                {
                                    if (!map.MemberOffsets.TryGetValue(words[1], out var offsets))
                                    {
                                        offsets = new Dictionary<uint, int>();
                                        map.MemberOffsets[words[1]] = offsets;
                                    }
                                    offsets[words[2]] = (int)words[4];
                                }
                                break;
                        }
                    }
                }
            }
            catch
            {
                // An unparseable module simply gets no renames (conservative).
                map.BindingOf.Clear();
                map.SetOf.Clear();
                map.MemberOffsets.Clear();
                map.PointerTypeOf.Clear();
                map.PointeeOf.Clear();
            }

            return map;
        }

        /// <summary>
        /// Descriptor set + binding the given variable id is decorated with, when both
        /// decorations are present (the flat printed `binding = N` loses the set).
        /// </summary>
        public bool TryGetSetBinding(uint variableId, out int set, out int binding)
        {
            set = 0;
            binding = 0;
            return SetOf.TryGetValue(variableId, out set) && BindingOf.TryGetValue(variableId, out binding);
        }

        /// <summary>
        /// Struct type id the given block variable points at, following the
        /// pointer (and possible array) hops, or 0 when it cannot be told.
        /// </summary>
        public uint BlockTypeOf(uint variableId)
        {
            if (!PointerTypeOf.TryGetValue(variableId, out var type))
            {
                return 0;
            }
            for (int hops = 0; hops < 3 && PointeeOf.TryGetValue(type, out var inner); hops++)
            {
                type = inner;
                if (MemberOffsets.ContainsKey(type))
                {
                    return type;
                }
            }
            return MemberOffsets.ContainsKey(type) ? type : (uint)0;
        }

        /// <summary>
        /// Sorted member byte offsets of the struct the given variable points at,
        /// or null when the module does not provide them.
        /// </summary>
        public int[] MemberOffsetSignature(uint variableId)
        {
            var structType = BlockTypeOf(variableId);
            if (structType == 0 || !MemberOffsets.TryGetValue(structType, out var offsets) || offsets.Count == 0)
            {
                return null;
            }
            return offsets.Values.OrderBy(x => x).ToArray();
        }
    }

    /// <summary>
    /// Rewrites anonymous spirv-cross identifiers in decompiled GLSL to the names
    /// the serialized shader carries: uniform blocks become
    /// <c>uniform UnityPerMaterial { vec4 _BaseColor; ... } UnityPerMaterial;</c>,
    /// members are resolved through their byte offsets, and texture uniforms pick
    /// up their property names.  Everything that cannot be resolved confidently is
    /// left untouched.
    /// </summary>
    public static class GlslUniformRenamer
    {
        private static readonly Regex BlockRegex = new Regex(
            @"(?<prefix>layout\s*\(\s*(?:set\s*=\s*\d+\s*,\s*)?binding\s*=\s*(?<binding>\d+)(?:\s*,\s*set\s*=\s*\d+)?[^)]*\)\s*" +
            @"(?<qual>readonly\s+|writeonly\s+)?(?<kind>uniform|buffer)\s+)" +
            @"(?<block>[A-Za-z_]\w*)\s*\{(?<body>[^{}]*)\}\s*(?<instance>[A-Za-z_]\w*)(?<iarr>\[[^\]]*\])?\s*;",
            RegexOptions.Compiled);

        private static readonly Regex OpaqueRegex = new Regex(
            @"(?<prefix>layout\s*\(\s*(?:set\s*=\s*\d+\s*,\s*)?binding\s*=\s*(?<binding>\d+)(?:\s*,\s*set\s*=\s*\d+)?[^)]*\)\s*" +
            @"uniform\s+(?<type>[A-Za-z_]\w*)\s+)" +
            @"(?<name>[A-Za-z_]\w*)(?<arr>\[[^\]]*\])?\s*;",
            RegexOptions.Compiled);

        private static readonly Regex BodyMemberRegex = new Regex(@"\b_m(?<idx>\d+)\b", RegexOptions.Compiled);
        private static readonly Regex AccessRegex = new Regex(@"\b(?<inst>[A-Za-z_]\w*)\._m(?<idx>\d+)\b", RegexOptions.Compiled);
        private static readonly Regex GeneratedNameRegex = new Regex(@"^_\d+$", RegexOptions.Compiled);

        public static string Rename(string glsl, SpiResourceMap spi, ShaderBindingContext context)
        {
            if (string.IsNullOrEmpty(glsl) || spi == null || context == null || !context.HasBindings)
            {
                return glsl;
            }

            // instance token -> resolved member names (member index -> name)
            var accessMaps = new Dictionary<string, Dictionary<int, string>>();
            // instance token -> real resource name, applied to the whole text afterwards
            var instanceNames = new Dictionary<string, string>();

            glsl = BlockRegex.Replace(glsl, match =>
            {
                if (!int.TryParse(match.Groups["binding"].Value, out var binding))
                {
                    return match.Value;
                }
                var kind = match.Groups["kind"].Value == "buffer"
                    ? ShaderBindingContext.ResourceKind.Buffer
                    : ShaderBindingContext.ResourceKind.Cbuffer;
                var instance = match.Groups["instance"].Value;
                var realName = ResolveResourceName(spi, context, instance, binding);
                if (realName == null)
                {
                    return match.Value;
                }

                var body = match.Groups["body"].Value;

                if (kind == ShaderBindingContext.ResourceKind.Cbuffer &&
                    context.CbufferMembers.TryGetValue(realName, out var members))
                {
                    // Conservative: only rename a member when the module decorates it
                    // with a byte offset that exists in the serialized cbuffer layout.
                    var offsets = MemberOffsetsFor(spi, instance);
                    if (offsets == null)
                    {
                        return match.Value;
                    }

                    string Resolve(int index)
                    {
                        return offsets.TryGetValue(index, out var offset) &&
                               members.TryGetValue(offset, out var byOffset)
                            ? byOffset
                            : null;
                    }

                    var memberNames = new Dictionary<int, string>();
                    body = BodyMemberRegex.Replace(body, m =>
                    {
                        var name = Resolve(int.Parse(m.Groups["idx"].Value));
                        if (name == null)
                        {
                            return m.Value;
                        }
                        memberNames[int.Parse(m.Groups["idx"].Value)] = name;
                        return name;
                    });

                    if (memberNames.Count > 0 && !accessMaps.ContainsKey(instance))
                    {
                        accessMaps[instance] = memberNames;
                    }
                }

                if (!instanceNames.ContainsKey(instance))
                {
                    instanceNames[instance] = realName;
                }

                return match.Groups["prefix"].Value + realName + "{" + body + "}" + instance + match.Groups["iarr"].Value + ";";
            });

            // `instance._mK` member accesses, keyed by the original instance token.
            if (accessMaps.Count > 0)
            {
                glsl = AccessRegex.Replace(glsl, m =>
                {
                    var inst = m.Groups["inst"].Value;
                    return accessMaps.TryGetValue(inst, out var names) &&
                           int.TryParse(m.Groups["idx"].Value, out var index) &&
                           names.TryGetValue(index, out var name)
                        ? inst + "." + name
                        : m.Value;
                });
            }

            // Block instance tokens (in the trailing `} _16;` and any remaining
            // accesses) -> the real resource name.  Generated instance names are the
            // SPIR-V variable ids, so they cannot collide with any other identifier.
            foreach (var pair in instanceNames)
            {
                if (pair.Key != pair.Value && GeneratedNameRegex.IsMatch(pair.Key))
                {
                    glsl = ReplaceWord(glsl, pair.Key, pair.Value);
                }
            }

            // Opaque uniforms: `layout(binding = N) uniform sampler2D _60;`
            glsl = OpaqueRegex.Replace(glsl, match =>
            {
                var name = match.Groups["name"].Value;
                if (!GeneratedNameRegex.IsMatch(name) ||
                    !int.TryParse(match.Groups["binding"].Value, out var binding))
                {
                    return match.Value;
                }
                var realName = ResolveResourceName(spi, context, name, binding);
                return realName == null
                    ? match.Value
                    : match.Groups["prefix"].Value + realName + match.Groups["arr"].Value + ";";
            });

            return glsl;
        }

        /// <summary>
        /// Resolve the resource name of a GLSL declaration, strictly evidence based:
        /// the (descriptor set, binding) the SPIR-V module decorates the variable
        /// with must exist in the serialized shader's binding tables (set 0 is the
        /// SPIR-V default for an undecorated resource), or — for cbuffers — the
        /// block's member byte offsets must exactly equal one known layout.
        /// Anything else stays anonymous.
        /// </summary>
        private static string ResolveResourceName(SpiResourceMap spi, ShaderBindingContext context, string generatedName, int printedBinding)
        {
            if (!GeneratedNameRegex.IsMatch(generatedName))
            {
                return null;
            }
            uint id = uint.Parse(generatedName.Substring(1));
            if (!spi.BindingOf.TryGetValue(id, out var binding))
            {
                return null;
            }
            int set = spi.SetOf.TryGetValue(id, out var decoratedSet) ? decoratedSet : 0;

            if (context.TryGetName(set, binding, out var bySet))
            {
                return Sanitize(bySet);
            }
            // The pass' parameters may not describe every set its modules reference;
            // consult the shader wide union of all passes' bindings.
            for (var fallback = context.Fallback; fallback != null; fallback = fallback.Fallback)
            {
                if (fallback.TryGetName(set, binding, out var byUnion))
                {
                    return Sanitize(byUnion);
                }
            }

            // Cbuffer whose binding is not serialized (runtime-bound material
            // constants): accept an exact member byte offset layout match only.
            var signature = spi.MemberOffsetSignature(id);
            if (signature != null && signature.Length > 1)
            {
                var byLayout = context.MatchUnboundCbuffer(signature) ?? context.Fallback?.MatchUnboundCbuffer(signature);
                if (byLayout != null)
                {
                    return Sanitize(byLayout);
                }
            }

            if (Environment.GetEnvironmentVariable("ANIMESTUDIO_SHADER_DEBUG") == "1")
            {
                Console.Error.WriteLine($"[uniform-debug] unresolved %{id} set={set} binding={binding} printed={printedBinding}");
            }
            return null;
        }

        private static Dictionary<int, int> MemberOffsetsFor(SpiResourceMap spi, string instance)
        {
            if (spi == null || !GeneratedNameRegex.IsMatch(instance))
            {
                return null;
            }
            var structType = spi.BlockTypeOf(uint.Parse(instance.Substring(1)));
            return structType != 0 && spi.MemberOffsets.TryGetValue(structType, out var offsets)
                ? offsets.ToDictionary(kv => (int)kv.Key, kv => kv.Value)
                : null;
        }

        private static string ReplaceWord(string text, string from, string to)
        {
            return Regex.Replace(text, @"(?<![A-Za-z0-9_])" + Regex.Escape(from) + @"(?![A-Za-z0-9_])", to);
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }
            var result = new string(name.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
            return result.Length > 0 && !char.IsDigit(result[0]) ? result : null;
        }
    }
}
