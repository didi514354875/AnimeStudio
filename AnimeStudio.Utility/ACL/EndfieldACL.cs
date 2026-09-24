using System;
using System.Runtime.InteropServices;
using AnimeStudio.PInvoke;

namespace ACLLibs
{
    /// <summary>
    /// endfield 的 ACL 缓冲解码。
    ///
    /// 数据在 <c>AnimationClip.m_AclCompressedBuffer</c>（不是 <c>m_Clip.m_ACLClip</c>，后者在 endfield 是 EmptyACLClip）：
    /// <list type="bullet">
    /// <item><c>TransformBufferData</c> —— qvvf 轨：每轨每采样 <b>10</b> 个 float = 四元数4 + 位置3 + 缩放3</item>
    /// <item><c>FloatBufferData</c> —— float1f 轨：每轨每采样 <b>1</b> 个 float</item>
    /// <item><c>RootMotionBufferData</c> —— float1f 轨，实测**等于 FloatBufferData 的前 28 轨**（冗余）</item>
    /// </list>
    ///
    /// ★ 必须把**整段**缓冲原样交给原生 <c>DecompressTracks</c>：跳过「[u32 size][u32 hash]」这 8 字节
    /// 反而会让解析失败（实测）。尾部补零是为了让原生那次“顺带解析第二个 compressed_tracks”立刻失败，
    /// 从而只解出我们喂进去的那一段。
    ///
    /// 缓冲头（实测）：<c>[u32 totalSize][u32 hash][11 ac 11 ac][u16 version=10][...][u32 num_tracks][u32 num_samples][f32 sample_rate]</c>
    /// —— version 10 = ACL v02_01_00，正好是本仓库 vendored ACL 支持的版本。
    /// </summary>
    public static class EndfieldACL
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

        private const int Pad = 1 << 16;

        static EndfieldACL()
        {
            try { DllLoader.PreloadDll("AnimeStudio.ACL.DB", archSpecific: false); }
            catch { /* 交给 P/Invoke 默认解析 */ }
        }

        /// <summary>解码一段 ACL 缓冲；成功时 values/times 为定长数组。</summary>
        public static bool TryDecode(byte[] blob, out float[] values, out float[] times)
        {
            values = Array.Empty<float>();
            times = Array.Empty<float>();
            if (blob == null || blob.Length < 16)
            {
                return false;
            }

            var rawD = Marshal.AllocHGlobal(blob.Length + Pad + 32);
            var rawB = Marshal.AllocHGlobal(64);
            var dPtr = new IntPtr(16 * (((long)rawD + 15) / 16));
            var bPtr = new IntPtr(16 * (((long)rawB + 15) / 16));
            try
            {
                Marshal.Copy(blob, 0, dPtr, blob.Length);   // ★ 原样，不跳过前 8 字节
                var clip = new NativeClip();
                DecompressTracks(dPtr, bPtr, IntPtr.Zero, ref clip);
                if (clip.ValuesCount <= 0 || clip.Values == IntPtr.Zero)
                {
                    return false;
                }

                values = new float[clip.ValuesCount];
                Marshal.Copy(clip.Values, values, 0, clip.ValuesCount);
                if (clip.TimesCount > 0 && clip.Times != IntPtr.Zero)
                {
                    times = new float[clip.TimesCount];
                    Marshal.Copy(clip.Times, times, 0, clip.TimesCount);
                }
                Dispose(ref clip);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(rawD);
                Marshal.FreeHGlobal(rawB);
            }
        }
    }
}
