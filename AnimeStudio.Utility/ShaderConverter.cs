using AnimeStudio.PInvoke;
using SharpGen.Runtime;
using SpirV;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Vortice.D3DCompiler;

namespace AnimeStudio
{
    public static class ShaderConverter
    {
        /// <summary>
        /// <c>ANIMESTUDIO_SHADER_VARIANTS=first</c> writes only the first keyword variant of
        /// each stage (and, for the classic layout, only the first blob index).  A keyword
        /// heavy shader can hold thousands of variants and the exported .shader grows with
        /// every one of them, so this trades completeness for a manageable file size.
        /// </summary>
        private static readonly bool FirstVariantOnly = string.Equals(
            Environment.GetEnvironmentVariable("ANIMESTUDIO_SHADER_VARIANTS"),
            "first", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 默认 <c>true</c>：只导 <c>Properties</c> + 占位实现（见 <see cref="ConvertPlaceholder"/>）。
        /// 设 <c>ANIMESTUDIO_SHADER_FULL=1</c> 可恢复导出原始程序反汇编的旧行为。
        /// </summary>
        public static readonly bool PlaceholderOnly = !string.Equals(
            Environment.GetEnvironmentVariable("ANIMESTUDIO_SHADER_FULL"),
            "1", StringComparison.OrdinalIgnoreCase);

        public static string Convert(this Shader shader)
        {
            if (shader.platformInfos != null)
            {
                return null;
            }
            if (shader.m_SubProgramBlob != null) //5.3 - 5.4
            {
                var decompressedBytes = new byte[shader.decompressedSize];
                var numWrite = LZ4.Instance.Decompress(shader.m_SubProgramBlob, decompressedBytes);
                if (numWrite != shader.decompressedSize)
                {
                    throw new IOException($"Lz4 decompression error, write {numWrite} bytes but expected {shader.decompressedSize} bytes");
                }
                using (var blobReader = new EndianBinaryReader(new MemoryStream(decompressedBytes), EndianType.LittleEndian))
                {
                    var program = new ShaderProgram(blobReader, shader);
                    program.Read(blobReader, 0);
                    return header + program.Export(Encoding.UTF8.GetString(shader.m_Script));
                }
            }

            // Live Endfield (m_EnableShaderLODStreaming): the compiled programs are not in
            // compressedBlob at all, they sit in subShaderBlobs, one blob per ShaderLOD.
            // Must be tested before compressedBlob, which parses as an empty array here.
            if (shader.subShaderBlobs != null && shader.subShaderBlobs.Count > 0)
            {
                return header + ConvertSerializedShader(shader);
            }

            if (shader.compressedBlob != null) //5.5 and up
            {
                return header + ConvertSerializedShader(shader);
            }

            return header + Encoding.UTF8.GetString(shader.m_Script);
        }

        /// <summary>
        /// 只导「属性 + 占位实现」的 .shader（默认路径）：
        ///   - <c>Properties</c> 块来自资源本体 —— 属性名 / 类型 / 默认值 / 显示名 / 特性全部保真
        ///   - <c>SubShader</c>/<c>Pass</c> 是一个通用 UNLIT 占位实现，**不是**原始 GPU 程序
        /// 目的：让材质能在 Unity 里正常导入（属性名、贴图、颜色对得上），模型可见可调。
        /// 不导原始实现的原因：.ab 里只有目标 GPU 的编译字节码，无法还原成可移植的 HLSL。
        /// 设 <c>ANIMESTUDIO_SHADER_FULL=1</c> 可恢复「原始程序反汇编」的旧行为。
        /// </summary>
        public static string ConvertPlaceholder(this Shader shader)
        {
            var pf = shader.m_ParsedForm;
            var name = pf?.m_Name;
            if (string.IsNullOrEmpty(name))
            {
                name = string.IsNullOrEmpty(shader.m_Name) ? "Hidden/AnimeStudio/Placeholder" : shader.m_Name;
            }

            var props = pf?.m_PropInfo?.m_Props;
            var texProp = PickProperty(props, SerializedPropertyType.Texture);
            var colProp = PickProperty(props, SerializedPropertyType.Color);

            var sb = new StringBuilder();
            sb.AppendLine("// ===========================================================================");
            sb.AppendLine("// 占位 Shader（由 AnimeStudio 生成）");
            sb.AppendLine("//   Properties : 来自资源本体（属性名 / 类型 / 默认值 / 显示名 / 特性 全部保真）");
            sb.AppendLine("//   SubShader   : **URP 默认光照占位实现**（主光+阴影 / SH 环境光 / 附加光 / 雾）");
            sb.AppendLine("//                 —— 原版实现只有目标 GPU 的编译字节码，无法还原；这里给一个能正常受光的等价外观");
            sb.AppendLine("//   不导原始实现的原因：.ab 里只有目标 GPU 的编译字节码，无法还原成可移植 HLSL");
            sb.AppendLine("// ===========================================================================");
            sb.Append($"Shader \"{name}\" {{\n");

            if (props != null && props.Count > 0)
            {
                sb.Append(ConvertSerializedProperties(pf.m_PropInfo));
            }
            else
            {
                sb.Append("Properties {\n}\n");
            }

            // ---------------- URP 默认光照模板 ----------------
            // 主光（含阴影）+ SH 环境光 + 附加光 + 雾；另有 ShadowCaster / DepthOnly 两个必要 pass。
            string texDecl = texProp != null
                ? $"    TEXTURE2D({texProp}); SAMPLER(sampler{texProp});\n    float4 {texProp}_ST;\n"
                : "";
            string colDecl = colProp != null ? $"    half4 {colProp};\n" : "";
            string uvExpr = texProp != null ? $"TRANSFORM_TEX(IN.uv, {texProp})" : "IN.uv";
            string albedoExpr;
            if (texProp != null && colProp != null)
            {
                albedoExpr = $"SAMPLE_TEXTURE2D({texProp}, sampler{texProp}, IN.uv).rgb * {colProp}.rgb";
            }
            else if (texProp != null)
            {
                albedoExpr = $"SAMPLE_TEXTURE2D({texProp}, sampler{texProp}, IN.uv).rgb";
            }
            else if (colProp != null)
            {
                albedoExpr = $"{colProp}.rgb";
            }
            else
            {
                albedoExpr = "half3(0.8, 0.8, 0.8)";
            }

            sb.Append("SubShader {\n");
            sb.Append("  Tags { \"RenderType\" = \"Opaque\" \"Queue\" = \"Geometry\" \"RenderPipeline\" = \"UniversalPipeline\" }\n");
            sb.Append("  LOD 100\n");
            sb.Append("  Pass {\n");
            sb.Append("    Name \"ForwardLit\"\n");
            sb.Append("    Tags { \"LightMode\" = \"UniversalForward\" }\n");
            sb.Append("    Cull Back\n");
            sb.Append("    ZWrite On\n");
            sb.Append("    HLSLPROGRAM\n");
            sb.Append("    #pragma vertex vert\n");
            sb.Append("    #pragma fragment frag\n");
            sb.Append("    #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE\n");
            sb.Append("    #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS\n");
            sb.Append("    #pragma multi_compile_fragment _ _SHADOWS_SOFT\n");
            sb.Append("    #pragma multi_compile_fog\n");
            sb.Append("    #include \"Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl\"\n");
            sb.Append("    #include \"Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl\"\n\n");
            sb.Append(texDecl);
            sb.Append(colDecl);
            sb.Append("\n");
            sb.Append("    struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; };\n");
            sb.Append("    struct Varyings { float4 positionHCS : SV_POSITION; float2 uv : TEXCOORD0; float3 normalWS : TEXCOORD1; float3 positionWS : TEXCOORD2; float fogFactor : TEXCOORD3; };\n\n");
            sb.Append("    Varyings vert (Attributes IN) {\n");
            sb.Append("        Varyings OUT;\n");
            sb.Append("        VertexPositionInputs p = GetVertexPositionInputs(IN.positionOS.xyz);\n");
            sb.Append("        OUT.positionHCS = p.positionCS;\n");
            sb.Append("        OUT.positionWS = p.positionWS;\n");
            sb.Append("        OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);\n");
            sb.Append($"        OUT.uv = {uvExpr};\n");
            sb.Append("        OUT.fogFactor = ComputeFogFactor(p.positionCS.z);\n");
            sb.Append("        return OUT;\n");
            sb.Append("    }\n\n");
            sb.Append("    half4 frag (Varyings IN) : SV_Target {\n");
            sb.Append("        half3 albedo = " + albedoExpr + ";\n");
            sb.Append("        float3 normalWS = normalize(IN.normalWS);\n\n");
            sb.Append("        half3 lighting = SampleSH(normalWS);                       // 环境光（球谐）\n");
            sb.Append("        float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);\n");
            sb.Append("        Light mainLight = GetMainLight(shadowCoord);\n");
            sb.Append("        lighting += mainLight.color * (saturate(dot(normalWS, mainLight.direction)) * mainLight.shadowAttenuation);\n");
            sb.Append("        #ifdef _ADDITIONAL_LIGHTS\n");
            sb.Append("        uint lightCount = GetAdditionalLightsCount();\n");
            sb.Append("        for (uint li = 0u; li < lightCount; ++li) {\n");
            sb.Append("            Light addLight = GetAdditionalLight(li, IN.positionWS);\n");
            sb.Append("            lighting += addLight.color * (saturate(dot(normalWS, addLight.direction))\n");
            sb.Append("                        * addLight.distanceAttenuation * addLight.shadowAttenuation);\n");
            sb.Append("        }\n");
            sb.Append("        #endif\n\n");
            sb.Append("        half3 color = albedo * lighting;\n");
            sb.Append("        color = MixFog(color, IN.fogFactor);\n");
            sb.Append("        return half4(color, 1);\n");
            sb.Append("    }\n");
            sb.Append("    ENDHLSL\n");
            sb.Append("  }\n\n");
            // 阴影投射（URP 需要）
            sb.Append("  Pass {\n");
            sb.Append("    Name \"ShadowCaster\"\n");
            sb.Append("    Tags { \"LightMode\" = \"ShadowCaster\" }\n");
            sb.Append("    ZWrite On ZTest LEqual ColorMask 0\n");
            sb.Append("    Cull Back\n");
            sb.Append("    HLSLPROGRAM\n");
            sb.Append("    #pragma vertex shadowVert\n");
            sb.Append("    #pragma fragment shadowFrag\n");
            sb.Append("    #include \"Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl\"\n");
            sb.Append("    #include \"Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl\"\n");
            sb.Append("    float3 _LightDirection;\n\n");
            sb.Append("    struct SAttrs { float4 positionOS : POSITION; float3 normalOS : NORMAL; };\n");
            sb.Append("    struct SVary { float4 positionCS : SV_POSITION; };\n\n");
            sb.Append("    SVary shadowVert (SAttrs IN) {\n");
            sb.Append("        SVary OUT;\n");
            sb.Append("        float3 posWS = TransformObjectToWorld(IN.positionOS.xyz);\n");
            sb.Append("        float3 nrmWS = TransformObjectToWorldNormal(IN.normalOS);\n");
            sb.Append("        OUT.positionCS = TransformWorldToHClip(ApplyShadowBias(posWS, nrmWS, _LightDirection));\n");
            sb.Append("        return OUT;\n");
            sb.Append("    }\n");
            sb.Append("    half4 shadowFrag (SVary IN) : SV_Target { return 0; }\n");
            sb.Append("    ENDHLSL\n");
            sb.Append("  }\n\n");
            // 深度（URP 的 DepthPrepass / SSAO 需要）
            sb.Append("  Pass {\n");
            sb.Append("    Name \"DepthOnly\"\n");
            sb.Append("    Tags { \"LightMode\" = \"DepthOnly\" }\n");
            sb.Append("    ZWrite On ColorMask R\n");
            sb.Append("    Cull Back\n");
            sb.Append("    HLSLPROGRAM\n");
            sb.Append("    #pragma vertex depthVert\n");
            sb.Append("    #pragma fragment depthFrag\n");
            sb.Append("    #include \"Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl\"\n\n");
            sb.Append("    struct DAttrs { float4 positionOS : POSITION; };\n");
            sb.Append("    struct DVary { float4 positionCS : SV_POSITION; };\n\n");
            sb.Append("    DVary depthVert (DAttrs IN) {\n");
            sb.Append("        DVary OUT;\n");
            sb.Append("        OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);\n");
            sb.Append("        return OUT;\n");
            sb.Append("    }\n");
            sb.Append("    half4 depthFrag (DVary IN) : SV_Target { return 0; }\n");
            sb.Append("    ENDHLSL\n");
            sb.Append("  }\n");
            sb.Append("}\n");

            if (!string.IsNullOrEmpty(pf?.m_FallbackName))
            {
                sb.Append($"Fallback \"{pf.m_FallbackName}\"\n");
            }
            sb.Append("}\n");
            return sb.ToString();
        }

        /// <summary>优先挑选贴图/颜色属性的候选名（按游戏中常见的命名习惯排序）。</summary>
        private static readonly string[] TexPreference =
            { "_BaseColorMap", "_BaseMap", "_MainTex", "_AlbedoMap", "_DiffuseMap", "_BaseTex", "_Texture" };

        private static readonly string[] ColPreference =
            { "_BaseColor", "_Color", "_TintColor", "_Tint", "_BaseTintColor" };

        private static string PickProperty(List<SerializedProperty> props, SerializedPropertyType type)
        {
            if (props == null)
            {
                return null;
            }
            var pref = type == SerializedPropertyType.Texture ? TexPreference : ColPreference;
            foreach (var want in pref)
            {
                foreach (var p in props)
                {
                    if (p.m_Type == type && string.Equals(p.m_Name, want, StringComparison.Ordinal))
                    {
                        return p.m_Name;
                    }
                }
            }
            // 退而求其次：取第一个同类型属性（贴图只接受 2D，避免在 sampler2D 里塞 Cube/3D）
            foreach (var p in props)
            {
                if (p.m_Type != type)
                {
                    continue;
                }
                if (type == SerializedPropertyType.Texture
                    && p.m_DefTexture != null
                    && p.m_DefTexture.m_TexDim != TextureDimension.Tex2D
                    && p.m_DefTexture.m_TexDim != TextureDimension.Any)
                {
                    continue;
                }
                return p.m_Name;
            }
            return null;
        }

        private static string ConvertSerializedShader(Shader shader)
        {
            if (shader.subShaderBlobs != null && shader.subShaderBlobs.Count > 0)
            {
                // One ShaderProgram[] (indexed by platform) per ShaderLOD.  The subshaders of
                // m_ParsedForm carry matching m_LOD values, and every LOD has its own program
                // index table, so they must stay separate -- the same Pass has different
                // m_BlobIndex ranges in different LODs.
                var programsByLod = new Dictionary<int, ShaderProgram[]>();
                foreach (var blob in shader.subShaderBlobs)
                {
                    programsByLod[blob.m_ShaderLOD] = DecompressSubShaderBlob(shader, blob);
                }
                return ConvertSerializedShader(shader.m_ParsedForm, shader.platforms, new ShaderProgramSet(programsByLod));
            }

            var length = shader.platforms.Length;
            var shaderPrograms = new ShaderProgram[length];
            for (var i = 0; i < length; i++)
            {
                for (var j = 0; j < shader.offsets[i].Length; j++)
                {
                    var offset = shader.offsets[i][j];
                    var compressedLength = shader.compressedLengths[i][j];
                    var decompressedLength = shader.decompressedLengths[i][j];
                    var decompressedBytes = DecompressChunk(shader, shader.compressedBlob, (int)offset, (int)compressedLength, (int)decompressedLength);
                    using (var blobReader = new EndianBinaryReader(new MemoryStream(decompressedBytes), EndianType.LittleEndian))
                    {
                        if (j == 0)
                        {
                            shaderPrograms[i] = new ShaderProgram(blobReader, shader);
                        }
                        shaderPrograms[i].Read(blobReader, j);
                    }
                }
            }

            return ConvertSerializedShader(shader.m_ParsedForm, shader.platforms, new ShaderProgramSet(shaderPrograms));
        }

        /// <summary>
        /// Expand one <see cref="SubShaderBlob"/> into a <see cref="ShaderProgram"/> per
        /// platform.  Chunk 0 holds the program index table, chunks 1..n the program
        /// segments; <see cref="ShaderProgram.Read"/> already routes each entry to the
        /// chunk named by its <c>Segment</c> field, so feeding it the decompressed chunk
        /// (segment) by segment is enough.
        /// </summary>
        private static ShaderProgram[] DecompressSubShaderBlob(Shader shader, SubShaderBlob blob)
        {
            var programs = new ShaderProgram[shader.platforms.Length];
            if (blob.m_Offsets == null)
            {
                return programs;
            }

            for (var i = 0; i < blob.m_Offsets.Length && i < programs.Length; i++)
            {
                var offsets = blob.m_Offsets[i];
                var compressedLengths = blob.m_CompressedLengths[i];
                var decompressedLengths = blob.m_DecompressedLengths[i];
                for (var j = 0; j < offsets.Length; j++)
                {
                    var decompressedBytes = DecompressChunk(
                        shader,
                        blob.m_CompressedBlob,
                        (int)offsets[j],
                        (int)compressedLengths[j],
                        (int)decompressedLengths[j]);
                    using (var blobReader = new EndianBinaryReader(new MemoryStream(decompressedBytes), EndianType.LittleEndian))
                    {
                        if (j == 0)
                        {
                            programs[i] = new ShaderProgram(blobReader, shader);
                        }
                        programs[i].Read(blobReader, j);
                    }
                }
            }

            return programs;
        }

        private static byte[] DecompressChunk(Shader shader, byte[] blob, int offset, int compressedLength, int decompressedLength)
        {
            var decompressedBytes = new byte[decompressedLength];
            if (decompressedLength == 0)
            {
                return decompressedBytes;
            }

            if (shader.assetsFile.game.Type.IsGISubGroup() || compressedLength == decompressedLength)
            {
                // Stored uncompressed (Unity skips LZ4 when the two lengths match).
                Buffer.BlockCopy(blob, offset, decompressedBytes, 0, decompressedLength);
                return decompressedBytes;
            }

            int numWrite;
            try
            {
                numWrite = LZ4.Instance.Decompress(blob.AsSpan().Slice(offset, compressedLength), decompressedBytes.AsSpan().Slice(0, decompressedLength));
            }
            catch (Exception)
            {
                // Defensive: fall back to a raw copy rather than losing the whole export.
                Buffer.BlockCopy(blob, offset, decompressedBytes, 0, decompressedLength);
                return decompressedBytes;
            }

            if (numWrite != decompressedLength)
            {
                throw new IOException($"Lz4 decompression error, write {numWrite} bytes but expected {decompressedLength} bytes");
            }

            return decompressedBytes;
        }

        private static string ConvertSerializedShader(SerializedShader m_ParsedForm, ShaderCompilerPlatform[] platforms, ShaderProgramSet shaderPrograms)
        {
            var sb = new StringBuilder();
            sb.Append($"Shader \"{m_ParsedForm.m_Name}\" {{\n");

            sb.Append(ConvertSerializedProperties(m_ParsedForm.m_PropInfo));

            // Shader wide union of every pass' bindings: some passes ship minimal
            // per-pass parameters that do not describe all the descriptor sets their
            // compiled programs reference, so renaming falls back to the union.
            var shaderWide = BuildShaderWideContext(m_ParsedForm);

            foreach (var m_SubShader in m_ParsedForm.m_SubShaders)
            {
                sb.Append(ConvertSerializedSubShader(m_SubShader, platforms, shaderPrograms, shaderWide));
            }

            if (!string.IsNullOrEmpty(m_ParsedForm.m_FallbackName))
            {
                sb.Append($"Fallback \"{m_ParsedForm.m_FallbackName}\"\n");
            }

            if (!string.IsNullOrEmpty(m_ParsedForm.m_CustomEditorName))
            {
                sb.Append($"CustomEditor \"{m_ParsedForm.m_CustomEditorName}\"\n");
            }

            sb.Append("}");
            return sb.ToString();
        }

        /// <summary>
        /// Union of the binding contexts of every (pass, stage) pair in the shader.
        /// Conflicting names behind one (set, binding) key are dropped.
        /// </summary>
        private static ShaderBindingContext BuildShaderWideContext(SerializedShader m_ParsedForm)
        {
            var union = new ShaderBindingContext();
            foreach (var subShader in m_ParsedForm.m_SubShaders)
            {
                foreach (var pass in subShader.m_Passes)
                {
                    foreach (var program in new[] { pass.progVertex, pass.progFragment, pass.progGeometry, pass.progHull, pass.progDomain, pass.progRayTracing })
                    {
                        if (program?.m_CommonParameters == null)
                        {
                            continue;
                        }
                        var context = ShaderBindingContext.Build(pass.m_NameIndices, program.m_CommonParameters);
                        if (context != null)
                        {
                            ShaderBindingContext.Merge(context, union);
                        }
                    }
                }
            }
            return union;
        }

        private static string ConvertSerializedSubShader(SerializedSubShader m_SubShader, ShaderCompilerPlatform[] platforms, ShaderProgramSet shaderProgramSet, ShaderBindingContext shaderWide)
        {
            // A subshader is tied to one ShaderLOD; with LOD streaming every LOD has its own
            // program index table, so resolve the matching set here and pass the plain array down.
            var shaderPrograms = shaderProgramSet.Resolve(m_SubShader.m_LOD);

            var sb = new StringBuilder();
            sb.Append("SubShader {\n");
            if (m_SubShader.m_LOD != 0)
            {
                sb.Append($" LOD {m_SubShader.m_LOD}\n");
            }

            sb.Append(ConvertSerializedTagMap(m_SubShader.m_Tags, 1));

            foreach (var m_Passe in m_SubShader.m_Passes)
            {
                sb.Append(ConvertSerializedPass(m_Passe, platforms, shaderPrograms, shaderWide));
            }
            sb.Append("}\n");
            return sb.ToString();
        }

        private static string ConvertSerializedPass(SerializedPass m_Passe, ShaderCompilerPlatform[] platforms, ShaderProgram[] shaderPrograms, ShaderBindingContext shaderWide)
        {
            var sb = new StringBuilder();
            switch (m_Passe.m_Type)
            {
                case PassType.Normal:
                    sb.Append(" Pass ");
                    break;
                case PassType.Use:
                    sb.Append(" UsePass ");
                    break;
                case PassType.Grab:
                    sb.Append(" GrabPass ");
                    break;
            }
            if (m_Passe.m_Type == PassType.Use)
            {
                sb.Append($"\"{m_Passe.m_UseName}\"\n");
            }
            else
            {
                sb.Append("{\n");

                if (m_Passe.m_Type == PassType.Grab)
                {
                    if (!string.IsNullOrEmpty(m_Passe.m_TextureName))
                    {
                        sb.Append($"  \"{m_Passe.m_TextureName}\"\n");
                    }
                }
                else
                {
                    sb.Append(ConvertSerializedShaderState(m_Passe.m_State));

                    AppendProgram(sb, "vp", m_Passe.progVertex, platforms, shaderPrograms, m_Passe.m_NameIndices, shaderWide);
                    AppendProgram(sb, "fp", m_Passe.progFragment, platforms, shaderPrograms, m_Passe.m_NameIndices, shaderWide);
                    AppendProgram(sb, "gp", m_Passe.progGeometry, platforms, shaderPrograms, m_Passe.m_NameIndices, shaderWide);
                    AppendProgram(sb, "hp", m_Passe.progHull, platforms, shaderPrograms, m_Passe.m_NameIndices, shaderWide);
                    AppendProgram(sb, "dp", m_Passe.progDomain, platforms, shaderPrograms, m_Passe.m_NameIndices, shaderWide);
                    AppendProgram(sb, "rtp", m_Passe.progRayTracing, platforms, shaderPrograms, m_Passe.m_NameIndices, shaderWide);
                }
                sb.Append("}\n");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Emit a <c>Program "xx" { ... }</c> block for a single stage, or nothing at all when
        /// the stage carries no programs.  <paramref name="nameIndices"/> is the pass wide
        /// name table the GLSL uniform renamer resolves bindings against.
        /// </summary>
        private static void AppendProgram(StringBuilder sb, string stageName, SerializedProgram program, ShaderCompilerPlatform[] platforms, ShaderProgram[] shaderPrograms, List<KeyValuePair<string, int>> nameIndices, ShaderBindingContext shaderWide)
        {
            var body = program == null ? null : ConvertSerializedProgram(program, platforms, shaderPrograms, nameIndices, shaderWide);
            if (string.IsNullOrEmpty(body))
            {
                return;
            }
            sb.Append($"Program \"{stageName}\" {{\n");
            sb.Append(body);
            sb.Append("}\n");
        }

        /// <summary>
        /// Serialized passes carry their variants in either of two places: <c>m_SubPrograms</c>
        /// (editor data, the only one old AnimeStudio looked at) or <c>m_PlayerSubPrograms</c>
        /// (what a 2021.3.10+ *player* build actually ships -- m_SubPrograms is empty there, which
        /// is why shaders exported from such a build came out with no program blocks at all).
        /// </summary>
        private static string ConvertSerializedProgram(SerializedProgram program, ShaderCompilerPlatform[] platforms, ShaderProgram[] shaderPrograms, List<KeyValuePair<string, int>> nameIndices, ShaderBindingContext shaderWide)
        {
            if (program.m_SubPrograms != null && program.m_SubPrograms.Count > 0)
            {
                return ConvertSerializedSubPrograms(program.m_SubPrograms, platforms, shaderPrograms, nameIndices, shaderWide);
            }
            if (program.m_PlayerSubPrograms != null && program.m_PlayerSubPrograms.Count > 0)
            {
                return ConvertSerializedPlayerSubPrograms(program.m_PlayerSubPrograms, platforms, shaderPrograms, nameIndices, program.m_CommonParameters, shaderWide);
            }
            return null;
        }

        /// <summary>
        /// Emit the variant list a player build ships.  The outer list is a group per graphics
        /// tier/platform bucket and in the builds seen so far only one of them holds data, so
        /// the first group that targets a platform this shader was built for wins.  Every entry
        /// of that group is one variant, addressed by its <c>m_BlobIndex</c> in the program table
        /// of the shader LOD owning the pass.
        /// </summary>
        private static string ConvertSerializedPlayerSubPrograms(List<List<SerializedPlayerSubProgram>> playerSubPrograms, ShaderCompilerPlatform[] platforms, ShaderProgram[] shaderPrograms, List<KeyValuePair<string, int>> nameIndices, SerializedProgramParameters commonParameters, ShaderBindingContext shaderWide)
        {
            var sb = new StringBuilder();
            // All variants of one stage share the pass' common parameters, so one binding
            // context covers every SubProgram emitted below.
            var bindingContext = ShaderBindingContext.Build(nameIndices, commonParameters);
            if (bindingContext != null)
            {
                bindingContext.Fallback = shaderWide;
            }
            foreach (var group in playerSubPrograms)
            {
                if (group == null || group.Count == 0)
                {
                    continue;
                }

                for (int i = 0; i < platforms.Length; i++)
                {
                    var platform = platforms[i];
                    var platformPrograms = shaderPrograms[i]?.m_SubPrograms;

                    foreach (var typeGroup in group.GroupBy(x => x.m_GpuProgramType))
                    {
                        if (!CheckGpuProgramUsable(platform, typeGroup.Key))
                        {
                            continue;
                        }

                        foreach (var subProgram in typeGroup)
                        {
                            sb.Append($"SubProgram \"{GetPlatformString(platform)} \" {{\n");
                            if (platformPrograms == null || subProgram.m_BlobIndex >= platformPrograms.Length)
                            {
                                sb.Append($"// blob {subProgram.m_BlobIndex} not present in this LOD's program table\n");
                            }
                            else if (platformPrograms[subProgram.m_BlobIndex] == null)
                            {
                                sb.Append($"// blob {subProgram.m_BlobIndex} not decoded\n");
                            }
                            else
                            {
                                sb.Append(platformPrograms[subProgram.m_BlobIndex].Export(bindingContext));
                            }
                            sb.Append("\n}\n");
                            if (FirstVariantOnly)
                            {
                                break;
                            }
                        }

                        return sb.ToString();
                    }
                }
            }
            return sb.ToString();
        }

        private static string ConvertSerializedSubPrograms(List<SerializedSubProgram> m_SubPrograms, ShaderCompilerPlatform[] platforms, ShaderProgram[] shaderPrograms, List<KeyValuePair<string, int>> nameIndices, ShaderBindingContext shaderWide)
        {
            var sb = new StringBuilder();
            var groups = m_SubPrograms.GroupBy(x => x.m_BlobIndex);
            if (FirstVariantOnly)
            {
                groups = groups.Take(1);
            }
            foreach (var group in groups)
            {
                var programs = group.GroupBy(x => x.m_GpuProgramType);
                foreach (var program in programs)
                {
                    for (int i = 0; i < platforms.Length; i++)
                    {
                        var platform = platforms[i];
                        if (CheckGpuProgramUsable(platform, program.Key))
                        {
                                var subPrograms = program.ToList();
                                var isTier = subPrograms.Count > 1;
                                var platformPrograms = shaderPrograms[i]?.m_SubPrograms;
                                foreach (var subProgram in subPrograms)
                                {
                                    sb.Append($"SubProgram \"{GetPlatformString(platform)} ");
                                    if (isTier)
                                    {
                                        sb.Append($"hw_tier{subProgram.m_ShaderHardwareTier:00} ");
                                    }
                                    sb.Append("\" {\n");
                                    if (platformPrograms == null || subProgram.m_BlobIndex >= platformPrograms.Length)
                                    {
                                        sb.Append($"// blob {subProgram.m_BlobIndex} not present in this LOD's program table\n");
                                    }
                                    else if (platformPrograms[subProgram.m_BlobIndex] == null)
                                    {
                                        sb.Append($"// blob {subProgram.m_BlobIndex} not decoded\n");
                                    }
                                    else
                                    {
                                        var subContext = ShaderBindingContext.Build(nameIndices, subProgram.m_Parameters);
                                        if (subContext != null)
                                        {
                                            subContext.Fallback = shaderWide;
                                        }
                                        sb.Append(platformPrograms[subProgram.m_BlobIndex].Export(subContext));
                                    }
                                    sb.Append("\n}\n");
                                }
                                break;
                        }
                    }
                }
            }
            return sb.ToString();
        }

        private static string ConvertSerializedShaderState(SerializedShaderState m_State)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(m_State.m_Name))
            {
                sb.Append($"  Name \"{m_State.m_Name}\"\n");
            }
            if (m_State.m_LOD != 0)
            {
                sb.Append($"  LOD {m_State.m_LOD}\n");
            }

            sb.Append(ConvertSerializedTagMap(m_State.m_Tags, 2));

            sb.Append(ConvertSerializedShaderRTBlendState(m_State.rtBlend, m_State.rtSeparateBlend));

            if (m_State.alphaToMask.val > 0f)
            {
                sb.Append("  AlphaToMask On\n");
            }

            if (m_State.zClip?.val != 1f) //ZClip On
            {
                sb.Append("  ZClip Off\n");
            }

            if (m_State.zTest.val != 4f) //ZTest LEqual
            {
                sb.Append("  ZTest ");
                switch (m_State.zTest.val) //enum CompareFunction
                {
                    case 0f: //kFuncDisabled
                        sb.Append("Off");
                        break;
                    case 1f: //kFuncNever
                        sb.Append("Never");
                        break;
                    case 2f: //kFuncLess
                        sb.Append("Less");
                        break;
                    case 3f: //kFuncEqual
                        sb.Append("Equal");
                        break;
                    case 5f: //kFuncGreater
                        sb.Append("Greater");
                        break;
                    case 6f: //kFuncNotEqual
                        sb.Append("NotEqual");
                        break;
                    case 7f: //kFuncGEqual
                        sb.Append("GEqual");
                        break;
                    case 8f: //kFuncAlways
                        sb.Append("Always");
                        break;
                }

                sb.Append("\n");
            }

            if (m_State.zWrite.val != 1f) //ZWrite On
            {
                sb.Append("  ZWrite Off\n");
            }

            if (m_State.culling.val != 2f) //Cull Back
            {
                sb.Append("  Cull ");
                switch (m_State.culling.val) //enum CullMode
                {
                    case 0f: //kCullOff
                        sb.Append("Off");
                        break;
                    case 1f: //kCullFront
                        sb.Append("Front");
                        break;
                }
                sb.Append("\n");
            }

            if (m_State.offsetFactor.val != 0f || m_State.offsetUnits.val != 0f)
            {
                sb.Append($"  Offset {m_State.offsetFactor.val}, {m_State.offsetUnits.val}\n");
            }

            if (m_State.stencilRef.val != 0f ||
                m_State.stencilReadMask.val != 255f ||
                m_State.stencilWriteMask.val != 255f ||
                m_State.stencilOp.pass.val != 0f ||
                m_State.stencilOp.fail.val != 0f ||
                m_State.stencilOp.zFail.val != 0f ||
                m_State.stencilOp.comp.val != 8f ||
                m_State.stencilOpFront.pass.val != 0f ||
                m_State.stencilOpFront.fail.val != 0f ||
                m_State.stencilOpFront.zFail.val != 0f ||
                m_State.stencilOpFront.comp.val != 8f ||
                m_State.stencilOpBack.pass.val != 0f ||
                m_State.stencilOpBack.fail.val != 0f ||
                m_State.stencilOpBack.zFail.val != 0f ||
                m_State.stencilOpBack.comp.val != 8f)
            {
                sb.Append("  Stencil {\n");
                if (m_State.stencilRef.val != 0f)
                {
                    sb.Append($"   Ref {m_State.stencilRef.val}\n");
                }
                if (m_State.stencilReadMask.val != 255f)
                {
                    sb.Append($"   ReadMask {m_State.stencilReadMask.val}\n");
                }
                if (m_State.stencilWriteMask.val != 255f)
                {
                    sb.Append($"   WriteMask {m_State.stencilWriteMask.val}\n");
                }
                if (m_State.stencilOp.pass.val != 0f ||
                    m_State.stencilOp.fail.val != 0f ||
                    m_State.stencilOp.zFail.val != 0f ||
                    m_State.stencilOp.comp.val != 8f)
                {
                    sb.Append(ConvertSerializedStencilOp(m_State.stencilOp, ""));
                }
                if (m_State.stencilOpFront.pass.val != 0f ||
                    m_State.stencilOpFront.fail.val != 0f ||
                    m_State.stencilOpFront.zFail.val != 0f ||
                    m_State.stencilOpFront.comp.val != 8f)
                {
                    sb.Append(ConvertSerializedStencilOp(m_State.stencilOpFront, "Front"));
                }
                if (m_State.stencilOpBack.pass.val != 0f ||
                    m_State.stencilOpBack.fail.val != 0f ||
                    m_State.stencilOpBack.zFail.val != 0f ||
                    m_State.stencilOpBack.comp.val != 8f)
                {
                    sb.Append(ConvertSerializedStencilOp(m_State.stencilOpBack, "Back"));
                }
                sb.Append("  }\n");
            }

            if (m_State.fogMode != FogMode.Unknown ||
                m_State.fogColor.x.val != 0f ||
                m_State.fogColor.y.val != 0f ||
                m_State.fogColor.z.val != 0f ||
                m_State.fogColor.w.val != 0f ||
                m_State.fogDensity.val != 0f ||
                m_State.fogStart.val != 0f ||
                m_State.fogEnd.val != 0f)
            {
                sb.Append("  Fog {\n");
                if (m_State.fogMode != FogMode.Unknown)
                {
                    sb.Append("   Mode ");
                    switch (m_State.fogMode)
                    {
                        case FogMode.Disabled:
                            sb.Append("Off");
                            break;
                        case FogMode.Linear:
                            sb.Append("Linear");
                            break;
                        case FogMode.Exp:
                            sb.Append("Exp");
                            break;
                        case FogMode.Exp2:
                            sb.Append("Exp2");
                            break;
                    }
                    sb.Append("\n");
                }
                if (m_State.fogColor.x.val != 0f ||
                    m_State.fogColor.y.val != 0f ||
                    m_State.fogColor.z.val != 0f ||
                    m_State.fogColor.w.val != 0f)
                {
                    sb.AppendFormat("   Color ({0},{1},{2},{3})\n",
                        m_State.fogColor.x.val.ToString(CultureInfo.InvariantCulture),
                        m_State.fogColor.y.val.ToString(CultureInfo.InvariantCulture),
                        m_State.fogColor.z.val.ToString(CultureInfo.InvariantCulture),
                        m_State.fogColor.w.val.ToString(CultureInfo.InvariantCulture));
                }
                if (m_State.fogDensity.val != 0f)
                {
                    sb.Append($"   Density {m_State.fogDensity.val.ToString(CultureInfo.InvariantCulture)}\n");
                }
                if (m_State.fogStart.val != 0f ||
                    m_State.fogEnd.val != 0f)
                {
                    sb.Append($"   Range {m_State.fogStart.val.ToString(CultureInfo.InvariantCulture)}, {m_State.fogEnd.val.ToString(CultureInfo.InvariantCulture)}\n");
                }
                sb.Append("  }\n");
            }

            if (m_State.lighting)
            {
                sb.Append($"  Lighting {(m_State.lighting ? "On" : "Off")}\n");
            }

            sb.Append($"  GpuProgramID {m_State.gpuProgramID}\n");

            return sb.ToString();
        }

        private static string ConvertSerializedStencilOp(SerializedStencilOp stencilOp, string suffix)
        {
            var sb = new StringBuilder();
            sb.Append($"   Comp{suffix} {ConvertStencilComp(stencilOp.comp)}\n");
            sb.Append($"   Pass{suffix} {ConvertStencilOp(stencilOp.pass)}\n");
            sb.Append($"   Fail{suffix} {ConvertStencilOp(stencilOp.fail)}\n");
            sb.Append($"   ZFail{suffix} {ConvertStencilOp(stencilOp.zFail)}\n");
            return sb.ToString();
        }

        private static string ConvertStencilOp(SerializedShaderFloatValue op)
        {
            switch (op.val)
            {
                case 0f:
                default:
                    return "Keep";
                case 1f:
                    return "Zero";
                case 2f:
                    return "Replace";
                case 3f:
                    return "IncrSat";
                case 4f:
                    return "DecrSat";
                case 5f:
                    return "Invert";
                case 6f:
                    return "IncrWrap";
                case 7f:
                    return "DecrWrap";
            }
        }

        private static string ConvertStencilComp(SerializedShaderFloatValue comp)
        {
            switch (comp.val)
            {
                case 0f:
                    return "Disabled";
                case 1f:
                    return "Never";
                case 2f:
                    return "Less";
                case 3f:
                    return "Equal";
                case 4f:
                    return "LEqual";
                case 5f:
                    return "Greater";
                case 6f:
                    return "NotEqual";
                case 7f:
                    return "GEqual";
                case 8f:
                default:
                    return "Always";
            }
        }

        private static string ConvertSerializedShaderRTBlendState(List<SerializedShaderRTBlendState> rtBlend, bool rtSeparateBlend)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < rtBlend.Count; i++)
            {
                var blend = rtBlend[i];
                if (blend.srcBlend.val != 1f ||
                    blend.destBlend.val != 0f ||
                    blend.srcBlendAlpha.val != 1f ||
                    blend.destBlendAlpha.val != 0f)
                {
                    sb.Append("  Blend ");
                    if (i != 0 || rtSeparateBlend)
                    {
                        sb.Append($"{i} ");
                    }
                    sb.Append($"{ConvertBlendFactor(blend.srcBlend)} {ConvertBlendFactor(blend.destBlend)}");
                    if (blend.srcBlendAlpha.val != 1f ||
                        blend.destBlendAlpha.val != 0f)
                    {
                        sb.Append($", {ConvertBlendFactor(blend.srcBlendAlpha)} {ConvertBlendFactor(blend.destBlendAlpha)}");
                    }
                    sb.Append("\n");
                }

                if (blend.blendOp.val != 0f ||
                    blend.blendOpAlpha.val != 0f)
                {
                    sb.Append("  BlendOp ");
                    if (i != 0 || rtSeparateBlend)
                    {
                        sb.Append($"{i} ");
                    }
                    sb.Append(ConvertBlendOp(blend.blendOp));
                    if (blend.blendOpAlpha.val != 0f)
                    {
                        sb.Append($", {ConvertBlendOp(blend.blendOpAlpha)}");
                    }
                    sb.Append("\n");
                }

                var val = (int)blend.colMask.val;
                if (val != 0xf)
                {
                    sb.Append("  ColorMask ");
                    if (val == 0)
                    {
                        sb.Append(0);
                    }
                    else
                    {
                        if ((val & 0x2) != 0)
                        {
                            sb.Append("R");
                        }
                        if ((val & 0x4) != 0)
                        {
                            sb.Append("G");
                        }
                        if ((val & 0x8) != 0)
                        {
                            sb.Append("B");
                        }
                        if ((val & 0x1) != 0)
                        {
                            sb.Append("A");
                        }
                    }
                    sb.Append($" {i}\n");
                }
            }
            return sb.ToString();
        }

