using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace AnimeStudio
{
    /// <summary>
    /// Wrapper around the external <c>spirv-cross</c> command line tool.
    ///
    /// Unity's Vulkan sub programs are SPIR-V modules.  Disassembling them back to
    /// SPIR-V assembly produces a listing roughly 2.5x larger than the equivalent
    /// GLSL and is hard to read, so <c>spirv-cross</c> is preferred whenever it can
    /// be found.  Resolution order:
    ///
    /// <list type="number">
    /// <item>the <c>SPIRV_CROSS</c> environment variable,</item>
    /// <item>the places the tool commonly ships in (RenderDoc, the Vulkan SDK),</item>
    /// <item><c>PATH</c>.</item>
    /// </list>
    ///
    /// Set <c>ANIMESTUDIO_SHADER_ASM=1</c> to keep the raw SPIR-V disassembly.
    /// </summary>
    public static class SpirvCross
    {
        private static readonly Lazy<string> executable = new Lazy<string>(Locate);
        private static readonly ConcurrentDictionary<string, string> cache =
            new ConcurrentDictionary<string, string>();
        private static readonly object logGate = new object();
        private static bool reportedMissing;

        /// <summary>True when the user asked for the SPIR-V assembly listing instead.</summary>
        public static bool ForceAssembly { get; } =
            IsTruthy(Environment.GetEnvironmentVariable("ANIMESTUDIO_SHADER_ASM"));

        /// <summary>Path of the resolved spirv-cross binary, or null when unavailable.</summary>
        public static string Executable => executable.Value;

        /// <summary>True when GLSL output is both requested and possible.</summary>
        public static bool Available => !ForceAssembly && Executable != null;

        /// <summary>Number of distinct modules decompiled so far (cache misses).</summary>
        public static int CompileCount;
        /// <summary>Number of cache hits.</summary>
        public static int CacheHits;

        private static readonly string[] Candidates =
        {
            @"C:\Program Files\RenderDoc\plugins\spirv\spirv-cross.exe",
            @"C:\Program Files\RenderDoc\plugins\spirv\spirv-cross",
            @"C:\VulkanSDK\Bin\spirv-cross.exe",
            @"/usr/bin/spirv-cross",
            @"/usr/local/bin/spirv-cross",
            @"/opt/homebrew/bin/spirv-cross",
        };

        private static bool IsTruthy(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
            value = value.Trim().ToLowerInvariant();
            return value == "1" || value == "true" || value == "yes" || value == "on";
        }

        private static string Locate()
        {
            if (ForceAssembly)
            {
                return null;
            }

            string explicitPath = Environment.GetEnvironmentVariable("SPIRV_CROSS");
            if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
            {
                return explicitPath;
            }

            foreach (string candidate in Candidates)
            {
                try
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                    // ignore unreadable paths
                }
            }

            string fileName = OperatingSystem.IsWindows() ? "spirv-cross.exe" : "spirv-cross";
            string pathVariable = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathVariable))
            {
                foreach (string dir in pathVariable.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(dir))
                    {
                        continue;
                    }
                    try
                    {
                        string full = Path.Combine(dir.Trim(), fileName);
                        if (File.Exists(full))
                        {
                            return full;
                        }
                    }
                    catch
                    {
                        // ignore malformed PATH entries
                    }
                }
            }

            if (!reportedMissing)
            {
                reportedMissing = true;
                Logger.Warning("spirv-cross was not found; falling back to SPIR-V assembly. " +
                    "Set SPIRV_CROSS to its path, or ANIMESTUDIO_SHADER_ASM=1 to silence this.");
            }
            return null;
        }

        /// <summary>
        /// Execution model of the module's first entry point, as the spirv-cross
        /// <c>--stage</c> spelling (vert/frag/...), or null when it cannot be told.
        /// </summary>
        public static string StageOf(byte[] spirv)
        {
            if (spirv == null || spirv.Length < 20)
            {
                return null;
            }

            int position = 20; // skip the five word header
            while (position + 8 <= spirv.Length)
            {
                uint word = BitConverter.ToUInt32(spirv, position);
                int wordCount = (int)(word >> 16);
                int opCode = (int)(word & 0xFFFF);
                if (wordCount <= 0)
                {
                    break;
                }

                if (opCode == 15) // OpEntryPoint
                {
                    switch (BitConverter.ToUInt32(spirv, position + 4))
                    {
                        case 0: return "vert";
                        case 1: return "tesc";
                        case 2: return "tese";
                        case 3: return "geom";
                        case 4: return "frag";
                        case 5: return "comp";
                        case 6: return "comp";
                    }
                }

                position += wordCount * 4;
            }
            return null;
        }

        /// <summary>
        /// Decompile one SPIR-V module to GLSL.  Results (including failures) are
        /// cached, because a shader's keyword variants often share modules.
        /// </summary>
        public static bool TryDecompile(byte[] spirv, out string glsl, out string error)
        {
            glsl = null;
            error = null;

            string exe = Executable;
            if (exe == null)
            {
                error = "spirv-cross is not available";
                return false;
            }
            if (spirv == null || spirv.Length < 20)
            {
                error = "empty SPIR-V module";
                return false;
            }

            string stage = StageOf(spirv);
            string key = (stage ?? "?") + ":" + Fnv1a64(spirv).ToString("x16");
            if (cache.TryGetValue(key, out string cached))
            {
                CacheHits++;
                if (cached.Length > 0)
                {
                    glsl = cached;
                    return true;
                }
                error = "spirv-cross failed for this module (cached)";
                return false;
            }

            string tempFile = Path.Combine(Path.GetTempPath(),
                "as_spv_" + Guid.NewGuid().ToString("N") + ".spv");
            try
            {
                File.WriteAllBytes(tempFile, spirv);

                // Prefer Vulkan semantics: it keeps image and sampler uniforms separate
                // (GL semantics merges them into anonymous `SPIRV_Cross_Combined`
                // placeholders that cannot be correlated with the serialized bindings)
                // and prints the descriptor set next to the binding.  Plain GL semantics
                // stays as the fallback for modules that require it.
                foreach (bool vulkanSemantics in new[] { true, false })
                {
                    var args = new StringBuilder();
                    if (stage != null)
                    {
                        args.Append("--stage ").Append(stage).Append(' ');
                    }
                    args.Append('"').Append(tempFile).Append('"');
                    if (vulkanSemantics)
                    {
                        args.Append(" --vulkan-semantics");
                    }

                    if (Run(exe, args.ToString(), out string stdout, out string stderr, out int exitCode))
                    {
                        glsl = stdout.TrimEnd() + "\n";
                        cache[key] = glsl;
                        CompileCount++;
                        return true;
                    }

                    error = string.IsNullOrWhiteSpace(stderr) ? "spirv-cross exited with " + exitCode
                                                              : stderr.Trim();
                    string lower = error.ToLowerInvariant();
                    if (!lower.Contains("vulkan semantics") && !lower.Contains("subgroup"))
                    {
                        break;
                    }
                }

                cache[key] = string.Empty;
                return false;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
            finally
            {
                try
                {
                    File.Delete(tempFile);
                }
                catch
                {
                    // best effort
                }
            }
        }

        private static bool Run(string exe, string args, out string stdout, out string stderr, out int exitCode)
        {
            stdout = string.Empty;
            stderr = string.Empty;
            exitCode = -1;

            var startInfo = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using (var process = new Process { StartInfo = startInfo })
            {
                var outBuilder = new StringBuilder();
                var errBuilder = new StringBuilder();
                process.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        lock (outBuilder)
                        {
                            outBuilder.Append(e.Data).Append('\n');
                        }
                    }
                };
                process.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        lock (errBuilder)
                        {
                            errBuilder.Append(e.Data).Append('\n');
                        }
                    }
                };

                if (!process.Start())
                {
                    stderr = "failed to start " + exe;
                    return false;
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (!process.WaitForExit(300000))
                {
                    try
                    {
                        process.Kill(true);
                    }
                    catch
                    {
                        // best effort
                    }
                    stderr = "spirv-cross timed out";
                    return false;
                }

                process.WaitForExit(); // let the async readers drain
                exitCode = process.ExitCode;
                lock (outBuilder)
                {
                    stdout = outBuilder.ToString();
                }
                lock (errBuilder)
                {
                    stderr = errBuilder.ToString();
                }
                return exitCode == 0 && stdout.Trim().Length > 0;
            }
        }

        internal static ulong Fnv1a64(byte[] data)
        {
            ulong hash = 0;
            foreach (byte b in data)
            {
                hash *= 0x100000001B3;
                hash ^= b;
            }
            return hash;
        }
    }
}
