using Smolv;
using SpirV;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AnimeStudio
{
    /// <summary>
    /// Turns a Unity Vulkan sub program into text.
    ///
    /// The sub program holds a small table pointing at one SMOL-V stream per shader
    /// stage (vertex, fragment, geometry, hull/tess-control and domain/tess-evaluation;
    /// 2019.3+ appends a sixth slot).  Each stream is decoded to SPIR-V and then either
    /// decompiled to GLSL with <c>spirv-cross</c> (preferred: much smaller and far more
    /// readable) or, when that is not available, disassembled to a SPIR-V listing.
    /// </summary>
    public static class SpirVShaderConverter
    {
        private const int SnippetCount = 5;

        /// <summary>SPIR-V assembly listing for every stage, the historical behaviour.</summary>
        public static string Convert(byte[] m_ProgramCode)
        {
            var sb = new StringBuilder();
            foreach (var snippet in ReadSnippets(m_ProgramCode))
            {
                sb.Append(ExportSnippet(m_ProgramCode, snippet.Key, snippet.Value));
            }
            return sb.ToString();
        }

        /// <summary>
        /// GLSL for every stage, or null when nothing could be decompiled (in which case
        /// the caller should fall back to <see cref="Convert(byte[])"/>).  When a
        /// <see cref="ShaderBindingContext"/> is supplied, the anonymous spirv-cross
        /// identifiers are renamed to the serialized shader's uniform/property names.
        /// </summary>
        public static string ConvertToGlsl(byte[] m_ProgramCode, ShaderBindingContext bindingContext = null)
        {
            if (m_ProgramCode == null || m_ProgramCode.Length == 0 || !SpirvCross.Available)
            {
                return null;
            }

            var sb = new StringBuilder();
            bool any = false;
            foreach (var snippet in ReadSnippets(m_ProgramCode))
            {
                byte[] spirv = DecodeSnippet(m_ProgramCode, snippet.Key, snippet.Value, out string error);
                if (spirv == null)
                {
                    sb.Append("// spirv-cross could not decode this stage: ").Append(error).Append('\n');
                    continue;
                }

                if (SpirvCross.TryDecompile(spirv, out string glsl, out error))
                {
                    if (bindingContext != null && bindingContext.HasBindings)
                    {
                        glsl = GlslUniformRenamer.Rename(glsl, SpiResourceMap.Parse(spirv), bindingContext);
                    }
                    string stage = SpirvCross.StageOf(spirv) ?? "unknown";
                    sb.Append("// ---- ").Append(stage).Append(" ----\n").Append(glsl).Append('\n');
                    any = true;
                }
                else
                {
                    sb.Append("// spirv-cross could not decompile this stage: ").Append(error).Append('\n');
                }
            }
            return any ? sb.ToString() : null;
        }

        private static List<KeyValuePair<int, int>> ReadSnippets(byte[] programCode)
        {
            var snippets = new List<KeyValuePair<int, int>>();
            if (programCode == null || programCode.Length < 8)
            {
                return snippets;
            }

            using (var ms = new MemoryStream(programCode))
            using (var reader = new BinaryReader(ms))
            {
                reader.ReadInt32(); // requirements
                int minOffset = programCode.Length;
                for (int i = 0; i < SnippetCount; i++)
                {
                    if (reader.BaseStream.Position >= minOffset)
                    {
                        break;
                    }

                    int offset = reader.ReadInt32();
                    int size = reader.ReadInt32();
                    if (size <= 0)
                    {
                        continue;
                    }
                    if (offset < minOffset)
                    {
                        minOffset = offset;
                    }
                    snippets.Add(new KeyValuePair<int, int>(offset, size));
                }
            }
            return snippets;
        }

        private static byte[] DecodeSnippet(byte[] programCode, int offset, int size, out string error)
        {
            error = null;
            using (var ms = new MemoryStream(programCode))
            {
                ms.Position = offset;
                int decodedSize = SmolvDecoder.GetDecodedBufferSize(ms);
                if (decodedSize == 0)
                {
                    error = "invalid SMOL-V shader header";
                    return null;
                }

                using (var decodedStream = new MemoryStream(new byte[decodedSize]))
                {
                    if (!SmolvDecoder.Decode(ms, size, decodedStream))
                    {
                        error = "unable to decode SMOL-V shader";
                        return null;
                    }
                    return decodedStream.ToArray();
                }
            }
        }

        private static string ExportSnippet(byte[] programCode, int offset, int size)
        {
            byte[] spirv = DecodeSnippet(programCode, offset, size, out string error);
            if (spirv == null)
            {
                throw new Exception(error ?? "unable to decode SMOL-V shader");
            }

            using (var decodedStream = new MemoryStream(spirv))
            {
                var module = Module.ReadFrom(decodedStream);
                var disassembler = new Disassembler();
                return disassembler.Disassemble(module, DisassemblyOptions.Default).Replace("\r\n", "\n");
            }
        }
    }
}
