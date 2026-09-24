using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json;

namespace AnimeStudio.CLI
{
    /// <summary>
    /// ACL 探针（环境变量 ANIMESTUDIO_ACL_PROBE=&lt;输出基名&gt; 触发）。
    ///
    /// 产出 <c>&lt;base&gt;.json</c>（元数据 + 绑定表）与 <c>&lt;base&gt;.bin</c>（float32 数值流）。
    ///
    /// 关键结论（已实测）：
    ///  · 三段缓冲都要**原样**喂给原生 <c>DecompressTracks</c>（不能跳过前 8 字节）；
    ///  · <c>TransformBufferData</c> 解出 qvvf 轨（每轨每采样 10 个 float：quat4+pos3+scale3）；
    ///  · <c>RootMotionBufferData</c> / <c>FloatBufferData</c> 解出 float1f 轨（每轨每采样 1 个 float）。
    /// </summary>
    public static class AclProbe
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeClip
        {
            public IntPtr Values;
            public int ValuesCount;
            public IntPtr Times;
            public int TimesCount;
        }

        [DllImport("AnimeStudio.ACL.DB", CallingConvention = CallingConvention.Cdecl)]
        private static extern void DecompressTracks(IntPtr data, IntPtr db, IntPtr streamer, ref NativeClip clip);

        [DllImport("AnimeStudio.ACL.DB", CallingConvention = CallingConvention.Cdecl)]
        private static extern void Dispose(ref NativeClip clip);

        private const int PAD = 1 << 16;