        private static string ConvertBlendOp(SerializedShaderFloatValue op)
        {
            switch (op.val)
            {
                case 0f:
                default:
                    return "Add";
                case 1f:
                    return "Sub";
                case 2f:
                    return "RevSub";
                case 3f:
                    return "Min";
                case 4f:
                    return "Max";
                case 5f:
                    return "LogicalClear";
                case 6f:
                    return "LogicalSet";
                case 7f:
                    return "LogicalCopy";
                case 8f:
                    return "LogicalCopyInverted";
                case 9f:
                    return "LogicalNoop";
                case 10f:
                    return "LogicalInvert";
                case 11f:
                    return "LogicalAnd";
                case 12f:
                    return "LogicalNand";
                case 13f:
                    return "LogicalOr";
                case 14f:
                    return "LogicalNor";
                case 15f:
                    return "LogicalXor";
                case 16f:
                    return "LogicalEquiv";
                case 17f:
                    return "LogicalAndReverse";
                case 18f:
                    return "LogicalAndInverted";
                case 19f:
                    return "LogicalOrReverse";
                case 20f:
                    return "LogicalOrInverted";
            }
        }

        private static string ConvertBlendFactor(SerializedShaderFloatValue factor)
        {
            switch (factor.val)
            {
                case 0f:
                    return "Zero";
                case 1f:
                default:
                    return "One";
                case 2f:
                    return "DstColor";
                case 3f:
                    return "SrcColor";
                case 4f:
                    return "OneMinusDstColor";
                case 5f:
                    return "SrcAlpha";
                case 6f:
                    return "OneMinusSrcColor";
                case 7f:
                    return "DstAlpha";
                case 8f:
                    return "OneMinusDstAlpha";
                case 9f:
                    return "SrcAlphaSaturate";
                case 10f:
                    return "OneMinusSrcAlpha";
            }
        }

