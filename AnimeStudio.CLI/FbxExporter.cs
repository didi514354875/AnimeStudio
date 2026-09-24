// FBX 导出（ASCII FBX 7.4）—— 目标：导出的 .fbx 拖进 Unity 后
//   ① 模型/骨骼层级与 prefab 一致；② Unity 能**自动生成 Avatar**。
//
// 为什么要自己写：Unity 只能从「带骨骼与蒙皮的模型文件」自动生成 Avatar（OBJ 不行，
// OBJ 只有 v/vn/vt/f）。FBX 是 Unity 原生支持的格式。
//
// 两个必须实测校准的点（都用环境变量留了开关，靠 Unity 导入结果来定）：
//   * 轴向：FBX 是右手系、Unity 是左手系。Unity 官方 FBX Exporter 的做法是
//     **把 X 取反**（位置 x→-x；四元数 (x,y,z,w)→(x,-y,-z,w)，因为旋转轴是赝矢量）。
//     `FBX_AXIS=x`(默认) | `z` | `none`
//   * 欧拉序：FBX 的 `Lcl Rotation` 是欧拉角（度）。`FBX_EULER=RzRyRx`(默认) | `RxRyRz`
//
// 结构（对照 Blender/Maya 导出的 ASCII FBX）：
//   FBXHeaderExtension / GlobalSettings / Documents / Definitions / Objects / Connections
//   每个 Transform → 一个 Model 节点（骨骼用 "LimbNode"，容器用 "Null"）
//   每个 SkinnedMeshRenderer → Geometry + Deformer(Skin) + 每骨骼一个 Cluster
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AnimeStudio
{
    public static class FbxExporter
    {
        // ── 自带行主序 4x4（避免和列主序存储混淆；只做 TRS/乘/求逆）────────
        public struct Mat4
        {
            public float[] M;                     // row-major: M[r*4+c]
            public static Mat4 Identity()
            {
                var m = new float[16];
                m[0] = m[5] = m[10] = m[15] = 1f;
                return new Mat4 { M = m };
            }
            public float this[int r, int c] { get => M[r * 4 + c]; set => M[r * 4 + c] = value; }

            public static Mat4 FromAnimeStudio(Matrix4x4 m)
            {
                var o = new Mat4 { M = new float[16] };
                for (int r = 0; r < 4; r++)
                    for (int c = 0; c < 4; c++)
                        o.M[r * 4 + c] = m[r, c];     // 用索引器，绕开存储序
                return o;
            }

            public static Mat4 TRS(Vector3 t, Quaternion q, Vector3 s)
            {
                float x = q.X, y = q.Y, z = q.Z, w = q.W;
                float n = (float)Math.Sqrt(x * x + y * y + z * z + w * w);
                if (n < 1e-12f) { x = 0; y = 0; z = 0; w = 1; } else { x /= n; y /= n; z /= n; w /= n; }
                float xx = x * x, yy = y * y, zz = z * z;
                float xy = x * y, xz = x * z, yz = y * z, wx = w * x, wy = w * y, wz = w * z;
                var o = Identity();
                o[0, 0] = (1 - 2 * (yy + zz)) * s.X; o[0, 1] = (2 * (xy - wz)) * s.Y;       o[0, 2] = (2 * (xz + wy)) * s.Z;       o[0, 3] = t.X;
                o[1, 0] = (2 * (xy + wz)) * s.X;     o[1, 1] = (1 - 2 * (xx + zz)) * s.Y;   o[1, 2] = (2 * (yz - wx)) * s.Z;       o[1, 3] = t.Y;
                o[2, 0] = (2 * (xz - wy)) * s.X;     o[2, 1] = (2 * (yz + wx)) * s.Y;       o[2, 2] = (1 - 2 * (xx + yy)) * s.Z;   o[2, 3] = t.Z;
                return o;
            }

            public static Mat4 Mul(Mat4 a, Mat4 b)
            {
                var o = new Mat4 { M = new float[16] };
                for (int r = 0; r < 4; r++)
                    for (int c = 0; c < 4; c++)
                    {
                        float sum = 0;
                        for (int k = 0; k < 4; k++) sum += a.M[r * 4 + k] * b.M[k * 4 + c];
                        o.M[r * 4 + c] = sum;
                    }
                return o;
            }

            /// <summary>通用 4x4 求逆（余子式展开；最后一行为 0,0,0,1）。</summary>
            public static Mat4 Inverse(Mat4 m)
            {
                var inv = new float[16];
                inv[0] = m.M[5] * m.M[10] * m.M[15] - m.M[5] * m.M[11] * m.M[14] - m.M[9] * m.M[6] * m.M[15]
                       + m.M[9] * m.M[7] * m.M[14] + m.M[13] * m.M[6] * m.M[11] - m.M[13] * m.M[7] * m.M[10];
                inv[4] = -m.M[4] * m.M[10] * m.M[15] + m.M[4] * m.M[11] * m.M[14] + m.M[8] * m.M[6] * m.M[15]
                       - m.M[8] * m.M[7] * m.M[14] - m.M[12] * m.M[6] * m.M[11] + m.M[12] * m.M[7] * m.M[10];
                inv[8] = m.M[4] * m.M[9] * m.M[15] - m.M[4] * m.M[11] * m.M[13] - m.M[8] * m.M[5] * m.M[15]
                       + m.M[8] * m.M[7] * m.M[13] + m.M[12] * m.M[5] * m.M[11] - m.M[12] * m.M[7] * m.M[9];
                inv[12] = -m.M[4] * m.M[9] * m.M[14] + m.M[4] * m.M[10] * m.M[13] + m.M[8] * m.M[5] * m.M[14]
                       - m.M[8] * m.M[6] * m.M[13] - m.M[12] * m.M[5] * m.M[10] + m.M[12] * m.M[6] * m.M[9];
                inv[1] = -m.M[1] * m.M[10] * m.M[15] + m.M[1] * m.M[11] * m.M[14] + m.M[9] * m.M[2] * m.M[15]
                       - m.M[9] * m.M[3] * m.M[14] - m.M[13] * m.M[2] * m.M[11] + m.M[13] * m.M[3] * m.M[10];
                inv[5] = m.M[0] * m.M[10] * m.M[15] - m.M[0] * m.M[11] * m.M[14] - m.M[8] * m.M[2] * m.M[15]
                       + m.M[8] * m.M[3] * m.M[14] + m.M[12] * m.M[2] * m.M[11] - m.M[12] * m.M[3] * m.M[10];
                inv[9] = -m.M[0] * m.M[9] * m.M[15] + m.M[0] * m.M[11] * m.M[13] + m.M[8] * m.M[1] * m.M[15]
                       - m.M[8] * m.M[3] * m.M[13] - m.M[12] * m.M[1] * m.M[11] + m.M[12] * m.M[3] * m.M[9];
                inv[13] = m.M[0] * m.M[9] * m.M[14] - m.M[0] * m.M[10] * m.M[13] - m.M[8] * m.M[1] * m.M[14]
                       + m.M[8] * m.M[2] * m.M[13] + m.M[12] * m.M[1] * m.M[10] - m.M[12] * m.M[2] * m.M[9];
                inv[2] = m.M[1] * m.M[6] * m.M[15] - m.M[1] * m.M[7] * m.M[14] - m.M[5] * m.M[2] * m.M[15]
                       + m.M[5] * m.M[3] * m.M[14] + m.M[13] * m.M[2] * m.M[7] - m.M[13] * m.M[3] * m.M[6];
                inv[6] = -m.M[0] * m.M[6] * m.M[15] + m.M[0] * m.M[7] * m.M[14] + m.M[4] * m.M[2] * m.M[15]
                       - m.M[4] * m.M[3] * m.M[14] - m.M[12] * m.M[2] * m.M[7] + m.M[12] * m.M[3] * m.M[6];
                inv[10] = m.M[0] * m.M[5] * m.M[15] - m.M[0] * m.M[7] * m.M[13] - m.M[4] * m.M[1] * m.M[15]
                       + m.M[4] * m.M[3] * m.M[13] + m.M[12] * m.M[1] * m.M[7] - m.M[12] * m.M[3] * m.M[5];
                inv[14] = -m.M[0] * m.M[5] * m.M[14] + m.M[0] * m.M[6] * m.M[13] + m.M[4] * m.M[1] * m.M[14]
                       - m.M[4] * m.M[2] * m.M[13] - m.M[12] * m.M[1] * m.M[6] + m.M[12] * m.M[2] * m.M[5];
                inv[3] = -m.M[1] * m.M[6] * m.M[11] + m.M[1] * m.M[7] * m.M[10] + m.M[5] * m.M[2] * m.M[11]
                       - m.M[5] * m.M[3] * m.M[10] - m.M[9] * m.M[2] * m.M[7] + m.M[9] * m.M[3] * m.M[6];
                inv[7] = m.M[0] * m.M[6] * m.M[11] - m.M[0] * m.M[7] * m.M[10] - m.M[4] * m.M[2] * m.M[11]
                       + m.M[4] * m.M[3] * m.M[10] + m.M[8] * m.M[2] * m.M[7] - m.M[8] * m.M[3] * m.M[6];
                inv[11] = -m.M[0] * m.M[5] * m.M[11] + m.M[0] * m.M[7] * m.M[9] + m.M[4] * m.M[1] * m.M[11]
                       - m.M[4] * m.M[3] * m.M[9] - m.M[8] * m.M[1] * m.M[7] + m.M[8] * m.M[3] * m.M[5];
                inv[15] = m.M[0] * m.M[5] * m.M[10] - m.M[0] * m.M[6] * m.M[9] - m.M[4] * m.M[1] * m.M[10]
                       + m.M[4] * m.M[2] * m.M[9] + m.M[8] * m.M[1] * m.M[6] - m.M[8] * m.M[2] * m.M[5];

                float det = m.M[0] * inv[0] + m.M[1] * inv[4] + m.M[2] * inv[8] + m.M[3] * inv[12];
                var o = new Mat4 { M = new float[16] };
                if (Math.Abs(det) < 1e-20f) return Identity();
                float id = 1f / det;
                for (int i = 0; i < 16; i++) o.M[i] = inv[i] * id;
                return o;
            }
        }

        // ── 需要的最小数据 ───────────────────────────────────────────────
        public sealed class Node
        {
            public string Name;
            public Vector3 T;
            public Quaternion R;
            public Vector3 S;
            public Node Parent;
            public List<Node> Children = new List<Node>();
            public int Id;
            public Mat4 World;               // 镜像后（FBX 空间）的世界矩阵
        }

        public sealed class MeshArrays
        {
            public string Name;
            public int VertexCount;
            public Vector3[] Positions;
            public Vector3[] Normals;        // 可空
            public Vector2[] Uvs;            // 可空
            public byte[] BoneIndices;       // 4/顶点
            public ushort[] BoneWeights;     // 4/顶点（原始整型）
            public int WeightFormat;         // 0=float32, 4=unorm16, 2=unorm8, 5=snorm16 …
            public int[] Indices;            // 三角形列表
        }

        public sealed class Skin
        {
            public Node MeshNode;            // SkinnedMeshRenderer 所在节点
            public MeshArrays Mesh;
            public Node[] Bones;
            public int[] BoneSlots;          // ★ Bones[i] 对应源 m_Bones 的下标（顶点 boneIndex 的空间）
            public Node Fallback;            // 零权重顶点的归属骨骼（SMR 的 rootBone，退而求其次 Bones[0]）
            public Matrix4x4[] Bindposes;    // Unity 约定：mesh-local → bone-local
        }

        // ── 轴向 ─────────────────────────────────────────────────────────
        static float[] _sign = { -1f, 1f, 1f };      // 默认 X 取反
        static bool _swap = false;

        static FbxExporter()
        {
            var axis = (Environment.GetEnvironmentVariable("FBX_AXIS") ?? "x").ToLowerInvariant();
            switch (axis)
            {
                case "none": _sign = new[] { 1f, 1f, 1f }; break;
                case "z": _sign = new[] { 1f, 1f, -1f }; break;
                default: _sign = new[] { -1f, 1f, 1f }; break;
            }
        }

        static Vector3 Mirror(Vector3 v) => new Vector3(v.X * _sign[0], v.Y * _sign[1], v.Z * _sign[2]);

        /// <summary>反射共轭：S·M·S（S = diag(sign)）。旋转轴是赝矢量，所以 x/y/z 要乘 det·sign。</summary>
        static Quaternion Mirror(Quaternion q)
        {
            float det = _sign[0] * _sign[1] * _sign[2];
            return new Quaternion(q.X * _sign[0] * det, q.Y * _sign[1] * det, q.Z * _sign[2] * det, q.W);
        }

        // ── mesh 空间 → 节点层级空间 ─────────────────────────────────
        // 源资产的 mesh 顶点/bindpose 是 **Z-up**（DCC 空间），而节点层级（prefab Transform）
        // 是 **Y-up**。两者差一个固定旋转：
        //     层级 → mesh：(x,y,z) -> (-z,-x,y)      （欧拉 90/0/-90）
        //     mesh → 层级：(x,y,z) -> (-y, z,-x)      ← 本函数
        // 不补这一步 ⇒ FBX 里 mesh 躺着、且蒙皮把两套空间混在一起（mesh 不跟骨骼动）。
        // 证据：Cluster 的 TransformLink 平移与该骨骼在节点层级里的世界位置正好差这个旋转
        //（如 Bip001_L_ForeTwist1：TL=(0.3205,0.0226,1.0818) vs 层级 (-0.0246,1.0818,-0.3184)）。
        static Vector3 ToNodeSpace(Vector3 v)
        {
            var m = Mirror(v);
            return new Vector3(-m.Y, m.Z, -m.X);
        }

        /// <summary>反射共轭 S·M·S（行主序）。S = diag(sign...,1)。</summary>
        static Mat4 MirrorM(Mat4 m)
        {
            float[] sg = { _sign[0], _sign[1], _sign[2], 1f };
            var o = new Mat4 { M = new float[16] };
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    o.M[r * 4 + c] = sg[r] * m.M[r * 4 + c] * sg[c];
            return o;
        }

        static string F(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) v = 0f;
            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        static string FA(IEnumerable<float> vs) => string.Join(",", vs.Select(F));

        // ── 欧拉角（度）─────────────────────────────────────────────────
        static float[] QuatToEulerXYZ(Quaternion q)
        {
            // 归一化
            double n = Math.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
            if (n < 1e-12) return new float[] { 0, 0, 0 };
            double x = q.X / n, y = q.Y / n, z = q.Z / n, w = q.W / n;

            // 旋转矩阵（行主序）
            double m00 = 1 - 2 * (y * y + z * z), m01 = 2 * (x * y - z * w), m02 = 2 * (x * z + y * w);
            double m10 = 2 * (x * y + z * w), m12 = 2 * (y * z - x * w), m20 = 2 * (x * z - y * w), m21 = 2 * (y * z + x * w), m22 = 1 - 2 * (x * x + y * y);

            var order = (Environment.GetEnvironmentVariable("FBX_EULER") ?? "RzRyRx").Trim();
            double rx, ry, rz;
            if (order.Equals("RxRyRz", StringComparison.OrdinalIgnoreCase))
            {
                // M = Rx·Ry·Rz  ⇒  m02 = sin(y)
                ry = Math.Asin(Math.Max(-1.0, Math.Min(1.0, m02)));
                rx = Math.Atan2(-m12, m22);
                rz = Math.Atan2(-m01, m00);
            }
            else
            {
                // M = Rz·Ry·Rx（FBX eEulerXYZ 的常见实现）⇒  m20 = -sin(y)
                ry = Math.Asin(Math.Max(-1.0, Math.Min(1.0, -m20)));
                rx = Math.Atan2(m21, m22);
                rz = Math.Atan2(m10, m00);
            }
            const double R2D = 180.0 / Math.PI;
            return new[] { (float)(rx * R2D), (float)(ry * R2D), (float)(rz * R2D) };
        }

        // ── 主入口 ───────────────────────────────────────────────────────
        public static void Write(string filePath, Node root, List<Skin> skins, int upAxisY = 1)
        {
            // 1) 赋 ID 并算世界矩阵（镜像空间内）
            // ★ 对象 ID 必须**全局唯一**：Documents 里已经占用了 1，
            //   如果 Model 也从 1 开始就会撞号（FBX SDK 会判文件不一致 ⇒ 读成空模型）。
            //   参照文件用的是几十亿的大随机数；这里统一抬到 1000000 起。
            int next = 1000000;
            var all = new List<Node>();
            void Number(Node n, Mat4 parentWorld)
            {
                n.Id = next++;
                n.T = Mirror(n.T);
                n.R = Mirror(n.R);
                // 注意 S 是缩放，镜像不会改它（负缩放由层次本身表达）
                n.World = Mat4.Mul(parentWorld, Mat4.TRS(n.T, n.R, n.S));
                all.Add(n);
                foreach (var c in n.Children) { c.Parent = n; Number(c, n.World); }
            }
            Number(root, Mat4.Identity());

            var bonesOf = new HashSet<Node>();
            foreach (var s in skins) foreach (var b in s.Bones) if (b != null) bonesOf.Add(b);

            // 2) 编号：Geometry / Skin / Cluster
            int geomStart = next;
            var geomIds = new Dictionary<Skin, int>();
            int gid = geomStart;
            foreach (var s in skins) { geomIds[s] = gid++; }
            int skinStart = gid;
            var skinIds = new Dictionary<Skin, int>();
            foreach (var s in skins) skinIds[s] = gid++;
            int clusterStart = gid;
            var clusterIds = new List<(Skin skin, int boneIdx, int id)>();
            foreach (var s in skins)
                for (int i = 0; i < s.Bones.Length; i++)
                    clusterIds.Add((s, i, gid++));
            int totalObjects = (next - 1) + skins.Count * 2 + clusterIds.Count;

            var sb = new StringBuilder(1 << 20);
            sb.Append("; FBX 7.4.0 project file\n; Exported by AnimeStudio\n; ----------------------------------------------------\n\n");

            // ── FBXHeaderExtension ──
            sb.Append("FBXHeaderExtension:  {\n");
            sb.Append("\tFBXHeaderVersion: 1003\n\tFBXVersion: 7400\n");
            sb.Append("\tCreationTimeStamp:  {\n\t\tVersion: 1000\n\t\tYear: 2024\n\t\tMonth: 1\n\t\tDay: 1\n\t\tHour: 0\n\t\tMinute: 0\n\t\tSecond: 0\n\t\tMillisecond: 0\n\t}\n");
            sb.Append("\tCreator: \"AnimeStudio FBX Exporter\"\n}\n");

            // ── GlobalSettings ──
            sb.Append("GlobalSettings:  {\n\tVersion: 1000\n\tProperties70:  {\n");
            sb.Append("\t\tP: \"UpAxis\", \"int\", \"Integer\", \"\",1\n");
            sb.Append("\t\tP: \"UpAxisSign\", \"int\", \"Integer\", \"\",1\n");
            sb.Append("\t\tP: \"FrontAxis\", \"int\", \"Integer\", \"\",2\n");
            sb.Append("\t\tP: \"FrontAxisSign\", \"int\", \"Integer\", \"\",1\n");
            sb.Append("\t\tP: \"CoordAxis\", \"int\", \"Integer\", \"\",0\n");
            sb.Append("\t\tP: \"CoordAxisSign\", \"int\", \"Integer\", \"\",1\n");
            sb.Append("\t\tP: \"OriginalUpAxis\", \"int\", \"Integer\", \"\",1\n");
            sb.Append("\t\tP: \"OriginalUpAxisSign\", \"int\", \"Integer\", \"\",1\n");
            // ★ FBX 默认单位是厘米。UnitScaleFactor=1 表示「1 文件单位 = 1 cm」，
            //   Unity 导入时会乘 0.01 ⇒ 所有位置缩小 100 倍（实测 Bip001 y=0.9459 → 0.0095）。
            //   我们的数据以米为单位 ⇒ 必须写 100（1 文件单位 = 100 cm = 1 m）。
            sb.Append("\t\tP: \"UnitScaleFactor\", \"double\", \"Number\", \"\",100\n");
            sb.Append("\t\tP: \"OriginalUnitScaleFactor\", \"double\", \"Number\", \"\",100\n");
            sb.Append("\t}\n}\n");

            // ── Documents ──
            sb.Append("Documents:  {\n\tCount: 1\n\tDocument: 1, \"Scene\", \"Scene\" {\n");
            sb.Append("\t\tProperties70:  {\n\t\t\tP: \"SourceObject\", \"object\", \"\", \"\"\n\t\t}\n");
            sb.Append("\t\tRootNode: 0\n\t}\n}\n");

            // ── References ──
            sb.Append("References:  {\n}\n");

            // ── Definitions ──
            sb.Append("Definitions:  {\n\tVersion: 100\n");
            sb.Append("\tCount: " + totalObjects + "\n");
            sb.Append("\tObjectType: \"Model\" {\n\t\tCount: " + (next - geomStart >= 0 ? next - 1 : next - 1) + "\n\t}\n");
            sb.Append("\tObjectType: \"Geometry\" {\n\t\tCount: " + skins.Count + "\n\t}\n");
            sb.Append("\tObjectType: \"Deformer\" {\n\t\tCount: " + (skins.Count + clusterIds.Count) + "\n\t}\n");
            sb.Append("}\n");

            // ── Objects ──
            sb.Append("Objects:  {\n");

            // 几何 + 蒙皮（ANIMESTUDIO_FBX_NOGEO=1 时只导骨架，用于隔离定位
            //  「Cluster 的 Transform/TransformLink 约定」是否影响骨骼变换）
            bool noGeo = Environment.GetEnvironmentVariable("ANIMESTUDIO_FBX_NOGEO") == "1";
            if (!noGeo)
            {
                foreach (var s in skins)
                {
                    WriteGeometry(sb, s, geomIds[s]);
                    WriteSkin(sb, s, skinIds[s], clusterIds.Where(x => x.skin == s).ToList(), clusterStart);
                }
            }

            // 节点
            foreach (var n in all)
            {
                bool isBone = bonesOf.Contains(n);
                sb.Append("\tModel: " + n.Id + ", \"Model::" + Esc(n.Name) + "\", \"" + (isBone ? "LimbNode" : "Null") + "\" {\n");
                sb.Append("\t\tVersion: 232\n");
                sb.Append("\t\tProperties70:  {\n");
                var e = QuatToEulerXYZ(n.R);
                // 这些是参照（Blender 导出的 ASCII FBX）里每个 Model 都有的，缺了 SDK 可能不认
                sb.Append("\t\t\tP: \"RotationActive\", \"bool\", \"\", \"\",1\n");
                sb.Append("\t\t\tP: \"InheritType\", \"enum\", \"\", \"\",1\n");
                sb.Append("\t\t\tP: \"ScalingMax\", \"Vector3D\", \"Vector\", \"\",0,0,0\n");
                sb.Append("\t\t\tP: \"DefaultAttributeIndex\", \"int\", \"Integer\", \"\",0\n");
                sb.Append("\t\t\tP: \"Lcl Translation\", \"Lcl Translation\", \"\", \"A\"," + F(n.T.X) + "," + F(n.T.Y) + "," + F(n.T.Z) + "\n");
                sb.Append("\t\t\tP: \"Lcl Rotation\", \"Lcl Rotation\", \"\", \"A\"," + F(e[0]) + "," + F(e[1]) + "," + F(e[2]) + "\n");
                sb.Append("\t\t\tP: \"Lcl Scaling\", \"Lcl Scaling\", \"\", \"A\"," + F(n.S.X) + "," + F(n.S.Y) + "," + F(n.S.Z) + "\n");
                sb.Append("\t\t\tP: \"RotationOrder\", \"enum\", \"\", \"\",0\n");
                sb.Append("\t\t\tP: \"currentUVSet\", \"KString\", \"\", \"U\", \"map1\"\n");
                sb.Append("\t\t}\n");
                sb.Append("\t\tShading: T\n\t\tCulling: \"CullingOff\"\n\t}\n");
            }
            sb.Append("}\n");

            // ── Connections ──
            sb.Append("Connections:  {\n");
            sb.Append("\t;Model::Root, Model::Scene\n");
            sb.Append("\tC: \"OO\"," + root.Id + ",0\n");
            foreach (var n in all)
            {
                if (n.Parent == null) continue;
                sb.Append("\tC: \"OO\"," + n.Id + "," + n.Parent.Id + "\n");
            }
            foreach (var s in skins)
            {
                // Geometry → 网格节点
                sb.Append("\tC: \"OO\"," + geomIds[s] + "," + s.MeshNode.Id + "\n");
                // Skin → Geometry
                sb.Append("\tC: \"OO\"," + skinIds[s] + "," + geomIds[s] + "\n");
                foreach (var (sk, bi, cid) in clusterIds.Where(x => x.skin == s))
                {
                    sb.Append("\tC: \"OO\"," + cid + "," + skinIds[s] + "\n");      // Cluster → Skin
                    // ★ 骨骼 → Cluster（不是反过来）。FBX SDK 用 FbxCluster::SetLink(bone) 序列化，
                    //   连线方向是 Model → Cluster。写反 ⇒ Unity 不生成 SkinnedMeshRenderer。
                    //   对照：Tuanjie 自带 Fox_Run_InPlace.fbx 的 39 个 Cluster 全是 dst=Model。
                    sb.Append("\tC: \"OO\"," + sk.Bones[bi].Id + "," + cid + "\n"); // 骨骼 Model → Cluster
                }
            }
            sb.Append("}\n");

            // ★ ASCII FBX 末尾必须有 Takes 段（参照里就有；没有会被解析器判为不完整）
            sb.Append(";Takes section\n;----------------------------------------------------\n\n");
            sb.Append("Takes:  {\n\tCurrent: \"Take 001\"\n\tTake: \"Take 001\" {\n");
            sb.Append("\t\tFileName: \"Take_001.tak\"\n\t\tLocalTime: 1924423250,230930790000\n");
            sb.Append("\t\tReferenceTime: 1924423250,230930790000\n\t}\n}\n");

            File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(false));
        }

        static void WriteGeometry(StringBuilder sb, Skin s, int id)
        {
            var m = s.Mesh;
            int nv = m.VertexCount;
            int tri = m.Indices.Length / 3;

            sb.Append("\tGeometry: " + id + ", \"Geometry::" + Esc(m.Name) + "\", \"Mesh\" {\n");
            sb.Append("\t\tVertices: *" + (nv * 3) + " {\n\t\t\ta: ");
            var pos = new List<float>(nv * 3);
            for (int i = 0; i < nv; i++) { var p = ToNodeSpace(m.Positions[i]); pos.Add(p.X); pos.Add(p.Y); pos.Add(p.Z); }
            sb.Append(FA(pos));
            sb.Append("\n\t\t} \n");

            // PolygonVertexIndex：每个多边形最后一个索引取按位取反（~i）
            sb.Append("\t\tPolygonVertexIndex: *" + (tri * 3) + " {\n\t\t\ta: ");
            var idx = new List<int>(tri * 3);
            for (int t = 0; t < tri; t++)
            {
                idx.Add(m.Indices[t * 3 + 0]);
                idx.Add(m.Indices[t * 3 + 1]);
                idx.Add(~m.Indices[t * 3 + 2]);
            }
            sb.Append(string.Join(",", idx.Select(i => i.ToString(CultureInfo.InvariantCulture))));
            sb.Append("\n\t\t} \n");

            sb.Append("\t\tGeometryVersion: 124\n");

            // 法线：按多边形顶点、直接值
            if (m.Normals != null && m.Normals.Length >= nv)
            {
                sb.Append("\t\tLayerElementNormal: 0 {\n\t\t\tVersion: 102\n\t\t\tName: \"\"\n");
                sb.Append("\t\t\tMappingInformationType: \"ByPolygonVertex\"\n\t\t\tReferenceInformationType: \"Direct\"\n");
                sb.Append("\t\t\tNormals: *" + (tri * 9) + " {\n\t\t\t\ta: ");
                var ns = new List<float>(tri * 9);
                for (int t = 0; t < tri; t++)
                    for (int k = 0; k < 3; k++)
                    {
                        var n = ToNodeSpace(m.Normals[m.Indices[t * 3 + k]]);
                        ns.Add(n.X); ns.Add(n.Y); ns.Add(n.Z);
                    }
                sb.Append(FA(ns));
                sb.Append("\n\t\t\t} \n\t\t}\n");
            }

            // UV：索引-直接
            if (m.Uvs != null && m.Uvs.Length >= nv)
            {
                sb.Append("\t\tLayerElementUV: 0 {\n\t\t\tVersion: 101\n\t\t\tName: \"UVMap\"\n");
                sb.Append("\t\t\tMappingInformationType: \"ByPolygonVertex\"\n\t\t\tReferenceInformationType: \"IndexToDirect\"\n");
                sb.Append("\t\t\tUV: *" + (nv * 2) + " {\n\t\t\t\ta: ");
                var uvs = new List<float>(nv * 2);
                for (int i = 0; i < nv; i++) { uvs.Add(m.Uvs[i].X); uvs.Add(m.Uvs[i].Y); }
                sb.Append(FA(uvs));
                sb.Append("\n\t\t\t} \n");
                sb.Append("\t\t\tUVIndex: *" + (tri * 3) + " {\n\t\t\t\ta: ");
                sb.Append(string.Join(",", Enumerable.Range(0, tri * 3).Select(k => m.Indices[k].ToString(CultureInfo.InvariantCulture))));
                sb.Append("\n\t\t\t} \n\t\t}\n");
            }

            // ★ 必须是 AllSame：用 ByPolygon 时 SDK 要求「每个多边形一个材质索引」，
            //   而这里只给 1 个 ⇒ 数量不匹配 ⇒ 整个 FBX 被判无效（Unity 读成空模型）。
            sb.Append("\t\tLayerElementMaterial: 0 {\n\t\t\tVersion: 101\n\t\t\tName: \"\"\n");
            sb.Append("\t\t\tMappingInformationType: \"AllSame\"\n\t\t\tReferenceInformationType: \"IndexToDirect\"\n");
            sb.Append("\t\t\tMaterials: *1 {\n\t\t\t\ta: 0\n\t\t\t} \n\t\t}\n");

            sb.Append("\t\tLayer: 0 {\n\t\t\tVersion: 100\n");
            if (m.Normals != null) sb.Append("\t\t\tLayerElement:  {\n\t\t\t\tType: \"LayerElementNormal\"\n\t\t\t\tTypedIndex: 0\n\t\t\t}\n");
            if (m.Uvs != null) sb.Append("\t\t\tLayerElement:  {\n\t\t\t\tType: \"LayerElementUV\"\n\t\t\t\tTypedIndex: 0\n\t\t\t}\n");
            sb.Append("\t\t\tLayerElement:  {\n\t\t\t\tType: \"LayerElementMaterial\"\n\t\t\t\tTypedIndex: 0\n\t\t\t}\n");
            sb.Append("\t\t}\n\t}\n");
        }

        static void WriteSkin(StringBuilder sb, Skin s, int skinId, List<(Skin skin, int boneIdx, int id)> clusters, int dummy)
        {
            sb.Append("\tDeformer: " + skinId + ", \"Deformer::Skin\", \"Skin\" {\n");
            sb.Append("\t\tVersion: 101\n\t\tLink_DeformAcuracy: 50\n\t}\n");

            // ★ 顶点 boneIndex 的空间是「源 m_Bones 下标」，必须用 BoneSlots 对齐；
            //   直接拿 bi（Cluster 序号）比较，在骨骼被丢弃时会错位 ⇒ 权重错挂/整批丢失。
            int nb = s.Bones.Length;
            var slotToBi = new Dictionary<int, int>();
            for (int bi = 0; bi < nb; bi++) slotToBi[s.BoneSlots[bi]] = bi;

            var verts = new List<int>[nb];
            var wts = new List<float>[nb];
            for (int bi = 0; bi < nb; bi++) { verts[bi] = new List<int>(); wts[bi] = new List<float>(); }
            var assigned = new bool[s.Mesh.VertexCount];

            for (int v = 0; v < s.Mesh.VertexCount; v++)
            {
                for (int k = 0; k < 4; k++)
                {
                    int idx = s.Mesh.BoneIndices[v * 4 + k];
                    float w = WeightAt(s.Mesh, v, k);
                    if (w <= 0f) continue;
                    if (!slotToBi.TryGetValue(idx, out var bi)) continue;
                    verts[bi].Add(v); wts[bi].Add(w);
                    assigned[v] = true;
                }
            }

            // ★ 零权重顶点显式挂到 SMR 的 rootBone（与 Unity 导入器的隐式回退等价，
            //   但可移植：Blender/Maya 没有「bone #0 兜底」，会直接得到坏蒙皮）。
            int fb = 0;
            for (int bi = 0; bi < nb; bi++) if (s.Bones[bi] == s.Fallback) { fb = bi; break; }
            int orphan = 0;
            for (int v = 0; v < s.Mesh.VertexCount; v++)
            {
                if (assigned[v]) continue;
                verts[fb].Add(v); wts[fb].Add(1f);
                assigned[v] = true; orphan++;
            }
            if (orphan > 0) Logger.Warning(string.Format(
                "[FBX] {0}: {1} 个顶点在源数据里没有权重（骨骼未解析/源为零），已挂到 {2}",
                s.Mesh.Name, orphan, s.Bones[fb].Name));

            for (int bi = 0; bi < nb; bi++)
            {
                int cid = clusters[bi].id;
                var bone = s.Bones[bi];

                // Transform / TransformLink（FBX 空间）：
                //   TransformLink = 骨骼节点在 FBX 层级里的世界矩阵（绑定时刻）
                //   Transform     = mesh 节点在 FBX 层级里的世界矩阵
                // FBX 蒙皮 = TransformLink(t) · TransformLink(bind)⁻¹ · Transform
                // ⚠ 旧写法 `link = MeshNode.World · bindpose⁻¹` 用的是 **bindpose 的空间（mesh 的
                //    Z-up 空间）**，与节点层级（Y-up）差一个旋转，且 Transform 写成 Identity
                //    ⇒ 蒙皮把两套空间混在一起，mesh 不跟骨骼动。
                var link = bone.World;
                var t = s.MeshNode.World;
                var vlist = verts[bi];
                var wlist = wts[bi];

                sb.Append("\tDeformer: " + cid + ", \"SubDeformer::Cluster" + bi + "\", \"Cluster\" {\n");
                sb.Append("\t\tVersion: 100\n\t\tUserData: \"\", \"\"\n");
                sb.Append("\t\tIndexes: *" + vlist.Count + " {\n\t\t\ta: " + string.Join(",", vlist) + "\n\t\t} \n");
                sb.Append("\t\tWeights: *" + wlist.Count + " {\n\t\t\ta: " + FA(wlist) + "\n\t\t} \n");
                sb.Append("\t\tTransform: *16 {\n\t\t\ta: " + FA(Mat(t)) + "\n\t\t} \n");
                sb.Append("\t\tTransformLink: *16 {\n\t\t\ta: " + FA(Mat(link)) + "\n\t\t} \n");
                sb.Append("\t}\n");
            }
        }

        static float WeightAt(MeshArrays m, int v, int k)
        {
            ushort raw = m.BoneWeights[v * 4 + k];
            switch (m.WeightFormat)
            {
                case 0: // Float32（4 字节，但这里被当作 ushort 存 — 调用方需保证格式）
                    return raw;
                case 2: // UNorm8
                    return raw / 255f;
                case 4: // UNorm16
                    return raw / 65535f;
                case 5: // SNorm16
                    return Math.Max(-1f, raw / 32767f);
                default:
                    return raw / 65535f;
            }
        }

        static IEnumerable<float> Mat(Mat4 m)
        {
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    yield return m.M[r * 4 + c];
        }

        static string Esc(string s) => (s ?? string.Empty).Replace("\"", "'");
    }
}