        private static (float[] v, float[] t, string diag) Decode(byte[] blob)
        {
            if (blob == null || blob.Length < 16)
                return (Array.Empty<float>(), Array.Empty<float>(), "empty");

            IntPtr rawD = Marshal.AllocHGlobal(blob.Length + PAD + 32);
            IntPtr rawB = Marshal.AllocHGlobal(64);
            var dPtr = new IntPtr(16 * (((long)rawD + 15) / 16));
            var bPtr = new IntPtr(16 * (((long)rawB + 15) / 16));
            Marshal.Copy(blob, 0, dPtr, blob.Length);

            var clip = new NativeClip();
            try
            {
                DecompressTracks(dPtr, bPtr, IntPtr.Zero, ref clip);
                int vc = clip.ValuesCount, tc = clip.TimesCount;
                var v = new float[Math.Max(0, vc)];
                var t = new float[Math.Max(0, tc)];
                if (vc > 0 && clip.Values != IntPtr.Zero) Marshal.Copy(clip.Values, v, 0, vc);
                if (tc > 0 && clip.Times != IntPtr.Zero) Marshal.Copy(clip.Times, t, 0, tc);
                if (vc > 0 || tc > 0) Dispose(ref clip);
                return (v, t, null);
            }
            catch (Exception e)
            {
                return (Array.Empty<float>(), Array.Empty<float>(), e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                Marshal.FreeHGlobal(rawD);
                Marshal.FreeHGlobal(rawB);
            }
        }

        public static void Run(string[] files, string outBase)
        {
            var index = new List<object>();
            var bin = new MemoryStream();
            var bw = new BinaryWriter(bin);
            var i = 0;
            int limit = 0;
            int.TryParse(Environment.GetEnvironmentVariable("ANIMESTUDIO_ACL_PROBE_LIMIT"), out limit);

            foreach (var file in files)
            {
                Studio.assetsManager.LoadFiles(file);
                if (Studio.assetsManager.assetsFileList.Count > 0)
                {
                    Studio.BuildAssetData(new[] { ClassIDType.AnimationClip }, null, null, ref i);
                    foreach (var item in Studio.exportableAssets)
                    {
                        if (!(item.Asset is AnimationClip clip)) continue;
                        try { index.Add(Probe(clip, bw)); }
                        catch (Exception e) { index.Add(new Dictionary<string, object> { ["name"] = clip.m_Name, ["error"] = e.ToString() }); }
                        if (limit > 0 && index.Count >= limit) break;
                    }
                }
                Studio.exportableAssets.Clear();
                Studio.assetsManager.Clear();
                if (limit > 0 && index.Count >= limit) break;
            }
            bw.Flush();
            File.WriteAllBytes(outBase + ".bin", bin.ToArray());
            File.WriteAllText(outBase + ".json", JsonConvert.SerializeObject(index, Formatting.Indented));
            Logger.Info($"[acl-probe] {index.Count} clips -> {outBase}.json/.bin ({bin.Length} B)");
        }

        private static Dictionary<string, object> Probe(AnimationClip clip, BinaryWriter bw)
        {
            var b = clip.m_AclCompressedBuffer;
            var (tv, tt, _) = Decode(b?.TransformBufferData);
            var (fv, ft, ferr) = Decode(b?.FloatBufferData);
            var (rv, rt, rerr) = Decode(b?.RootMotionBufferData);

            var rec = new Dictionary<string, object>
            {
                ["name"] = clip.m_Name,
                ["aclVersion"] = b?.Version ?? -999,
                ["sampleRate"] = clip.m_SampleRate,
                ["counts"] = new Dictionary<string, object>
                {
                    ["outputTrackCount"] = b?.OutputTrackCount ?? 0,
                    ["rootTrackCount"] = b?.RootTrackCount ?? 0,
                    ["floatCurveCount"] = b?.FloatCurveCount ?? 0,
                    ["constantIndexs"] = b?.m_ConstantIndexs?.Length ?? 0,
                    ["constantValues"] = b?.m_ConstantValues?.Length ?? 0,
                },
                ["acl"] = new Dictionary<string, object>
                {
                    ["transformValues"] = tv.Length,
                    ["transformTimes"] = tt.Length,
                    ["floatValues"] = fv.Length,
                    ["floatTimes"] = ft.Length,
                    ["floatErr"] = ferr,
                    ["rootValues"] = rv.Length,
                    ["rootTimes"] = rt.Length,
                    ["rootErr"] = rerr,
                },
            };

            var bl = new List<object>();
            if (clip.m_ClipBindingConstant?.genericBindings != null)
            {
                foreach (var g in clip.m_ClipBindingConstant.genericBindings)
                {
                    bl.Add(new Dictionary<string, object>
                    {
                        ["p"] = g.path,
                        ["a"] = g.attribute,
                        ["c"] = g.customType,
                        ["t"] = (int)g.typeID,
                    });
                }
            }
            rec["bindings"] = bl;

            var offsets = new Dictionary<string, object>();
            offsets["times"] = bw.BaseStream.Position;
            foreach (var x in tt) bw.Write(x);
            offsets["transform"] = bw.BaseStream.Position;
            foreach (var x in tv) bw.Write(x);
            offsets["float"] = bw.BaseStream.Position;
            foreach (var x in fv) bw.Write(x);
            offsets["root"] = bw.BaseStream.Position;
            foreach (var x in rv) bw.Write(x);
            rec["bin"] = offsets;

            if (b?.m_ConstantIndexs != null && b.m_ConstantIndexs.Length > 0)
            {
                var ci = new ushort[Math.Min(64, b.m_ConstantIndexs.Length)];
                Array.Copy(b.m_ConstantIndexs, ci, ci.Length);
                rec["constantIndexsHead"] = ci.Select(x => (int)x).ToList();
                var cv = new float[Math.Min(64, b.m_ConstantValues?.Length ?? 0)];
                if (b.m_ConstantValues != null) Array.Copy(b.m_ConstantValues, cv, cv.Length);
                rec["constantValuesHead"] = cv.Select(x => Math.Round(x, 5)).ToList();
            }
            if (b?.TransformSubTrackMasks != null && b.TransformSubTrackMasks.Length > 0)
                rec["subTrackMasksHead"] = BitConverter.ToString(b.TransformSubTrackMasks, 0, Math.Min(24, b.TransformSubTrackMasks.Length)).Replace("-", " ");
            return rec;
        }
    }
}