        private static string ConvertSerializedTagMap(SerializedTagMap m_Tags, int intent)
        {
            var sb = new StringBuilder();
            if (m_Tags.tags.Count > 0)
            {
                sb.Append(new string(' ', intent));
                sb.Append("Tags { ");
                foreach (var pair in m_Tags.tags)
                {
                    sb.Append($"\"{pair.Key}\" = \"{pair.Value}\" ");
                }
                sb.Append("}\n");
            }
            return sb.ToString();
        }

        private static string ConvertSerializedProperties(SerializedProperties m_PropInfo)
        {
            var sb = new StringBuilder();
            sb.Append("Properties {\n");
            foreach (var m_Prop in m_PropInfo.m_Props)
            {
                sb.Append(ConvertSerializedProperty(m_Prop));
            }
            sb.Append("}\n");
            return sb.ToString();
        }

        private static string ConvertSerializedProperty(SerializedProperty m_Prop)
        {
            var sb = new StringBuilder();
            foreach (var m_Attribute in m_Prop.m_Attributes)
            {
                sb.Append($"[{m_Attribute}] ");
            }
            foreach (var flag in Enum.GetValues<SerializedPropertyFlag>().Where(x => m_Prop.m_Flags.HasFlag(x)))
            {
                sb.Append($"[{flag}] ");
            }
            sb.Append($"{m_Prop.m_Name} (\"{m_Prop.m_Description}\", ");
            switch (m_Prop.m_Type)
            {
                case SerializedPropertyType.Color:
                    sb.Append("Color");
                    break;
                case SerializedPropertyType.Vector:
                    sb.Append("Vector");
                    break;
                case SerializedPropertyType.Float:
                    sb.Append("Float");
                    break;
                case SerializedPropertyType.Range:
                    sb.Append($"Range({m_Prop.m_DefValue[1]}, {m_Prop.m_DefValue[2]})");
                    break;
                case SerializedPropertyType.Texture:
                    switch (m_Prop.m_DefTexture.m_TexDim)
                    {
                        case TextureDimension.Any:
                            sb.Append("any");
                            break;
                        case TextureDimension.Tex2D:
                            sb.Append("2D");
                            break;
                        case TextureDimension.Tex3D:
                            sb.Append("3D");
                            break;
                        case TextureDimension.Cube:
                            sb.Append("Cube");
                            break;
                        case TextureDimension.Tex2DArray:
                            sb.Append("2DArray");
                            break;
                        case TextureDimension.CubeArray:
                            sb.Append("CubeArray");
                            break;
                    }
                    break;
            }
            sb.Append(") = ");
            switch (m_Prop.m_Type)
            {
                case SerializedPropertyType.Color:
                case SerializedPropertyType.Vector:
                    sb.Append($"({m_Prop.m_DefValue[0]},{m_Prop.m_DefValue[1]},{m_Prop.m_DefValue[2]},{m_Prop.m_DefValue[3]})");
                    break;
                case SerializedPropertyType.Float:
                case SerializedPropertyType.Range:
                    sb.Append(m_Prop.m_DefValue[0]);
                    break;
                case SerializedPropertyType.Texture:
                    sb.Append($"\"{m_Prop.m_DefTexture.m_DefaultName}\" {{ }}");
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
            sb.Append("\n");
            return sb.ToString();
        }

        private static bool CheckGpuProgramUsable(ShaderCompilerPlatform platform, ShaderGpuProgramType programType)
        {
            switch (platform)
            {
                case ShaderCompilerPlatform.GL:
                    return programType == ShaderGpuProgramType.GLLegacy;
                case ShaderCompilerPlatform.D3D9:
                    return programType == ShaderGpuProgramType.DX9VertexSM20
                        || programType == ShaderGpuProgramType.DX9VertexSM30
                        || programType == ShaderGpuProgramType.DX9PixelSM20
                        || programType == ShaderGpuProgramType.DX9PixelSM30;
                case ShaderCompilerPlatform.Xbox360:
                case ShaderCompilerPlatform.PS3:
                case ShaderCompilerPlatform.PSP2:
                case ShaderCompilerPlatform.PS4:
                case ShaderCompilerPlatform.XboxOne:
                case ShaderCompilerPlatform.N3DS:
                case ShaderCompilerPlatform.WiiU:
                case ShaderCompilerPlatform.Switch:
                case ShaderCompilerPlatform.XboxOneD3D12:
                case ShaderCompilerPlatform.GameCoreXboxOne:
                case ShaderCompilerPlatform.GameCoreScarlett:
                case ShaderCompilerPlatform.PS5:
                    return programType == ShaderGpuProgramType.ConsoleVS
                        || programType == ShaderGpuProgramType.ConsoleFS
                        || programType == ShaderGpuProgramType.ConsoleHS
                        || programType == ShaderGpuProgramType.ConsoleDS
                        || programType == ShaderGpuProgramType.ConsoleGS;
                case ShaderCompilerPlatform.PS5NGGC:
                    return programType == ShaderGpuProgramType.PS5NGGC;
                case ShaderCompilerPlatform.D3D11:
                    return programType == ShaderGpuProgramType.DX11VertexSM40
                        || programType == ShaderGpuProgramType.DX11VertexSM50
                        || programType == ShaderGpuProgramType.DX11PixelSM40
                        || programType == ShaderGpuProgramType.DX11PixelSM50
                        || programType == ShaderGpuProgramType.DX11GeometrySM40
                        || programType == ShaderGpuProgramType.DX11GeometrySM50
                        || programType == ShaderGpuProgramType.DX11HullSM50
                        || programType == ShaderGpuProgramType.DX11DomainSM50;
                case ShaderCompilerPlatform.GLES20:
                    return programType == ShaderGpuProgramType.GLES;
                case ShaderCompilerPlatform.NaCl: //Obsolete
                    throw new NotSupportedException();
                case ShaderCompilerPlatform.Flash: //Obsolete
                    throw new NotSupportedException();
                case ShaderCompilerPlatform.D3D11_9x:
                    return programType == ShaderGpuProgramType.DX10Level9Vertex
                        || programType == ShaderGpuProgramType.DX10Level9Pixel;
                case ShaderCompilerPlatform.GLES3Plus:
                    return programType == ShaderGpuProgramType.GLES31AEP
                        || programType == ShaderGpuProgramType.GLES31
                        || programType == ShaderGpuProgramType.GLES3;
                case ShaderCompilerPlatform.PSM: //Unknown
                    throw new NotSupportedException();
                case ShaderCompilerPlatform.Metal:
                    return programType == ShaderGpuProgramType.MetalVS
                        || programType == ShaderGpuProgramType.MetalFS;
                case ShaderCompilerPlatform.OpenGLCore:
                    return programType == ShaderGpuProgramType.GLCore32
                        || programType == ShaderGpuProgramType.GLCore41
                        || programType == ShaderGpuProgramType.GLCore43;
                case ShaderCompilerPlatform.Vulkan:
                    return programType == ShaderGpuProgramType.SPIRV;
                default:
                    throw new NotSupportedException();
            }
        }

        public static string GetPlatformString(ShaderCompilerPlatform platform)
        {
            switch (platform)
            {
                case ShaderCompilerPlatform.GL:
                    return "openGL";
                case ShaderCompilerPlatform.D3D9:
                    return "d3d9";
                case ShaderCompilerPlatform.Xbox360:
                    return "xbox360";
                case ShaderCompilerPlatform.PS3:
                    return "ps3";
                case ShaderCompilerPlatform.D3D11:
                    return "d3d11";
                case ShaderCompilerPlatform.GLES20:
                    return "gles";
                case ShaderCompilerPlatform.NaCl:
                    return "glesdesktop";
                case ShaderCompilerPlatform.Flash:
                    return "flash";
                case ShaderCompilerPlatform.D3D11_9x:
                    return "d3d11_9x";
                case ShaderCompilerPlatform.GLES3Plus:
                    return "gles3";
                case ShaderCompilerPlatform.PSP2:
                    return "psp2";
                case ShaderCompilerPlatform.PS4:
                    return "ps4";
                case ShaderCompilerPlatform.XboxOne:
                    return "xboxone";
                case ShaderCompilerPlatform.PSM:
                    return "psm";
                case ShaderCompilerPlatform.Metal:
                    return "metal";
                case ShaderCompilerPlatform.OpenGLCore:
                    return "glcore";
                case ShaderCompilerPlatform.N3DS:
                    return "n3ds";
                case ShaderCompilerPlatform.WiiU:
                    return "wiiu";
                case ShaderCompilerPlatform.Vulkan:
                    return "vulkan";
                case ShaderCompilerPlatform.Switch:
                    return "switch";
                case ShaderCompilerPlatform.XboxOneD3D12:
                    return "xboxone_d3d12";
                case ShaderCompilerPlatform.GameCoreXboxOne:
                    return "xboxone";
                case ShaderCompilerPlatform.GameCoreScarlett:
                    return "xbox_scarlett";
                case ShaderCompilerPlatform.PS5:
                    return "ps5";
                case ShaderCompilerPlatform.PS5NGGC:
                    return "ps5_nggc";
                default:
                    return "unknown";
            }
        }

        private static string header = "//////////////////////////////////////////\n" +
                                      "//\n" +
                                      "// NOTE: This is *not* a valid shader file\n" +
                                      "//\n" +
                                      "///////////////////////////////////////////\n";
    }

    public class ShaderSubProgramEntry
    {
        public int Offset;
        public int Length;
        public int Segment;

        public ShaderSubProgramEntry(EndianBinaryReader reader, int[] version)
        {
            Offset = reader.ReadInt32();
            Length = reader.ReadInt32();
            if (version[0] > 2019 || (version[0] == 2019 && version[1] >= 3)) //2019.3 and up
            {
                Segment = reader.ReadInt32();
            }
        }
    }

    /// <summary>
    /// Holds the compiled programs of a Shader in whichever shape the build uses.
    ///
    /// <para>Classic layout: one <see cref="ShaderProgram"/> per platform, selected by
    /// platform index (the array passed to the converter).</para>
    ///
    /// <para>LOD-streaming layout (<c>m_EnableShaderLODStreaming</c>, live Endfield): one
    /// <see cref="ShaderProgram"/> per platform <em>per ShaderLOD</em>, because every LOD
    /// ships its own program index table.  Subshaders are resolved by their
    /// <c>m_LOD</c>.</para>
    /// </summary>
    public class ShaderProgramSet
    {
        private readonly ShaderProgram[] byPlatform;
        private readonly Dictionary<int, ShaderProgram[]> byLod;

        public ShaderProgramSet(ShaderProgram[] byPlatform)
        {
            this.byPlatform = byPlatform;
        }

        public ShaderProgramSet(Dictionary<int, ShaderProgram[]> byLod)
        {
            this.byLod = byLod;
            byPlatform = byLod.OrderByDescending(x => x.Value?.Sum(p => p?.m_SubPrograms?.Length ?? 0) ?? 0)
                              .Select(x => x.Value)
                              .FirstOrDefault();
        }

        /// <summary>Programs to use for a subshader with the given LOD.</summary>
        public ShaderProgram[] Resolve(int lod)
        {
            if (byLod == null)
            {
                return byPlatform;
            }

            if (byLod.TryGetValue(lod, out var exact))
            {
                return exact;
            }

            // Unknown LOD: fall back to the richest table so the export still produces output.
            return byLod.OrderByDescending(x => x.Value?.Sum(p => p?.m_SubPrograms?.Length ?? 0) ?? 0)
                        .Select(x => x.Value)
                        .FirstOrDefault() ?? byPlatform;
        }
    }

    public class ShaderProgram
    {
        public ShaderSubProgramEntry[] entries;
        public ShaderSubProgram[] m_SubPrograms;

        private bool hasUpdatedGpuProgram = false;

        public ShaderProgram(EndianBinaryReader reader, Shader shader)
        {
            var subProgramsCapacity = reader.ReadInt32();
            entries = new ShaderSubProgramEntry[subProgramsCapacity];
            for (int i = 0; i < subProgramsCapacity; i++)
            {
                entries[i] = new ShaderSubProgramEntry(reader, shader.version);
            }
            m_SubPrograms = new ShaderSubProgram[subProgramsCapacity];
            if (shader.assetsFile.game.Type.IsGI())
            {
                hasUpdatedGpuProgram = SerializedSubProgram.HasInstancedStructuredBuffers(shader.serializedType) || SerializedSubProgram.HasGlobalLocalKeywordIndices(shader.serializedType);
            }
        }

        public void Read(EndianBinaryReader reader, int segment)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                if (entry.Segment == segment)
                {
                    reader.BaseStream.Position = entry.Offset;
                    try
                    {
                        m_SubPrograms[i] = new ShaderSubProgram(reader, hasUpdatedGpuProgram);
                    }
                    catch (Exception e)
                    {
                        // The program index table also lists the programs of every other
                        // graphics API the shader was ever built for (GLCore43 / GLES /
                        // GLCore41 / GLCore32 have been observed), and those are stored in a
                        // different layout.  Those entries are never referenced by a
                        // subprogram of this build's platform, and ConvertSerializedSubPrograms
                        // skips nulls, so leaving them undecoded is harmless.
                        Logger.Verbose($"Skipping undecodable shader subprogram {i} (segment {segment}, offset {entry.Offset}): {e.Message}");
                    }
                }
            }
        }

        public string Export(string shader)
        {
            var evaluator = new MatchEvaluator(match =>
            {
                var index = int.Parse(match.Groups[1].Value);
                return m_SubPrograms[index].Export();
            });
            shader = Regex.Replace(shader, "GpuProgramIndex (.+)", evaluator);
            return shader;
        }
    }

    public class ShaderSubProgram
    {
        private int m_Version;
        public ShaderGpuProgramType m_ProgramType;
        public string[] m_Keywords;
        public string[] m_LocalKeywords;
        public byte[] m_ProgramCode;

        public ShaderSubProgram(EndianBinaryReader reader, bool hasUpdatedGpuProgram)
        {
            //LoadGpuProgramFromData
            //201509030 - Unity 5.3
            //201510240 - Unity 5.4
            //201608170 - Unity 5.5
            //201609010 - Unity 5.6, 2017.1 & 2017.2
            //201708220 - Unity 2017.3, Unity 2017.4 & Unity 2018.1
            //201802150 - Unity 2018.2 & Unity 2018.3
            //201806140 - Unity 2019.1~2021.1
            //202012090 - Unity 2021.2
            m_Version = reader.ReadInt32();
            if (hasUpdatedGpuProgram && m_Version > 201806140)
            {
                m_Version = 201806140;
            }
            m_ProgramType = (ShaderGpuProgramType)reader.ReadInt32();
            reader.BaseStream.Position += 12;
            if (m_Version >= 201608170)
            {
                reader.BaseStream.Position += 4;
            }
            var m_KeywordsSize = reader.ReadInt32();
            m_Keywords = new string[m_KeywordsSize];
            for (int i = 0; i < m_KeywordsSize; i++)
            {
                m_Keywords[i] = reader.ReadAlignedString();
            }
            if (m_Version >= 201806140 && m_Version < 202012090)
            {
                var m_LocalKeywordsSize = reader.ReadInt32();
                m_LocalKeywords = new string[m_LocalKeywordsSize];
                for (int i = 0; i < m_LocalKeywordsSize; i++)
                {
                    m_LocalKeywords[i] = reader.ReadAlignedString();
                }
            }
            m_ProgramCode = reader.ReadUInt8Array();
            reader.AlignStream();

            //TODO
        }

        public string Export(ShaderBindingContext bindingContext = null)
        {
            var sb = new StringBuilder();
            if (m_Keywords.Length > 0)
            {
                sb.Append("Keywords { ");
                foreach (string keyword in m_Keywords)
                {
                    sb.Append($"\"{keyword}\" ");
                }
                sb.Append("}\n");
            }
            if (m_LocalKeywords != null && m_LocalKeywords.Length > 0)
            {
                sb.Append("Local Keywords { ");
                foreach (string keyword in m_LocalKeywords)
                {
                    sb.Append($"\"{keyword}\" ");
                }
                sb.Append("}\n");
            }

            sb.Append("\"");
            if (m_ProgramCode.Length > 0)
            {
                switch (m_ProgramType)
                {
                    case ShaderGpuProgramType.GLLegacy:
                    case ShaderGpuProgramType.GLES31AEP:
                    case ShaderGpuProgramType.GLES31:
                    case ShaderGpuProgramType.GLES3:
                    case ShaderGpuProgramType.GLES:
                    case ShaderGpuProgramType.GLCore32:
                    case ShaderGpuProgramType.GLCore41:
                    case ShaderGpuProgramType.GLCore43:
                        sb.Append($"// hash: {ComputeHash64(m_ProgramCode):x8}\n");
                        sb.Append(Encoding.UTF8.GetString(m_ProgramCode));
                        break;
                    case ShaderGpuProgramType.DX9VertexSM20:
                    case ShaderGpuProgramType.DX9VertexSM30:
                    case ShaderGpuProgramType.DX9PixelSM20:
                    case ShaderGpuProgramType.DX9PixelSM30:
                        {
                            try
                            {
                                var programCodeSpan = m_ProgramCode.AsSpan();
                                var g = Compiler.Disassemble(programCodeSpan.GetPinnableReference(), new PointerUSize((ulong)programCodeSpan.Length), DisasmFlags.None, "");

                                sb.Append($"// hash: {ComputeHash64(programCodeSpan):x8}\n");
                                sb.Append(g.AsString());
                            }
                            catch (Exception e)
                            {
                                sb.Append($"// disassembly error {e.Message}\n");
                            }

                            break;
                        }
                    case ShaderGpuProgramType.DX10Level9Vertex:
                    case ShaderGpuProgramType.DX10Level9Pixel:
                    case ShaderGpuProgramType.DX11VertexSM40:
                    case ShaderGpuProgramType.DX11VertexSM50:
                    case ShaderGpuProgramType.DX11PixelSM40:
                    case ShaderGpuProgramType.DX11PixelSM50:
                    case ShaderGpuProgramType.DX11GeometrySM40:
                    case ShaderGpuProgramType.DX11GeometrySM50:
                    case ShaderGpuProgramType.DX11HullSM50:
                    case ShaderGpuProgramType.DX11DomainSM50:
                        {
                            int type = m_ProgramCode[0];
                            int start = 1;
                            if (type > 0)
                            {
                                if (type == 1)
                                {
                                    start = 6;
                                }
                                else if (type == 2)
                                {
                                    start = 38;
                                }
                            }

                            var buffSpan = m_ProgramCode.AsSpan(start);

                            sb.Append($"// hash: {ComputeHash64(buffSpan):x8}\n");
                            try
                            {
                                HLSLDecompiler.DecompileShader(buffSpan.ToArray(), buffSpan.Length, out var hlslText);
                                sb.Append(hlslText);
                            }
                            catch (Exception e)
                            {
                                Logger.Verbose($"Decompile error {e.Message}");
                                Logger.Verbose($"Attempting to disassemble...");

                                try
                                {
                                    var g = Compiler.Disassemble(buffSpan.GetPinnableReference(), new PointerUSize((ulong)buffSpan.Length), DisasmFlags.None, "");
                                    sb.Append(g.AsString());
                                }
                                catch (Exception ex)
                                {
                                    sb.Append($"// decompile/disassembly error {ex.Message}\n");
                                }
                            }
                            break;
                        }
                    case ShaderGpuProgramType.MetalVS:
                    case ShaderGpuProgramType.MetalFS:
                        sb.Append($"// hash: {ComputeHash64(m_ProgramCode):x8}\n");
                        using (var reader = new EndianBinaryReader(new MemoryStream(m_ProgramCode), EndianType.LittleEndian))
                        {
                            var fourCC = reader.ReadUInt32();
                            if (fourCC == 0xf00dcafe)
                            {
                                int offset = reader.ReadInt32();
                                reader.BaseStream.Position = offset;
                            }
                            var entryName = reader.ReadStringToNull();
                            var buff = reader.ReadBytes((int)(reader.BaseStream.Length - reader.BaseStream.Position));
                            sb.Append(Encoding.UTF8.GetString(buff));
                        }
                        break;
                    case ShaderGpuProgramType.SPIRV:
                        try
                        {
                            sb.Append($"// hash: {ComputeHash64(m_ProgramCode):x8}\n");
                            // Prefer GLSL: the SPIR-V listing is roughly 2.5x larger and much
                            // harder to read. ConvertToGlsl returns null when spirv-cross is
                            // missing or cannot handle the module, so the listing stays as the
                            // fallback. With a binding context the anonymous GLSL identifiers
                            // are renamed to the serialized uniform/property names.
                            sb.Append(SpirVShaderConverter.ConvertToGlsl(m_ProgramCode, bindingContext)
                                      ?? SpirVShaderConverter.Convert(m_ProgramCode));
                        }
                        catch (Exception e)
                        {
                            sb.Append($"// disassembly error {e.Message}\n");
                        }
                        break;
                    case ShaderGpuProgramType.ConsoleVS:
                    case ShaderGpuProgramType.ConsoleFS:
                    case ShaderGpuProgramType.ConsoleHS:
                    case ShaderGpuProgramType.ConsoleDS:
                    case ShaderGpuProgramType.ConsoleGS:
                        sb.Append($"//hash: {ComputeHash64(m_ProgramCode):x8}\n");
                        sb.Append(Encoding.UTF8.GetString(m_ProgramCode));
                        break;
                    default:
                        sb.Append($"//hash: {ComputeHash64(m_ProgramCode):x8}\n");
                        sb.Append($"//shader disassembly not supported on {m_ProgramType}");
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
        public ulong ComputeHash64(Span<byte> data)
        {
            ulong hval = 0;
            foreach (var b in data)
            {
                hval *= 0x100000001B3;
                hval ^= b;
            }
            return hval;
        }
    }

    public static class HLSLDecompiler
    {
        private const string DLL_NAME = "AnimeStudio.HLSLDecompiler";
        static HLSLDecompiler()
        {
            // x64 only, so it lives in the application directory rather than in x86/x64.
            DllLoader.PreloadDll(DLL_NAME, archSpecific: false);
        }
        public static void DecompileShader(byte[] shaderByteCode, int shaderByteCodeSize, out string hlslText)
        {
            var code = Decompile(shaderByteCode, shaderByteCodeSize, out var shaderText, out var shaderTextSize);
            if (code != 0)
            {
                throw new Exception($"Unable to decompile shader, Error code: {code}");
            }

            hlslText = Marshal.PtrToStringAnsi(shaderText, shaderTextSize);
            Marshal.FreeHGlobal(shaderText);
        }

        #region importfunctions

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int Decompile(byte[] shaderByteCode, int shaderByteCodeSize, out IntPtr shaderText, out int shaderTextSize);

        #endregion
    }
}
