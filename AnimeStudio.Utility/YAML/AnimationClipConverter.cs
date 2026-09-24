using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using SevenZip;
using ACLLibs;

namespace AnimeStudio
{

    public class AnimationClipConverter
    {
        /// <summary>
        /// 占位名 float 曲线（`typetree_` / `script_` / `missed_`）默认**丢弃**。
        /// 理由：源数据里这些 binding 的 `attribute` 是**序号**而非属性名（实测 0..146 连续），
        /// 属于游戏自己的自定义 float 轨道；照原样写出去就是每个 clip 上百个
        /// `classID: 95`(Animator) + 空 path + `attribute: typetree_N` 的**空节点**，
        /// 让 clip 的节点集合和模型骨骼对不上。
        /// 需要保留（例如想自己解析这些轨道）时设 `ANIMESTUDIO_KEEP_PLACEHOLDER_FLOATS=1`。
        /// </summary>
        public static bool KeepPlaceholderFloats =>
            Environment.GetEnvironmentVariable("ANIMESTUDIO_KEEP_PLACEHOLDER_FLOATS") == "1";

        private int m_droppedPlaceholderFloats;

        /// <summary>把被丢弃的占位轨道登记到 clip 的 binding 表，导出时一并剔除。</summary>
        private void RegisterDroppedBinding(GenericBinding binding)
        {
            var bc = animationClip.m_ClipBindingConstant;
            if (bc == null) return;
            if (bc.droppedBindings == null) bc.droppedBindings = new HashSet<(ClassIDType, uint)>();
            bc.droppedBindings.Add((binding.typeID, binding.attribute));
        }

        public static readonly Regex UnknownPathRegex = new Regex($@"^{UnknownPathPrefix}[0-9]{{1,10}}$", RegexOptions.Compiled);

        private const string UnknownPathPrefix = "path_";
        private const string MissedPropertyPrefix = "missed_";
        private const string ScriptPropertyPrefix = "script_";
        private const string TypeTreePropertyPrefix = "typetree_";

        private readonly Game game;
        private readonly AnimationClip animationClip;
        private readonly CustomCurveResolver m_customCurveResolver;

        private readonly Dictionary<Vector3Curve, List<Keyframe<Vector3>>> m_translations = new Dictionary<Vector3Curve, List<Keyframe<Vector3>>>();
        private readonly Dictionary<QuaternionCurve, List<Keyframe<Quaternion>>> m_rotations = new Dictionary<QuaternionCurve, List<Keyframe<Quaternion>>>();
        private readonly Dictionary<Vector3Curve, List<Keyframe<Vector3>>> m_scales = new Dictionary<Vector3Curve, List<Keyframe<Vector3>>>();
        private readonly Dictionary<Vector3Curve, List<Keyframe<Vector3>>> m_eulers = new Dictionary<Vector3Curve, List<Keyframe<Vector3>>>();
        private readonly Dictionary<FloatCurve, List<Keyframe<Float>>> m_floats = new Dictionary<FloatCurve, List<Keyframe<Float>>>();
        private readonly Dictionary<PPtrCurve, List<PPtrKeyframe>> m_pptrs = new Dictionary<PPtrCurve, List<PPtrKeyframe>>();

        public List<Vector3Curve> Translations { get; private set; }
        public List<QuaternionCurve> Rotations { get; private set; }
        public List<Vector3Curve> Scales { get; private set; }
        public List<Vector3Curve> Eulers { get; private set; }
        public List<FloatCurve> Floats { get; private set; }
        public List<PPtrCurve> PPtrs { get; private set; }

        public AnimationClipConverter(AnimationClip clip)
        {
            game = clip.assetsFile.game;
            animationClip = clip;
            m_customCurveResolver = new CustomCurveResolver(animationClip);
        }

        public static AnimationClipConverter Process(AnimationClip clip)
        {
            var converter = new AnimationClipConverter(clip);
            converter.ProcessInner();
            return converter;
        }
        private void ProcessInner()
        {
            var m_Clip = animationClip.m_MuscleClip.m_Clip;
            var bindings = animationClip.m_ClipBindingConstant;
            var tos = animationClip.FindTOS();

            var streamedFrames = m_Clip.m_StreamedClip.ReadData();
            var lastDenseFrame = m_Clip.m_DenseClip.m_FrameCount / m_Clip.m_DenseClip.m_SampleRate;
            var lastSampleFrame = streamedFrames.Count > 1 ? streamedFrames[streamedFrames.Count - 2].time : 0.0f;
            var lastFrame = Math.Max(lastDenseFrame, lastSampleFrame);

            if (m_Clip.m_ACLClip.IsSet && !game.Type.IsSRGroup())
            {
                var lastACLFrame = ProcessACLClip(m_Clip, bindings, tos);
                lastFrame = Math.Max(lastFrame, lastACLFrame);
                animationClip.m_Compressed = false;
            }
            ProcessStreams(streamedFrames, bindings, tos, m_Clip.m_DenseClip.m_SampleRate);
            ProcessDenses(m_Clip, bindings, tos);
            if (m_Clip.m_ACLClip.IsSet && game.Type.IsSRGroup())
            {
                var lastACLFrame = ProcessACLClip(m_Clip, bindings, tos);
                lastFrame = Math.Max(lastFrame, lastACLFrame);
                animationClip.m_Compressed = false;
            }
            if (m_Clip.m_ConstantClip != null)
            {
                ProcessConstant(m_Clip, bindings, tos, lastFrame);
            }
            // endfield：数据不在 m_Clip 里，而在 animationClip.m_AclCompressedBuffer
            var aclFrame = ProcessEndfieldACLBuffer(tos);
            if (aclFrame > lastFrame)
            {
                lastFrame = aclFrame;
            }
            CreateCurves();
        }

        /// <summary>
        /// endfield 专用：从 <c>m_AclCompressedBuffer</c> 解出 ACL 轨并铺成曲线。
        ///
        /// 映射（已用 prefab 的骨骼静止坐标逐条对账验证：60/60，56 个精确到小数第 4 位，
        /// 另外 4 个是 IK 目标——它们的位置本来就被动画驱动）：
        /// <list type="bullet">
        /// <item>变换轨 i → 位置 = 第 i 个 <c>attribute==1</c> 的绑定；旋转 = 第 i 个 <c>attribute==2</c> 的绑定；
        ///       缩放 = 同 path 的 <c>attribute==3</c> 绑定（只有 8 个 path 有）</item>
        /// <item>float 轨 j → 绑定表尾部 <c>FloatCurveCount</c> 个绑定里的第 j 个</item>
        /// </list>
        /// 实测：349 个位置绑定、349 个旋转绑定、8 个缩放绑定、152 个 float 绑定，
        /// 与 <c>OutputTrackCount=349</c> / <c>FloatCurveCount=152</c> 完全一致。
        ///
        /// 优化：**恒定的通道只写 1 个关键帧**（Unity 视单关键帧为常量），
        /// 否则每条轨都铺 73 帧会让产物膨胀十几倍（实测大量轨本来就是静止偏移）。
        /// </summary>
        private float ProcessEndfieldACLBuffer(Dictionary<uint, string> tos)
        {
            var acb = animationClip.m_AclCompressedBuffer;
            if (acb == null || acb.TransformBufferData == null || acb.TransformBufferData.Length < 16)
            {
                return 0f;
            }
            if (!EndfieldACL.TryDecode(acb.TransformBufferData, out var tv, out var tt) || tt.Length == 0)
            {
                return 0f;
            }

            var bindings = animationClip.m_ClipBindingConstant;
            if (bindings?.genericBindings == null || bindings.genericBindings.Count == 0)
            {
                return 0f;
            }

            int nt = acb.OutputTrackCount;
            int nf = acb.FloatCurveCount;
            if (nt <= 0)
            {
                return 0f;
            }
            int samples = tv.Length / (nt * 10);
            var last = tt[tt.Length - 1];
            animationClip.m_Compressed = false;      // 曲线已展开，输出不再是“压缩态”
            var zero = new float[4];

            var posList = bindings.genericBindings
                .Where(x => x.attribute == 1 && x.typeID == ClassIDType.Transform).ToList();
            var rotList = bindings.genericBindings
                .Where(x => x.attribute == 2 && x.typeID == ClassIDType.Transform).ToList();
            var scaleByPath = bindings.genericBindings
                .Where(x => x.attribute == 3 && x.typeID == ClassIDType.Transform)
                .GroupBy(x => x.path).ToDictionary(g => g.Key, g => g.First());

            for (int i = 0; i < nt; i++)
            {
                int o0 = i * 10;                                   // 首采样的偏移
                bool rotConst = true, posConst = true, sclConst = true;
                for (int s = 1; s < samples; s++)
                {
                    int o = s * nt * 10 + i * 10;
                    for (int k = 0; k < 4; k++) if (Math.Abs(tv[o + k] - tv[o0 + k]) > 1e-5) rotConst = false;
                    for (int k = 4; k < 7; k++) if (Math.Abs(tv[o + k] - tv[o0 + k]) > 1e-5) posConst = false;
                    for (int k = 7; k < 10; k++) if (Math.Abs(tv[o + k] - tv[o0 + k]) > 1e-5) sclConst = false;
                }

                if (i < rotList.Count)
                {
                    var path = GetCurvePath(tos, rotList[i].path);
                    AddTransformCurve(0f, 2, tv, zero, zero, o0, path);
                    if (!rotConst)
                    {
                        for (int s = 1; s < samples; s++)
                        {
                            AddTransformCurve(tt[s], 2, tv, zero, zero, s * nt * 10 + i * 10, path);
                        }
                    }
                }
                if (i < posList.Count)
                {
                    var path = GetCurvePath(tos, posList[i].path);
                    AddTransformCurve(0f, 1, tv, zero, zero, o0 + 4, path);
                    if (!posConst)
                    {
                        for (int s = 1; s < samples; s++)
                        {
                            AddTransformCurve(tt[s], 1, tv, zero, zero, s * nt * 10 + i * 10 + 4, path);
                        }
                    }
                    if (scaleByPath.TryGetValue(posList[i].path, out var sb))
                    {
                        var spath = GetCurvePath(tos, sb.path);
                        AddTransformCurve(0f, 3, tv, zero, zero, o0 + 7, spath);
                        if (!sclConst)
                        {
                            for (int s = 1; s < samples; s++)
                            {
                                AddTransformCurve(tt[s], 3, tv, zero, zero, s * nt * 10 + i * 10 + 7, spath);
                            }
                        }
                    }
                }
            }

            if (nf > 0 && EndfieldACL.TryDecode(acb.FloatBufferData, out var fv, out _))
            {                int fSamples = fv.Length / nf;
                int floatStart = bindings.genericBindings.Count - nf;
                if (System.Environment.GetEnvironmentVariable("ANIMESTUDIO_ACL_DEBUG") == "1")
                {
                    Console.WriteLine(string.Format(
                        "[ACLDBG] {0} nt={1} nf={2} tvLen={3} tSamples={4} fvLen={5} fSamples={6} bindings={7} floatStart={8}",
                        animationClip.m_Name, nt, nf, tv.Length, tv.Length / Math.Max(1, nt * 10),
                        fv.Length, fSamples, bindings.genericBindings.Count, floatStart));
                    for (int jj = 0; jj < nf; jj++)
                    {
                        int fj = floatStart + jj;
                        if (fj < 0 || fj >= bindings.genericBindings.Count) break;
                        var bb2 = bindings.genericBindings[fj];
                        float mn2 = float.MaxValue, mx2 = float.MinValue;
                        for (int s2 = 0; s2 < fSamples; s2++)
                        {
                            float v2 = fv[s2 * nf + jj];
                            if (v2 < mn2) mn2 = v2;
                            if (v2 > mx2) mx2 = v2;
                        }
                        string nm2 = "-";
                        try
                        {
                            if ((BindingCustomType)bb2.customType == BindingCustomType.AnimatorMuscle)
                                nm2 = bb2.GetHumanoidMuscle().ToAttributeString();
                        }
                        catch { nm2 = "<ERR>"; }
                        Console.WriteLine(string.Format(
                            "[ACLDBG] j={0,3} attr={1,4} classID={2,4} custom={3} name={4,-32} v0={5,9:F4} min={6,9:F4} max={7,9:F4}",
                            jj, bb2.attribute, (int)bb2.typeID, bb2.customType, nm2, fv[jj], mn2, mx2));
                    }
                }
                for (int j = 0; j < nf; j++)
                {
                    int fi = floatStart + j;
                    if (fi < 0 || fi >= bindings.genericBindings.Count)
                    {
                        break;
                    }
                    var b = bindings.genericBindings[fi];
                    var path = GetCurvePath(tos, b.path);
                    bool cst = true;
                    for (int s = 1; s < fSamples; s++)
                    {
                        if (Math.Abs(fv[s * nf + j] - fv[j]) > 1e-5) { cst = false; break; }
                    }
                    // ★ 必须按 customType 分派：这些轨道的 customType 是
                    //   `AnimatorMuscle(8)`（人形肌肉），要走 AddCustomCurve→AddAnimatorMuscleCurve
                    //   才能写出真实肌肉属性名；直接 AddDefaultCurve 会退化成
                    //   `typetree_<序号>` + 空 path + classID 95 ⇒ Unity 侧全是 Missing。
                    AddFloatTrack(bindings, b, path, 0f, fv[j]);
                    if (!cst)
                    {
                        for (int s = 1; s < fSamples; s++)
                        {
                            AddFloatTrack(bindings, b, path, tt[s], fv[s * nf + j]);
                        }
                    }
                }
            }

            return last;
        }

        private void CreateCurves()
        {
            m_translations.AsEnumerable().ToList().ForEach(x => x.Key.curve.m_Curve.AddRange(x.Value));
            Translations = m_translations.Keys.ToList();
            m_rotations.AsEnumerable().ToList().ForEach(x => x.Key.curve.m_Curve.AddRange(x.Value));
            Rotations = m_rotations.Keys.ToList();
            m_scales.AsEnumerable().ToList().ForEach(x => x.Key.curve.m_Curve.AddRange(x.Value));
            Scales = m_scales.Keys.ToList();
            m_eulers.AsEnumerable().ToList().ForEach(x => x.Key.curve.m_Curve.AddRange(x.Value));
            Eulers = m_eulers.Keys.ToList();
            m_floats.AsEnumerable().ToList().ForEach(x => x.Key.curve.m_Curve.AddRange(x.Value));
            Floats = m_floats.Keys.ToList();
            m_pptrs.AsEnumerable().ToList().ForEach(x => x.Key.curve.AddRange(x.Value));
            PPtrs = m_pptrs.Keys.ToList();

            if (m_droppedPlaceholderFloats > 0)
            {
                Console.WriteLine("[Info] " + (animationClip.m_Name ?? "?") + ": 丢弃占位 float 曲线 "
                    + m_droppedPlaceholderFloats + " 条（源数据 attribute 是序号，Unity 侧无对应属性；"
                    + "设 ANIMESTUDIO_KEEP_PLACEHOLDER_FLOATS=1 可保留）");
            }
        }

        private void ProcessStreams(List<StreamedClip.StreamedFrame> streamFrames, AnimationClipBindingConstant bindings, Dictionary<uint, string> tos, float sampleRate)
        {
            var curveValues = new float[4];
            var inSlopeValues = new float[4];
            var outSlopeValues = new float[4];
            var interval = 1.0f / sampleRate;

            // first (index [0]) stream frame is for slope calculation for the first real frame (index [1])
            // last one (index [count - 1]) is +Infinity
            // it is made for slope processing, but we don't need them
            for (var frameIndex = 1; frameIndex < streamFrames.Count - 1; frameIndex++)
            {
                var frame = streamFrames[frameIndex];
                for (var curveIndex = 0; curveIndex < frame.keyList.Count;)
                {
                    var curve = frame.keyList[curveIndex];
                    var index = curve.index;
                    if (!game.Type.IsSRGroup())
                        index += (int)animationClip.m_MuscleClip.m_Clip.m_ACLClip.CurveCount;
                    var binding = bindings.FindBinding(index);

                    var path = GetCurvePath(tos, binding.path);
                    if (binding.typeID == ClassIDType.Transform)
                    {
                        GetPreviousFrame(streamFrames, curve.index, frameIndex, out var prevFrameIndex, out var prevCurveIndex);
                        var dimension = binding.GetDimension();
                        for (int key = 0; key < dimension; key++)
                        {
                            var keyCurve = frame.keyList[curveIndex];
                            var prevFrame = streamFrames[prevFrameIndex];
                            var prevKeyCurve = prevFrame.keyList[prevCurveIndex + key];
                            var deltaTime = frame.time - prevFrame.time;
                            curveValues[key] = keyCurve.value;
                            inSlopeValues[key] = prevKeyCurve.CalculateNextInSlope(deltaTime, keyCurve);
                            outSlopeValues[key] = keyCurve.outSlope;
                            curveIndex = GetNextCurve(frame, curveIndex);
                        }

                        AddTransformCurve(frame.time, binding.attribute, curveValues, inSlopeValues, outSlopeValues, 0, path);
                    }
                    else if ((BindingCustomType)binding.customType == BindingCustomType.None)
                    {
                        AddDefaultCurve(binding, path, frame.time, frame.keyList[curveIndex].value);
                        curveIndex = GetNextCurve(frame, curveIndex);
                    }
                    else
                    {
                        AddCustomCurve(bindings, binding, path, frame.time, frame.keyList[curveIndex].value);
                        curveIndex = GetNextCurve(frame, curveIndex);
                    }
                }
            }
        }

        private void ProcessDenses(Clip clip, AnimationClipBindingConstant bindings, Dictionary<uint, string> tos)
        {
            var dense = clip.m_DenseClip;
            var streamCount = clip.m_StreamedClip.curveCount;
            var slopeValues = new float[4]; // no slopes - 0 values
            for (var frameIndex = 0; frameIndex < dense.m_FrameCount; frameIndex++)
            {
                var time = frameIndex / dense.m_SampleRate;
                var frameOffset = frameIndex * (int)dense.m_CurveCount;
                for (var curveIndex = 0; curveIndex < dense.m_CurveCount;)
                {
                    var index = (int)streamCount + curveIndex;
                    if (!game.Type.IsSRGroup())
                        index += (int)clip.m_ACLClip.CurveCount;
                    var binding = bindings.FindBinding(index);
                    var path = GetCurvePath(tos, binding.path);
                    var framePosition = frameOffset + curveIndex;
                    if (binding.typeID == ClassIDType.Transform)
                    {
                        AddTransformCurve(time, binding.attribute, dense.m_SampleArray, slopeValues, slopeValues, framePosition, path);
                        curveIndex += binding.GetDimension();
                    }
                    else if ((BindingCustomType)binding.customType == BindingCustomType.None)
                    {
                        AddDefaultCurve(binding, path, time, dense.m_SampleArray[framePosition]);
                        curveIndex++;
                    }
                    else
                    {
                        AddCustomCurve(bindings, binding, path, time, dense.m_SampleArray[framePosition]);
                        curveIndex++;
                    }
                }
            }
        }
        private float ProcessACLClip(Clip clip, AnimationClipBindingConstant bindings, Dictionary<uint, string> tos)
        {
            var acl = clip.m_ACLClip;
            acl.Process(game, out var values, out var times);
            float[] slopeValues = new float[4]; // no slopes - 0 values

            int frameCount = times.Length;
            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                float time = times[frameIndex];
                int frameOffset = frameIndex * (int)acl.CurveCount;
                for (int curveIndex = 0; curveIndex < acl.CurveCount;)
                {
                    var index = curveIndex;
                    if (game.Type.IsSRGroup())
                        index += (int)(clip.m_DenseClip.m_CurveCount + clip.m_StreamedClip.curveCount);
                    GenericBinding binding = bindings.FindBinding(index);
                    string path = GetCurvePath(tos, binding.path);
                    int framePosition = frameOffset + curveIndex;
                    if (binding.typeID == ClassIDType.Transform)
                    {
                        AddTransformCurve(time, binding.attribute, values, slopeValues, slopeValues, framePosition, path);
                        curveIndex += binding.GetDimension();
                    }
                    else if ((BindingCustomType)binding.customType == BindingCustomType.None)
                    {
                        AddDefaultCurve(binding, path, time, values[framePosition]);
                        curveIndex++;
                    }
                    else
                    {
                        AddCustomCurve(bindings, binding, path, time, values[framePosition]);
                        curveIndex++;
                    }
                }
            }

            return times[frameCount - 1];
        }
        private void ProcessConstant(Clip clip, AnimationClipBindingConstant bindings, Dictionary<uint, string> tos, float lastFrame)
        {
            var constant = clip.m_ConstantClip;
            var streamCount = clip.m_StreamedClip.curveCount;
            var denseCount = clip.m_DenseClip.m_CurveCount;
            var slopeValues = new float[4]; // no slopes - 0 values

            // only first and last frames
            var time = 0.0f;
            for (var i = 0; i < 2; i++, time += lastFrame)
            {
                for (var curveIndex = 0; curveIndex < constant.data.Length;)
                {
                    var index = (int)(streamCount + denseCount + curveIndex);
                    if (clip.m_ACLClip.IsSet)
                        index += (int)clip.m_ACLClip.CurveCount;
                    GenericBinding binding = bindings.FindBinding(index);
                    string path = GetCurvePath(tos, binding.path);
                    if (binding.typeID == ClassIDType.Transform)
                    {
                        AddTransformCurve(time, binding.attribute, constant.data, slopeValues, slopeValues, curveIndex, path);
                        curveIndex += binding.GetDimension();
                    }
                    else if ((BindingCustomType)binding.customType == BindingCustomType.None)
                    {
                        AddDefaultCurve(binding, path, time, constant.data[curveIndex]);
                        curveIndex++;
                    }
                    else
                    {
                        AddCustomCurve(bindings, binding, path, time, constant.data[curveIndex]);
                        curveIndex++;
                    }
                }
            }
        }

        private void AddCustomCurve(AnimationClipBindingConstant bindings, GenericBinding binding, string path, float time, float value)
        {
            switch ((BindingCustomType)binding.customType)
            {
                case BindingCustomType.AnimatorMuscle:
                    AddAnimatorMuscleCurve(binding, time, value);
                    break;
                default:
                    string attribute = m_customCurveResolver.ToAttributeName((BindingCustomType)binding.customType, binding.attribute, path);
                    if (binding.isPPtrCurve == 0x01)
                    {
                        PPtrCurve curve = new PPtrCurve(path, attribute, binding.typeID, binding.script.Cast<MonoScript>());
                        AddPPtrKeyframe(curve, bindings, time, (int)value);
                    }
                    else
                    {
                        FloatCurve curve = new FloatCurve(path, attribute, binding.typeID, binding.script.Cast<MonoScript>());
                        AddFloatKeyframe(curve, time, value);
                    }
                    break;
            }
        }

        private void AddTransformCurve(float time, uint transType, float[] curveValues,
            float[] inSlopeValues, float[] outSlopeValues, int offset, string path)
        {
            switch (transType)
            {
                case 1:
                    {
                        var curve = new Vector3Curve(path);
                        if (!m_translations.TryGetValue(curve, out List<Keyframe<Vector3>> transCurve))
                        {
                            transCurve = new List<Keyframe<Vector3>>();
                            m_translations.Add(curve, transCurve);
                        }

                        float x = curveValues[offset + 0];
                        float y = curveValues[offset + 1];
                        float z = curveValues[offset + 2];

                        float inX = inSlopeValues[0];
                        float inY = inSlopeValues[1];
                        float inZ = inSlopeValues[2];

                        float outX = outSlopeValues[0];
                        float outY = outSlopeValues[1];
                        float outZ = outSlopeValues[2];

                        Vector3 value = new Vector3(x, y, z);
                        Vector3 inSlope = new Vector3(inX, inY, inZ);
                        Vector3 outSlope = new Vector3(outX, outY, outZ);
                        Keyframe<Vector3> transKey = new Keyframe<Vector3>(time, value, inSlope, outSlope, AnimationClipExtensions.DefaultVector3Weight);
                        transCurve.Add(transKey);
                    }
                    break;
                case 2:
                    {
                        var curve = new QuaternionCurve(path);
                        if (!m_rotations.TryGetValue(curve, out List<Keyframe<Quaternion>> rotCurve))
                        {
                            rotCurve = new List<Keyframe<Quaternion>>();
                            m_rotations.Add(curve, rotCurve);
                        }

                        float x = curveValues[offset + 0];
                        float y = curveValues[offset + 1];
                        float z = curveValues[offset + 2];
                        float w = curveValues[offset + 3];

                        float inX = inSlopeValues[0];
                        float inY = inSlopeValues[1];
                        float inZ = inSlopeValues[2];
                        float inW = inSlopeValues[3];

                        float outX = outSlopeValues[0];
                        float outY = outSlopeValues[1];
                        float outZ = outSlopeValues[2];
                        float outW = outSlopeValues[3];

                        Quaternion value = new Quaternion(x, y, z, w);
                        Quaternion inSlope = new Quaternion(inX, inY, inZ, inW);
                        Quaternion outSlope = new Quaternion(outX, outY, outZ, outW);
                        Keyframe<Quaternion> rotKey = new Keyframe<Quaternion>(time, value, inSlope, outSlope, AnimationClipExtensions.DefaultQuaternionWeight);
                        rotCurve.Add(rotKey);
                    }
                    break;
                case 3:
                    {
                        var curve = new Vector3Curve(path);
                        if (!m_scales.TryGetValue(curve, out List<Keyframe<Vector3>> scaleCurve))
                        {
                            scaleCurve = new List<Keyframe<Vector3>>();
                            m_scales.Add(curve, scaleCurve);
                        }

                        float x = curveValues[offset + 0];
                        float y = curveValues[offset + 1];
                        float z = curveValues[offset + 2];

                        float inX = inSlopeValues[0];
                        float inY = inSlopeValues[1];
                        float inZ = inSlopeValues[2];

                        float outX = outSlopeValues[0];
                        float outY = outSlopeValues[1];
                        float outZ = outSlopeValues[2];

                        Vector3 value = new Vector3(x, y, z);
                        Vector3 inSlope = new Vector3(inX, inY, inZ);
                        Vector3 outSlope = new Vector3(outX, outY, outZ);
                        Keyframe<Vector3> scaleKey = new Keyframe<Vector3>(time, value, inSlope, outSlope, AnimationClipExtensions.DefaultVector3Weight);
                        scaleCurve.Add(scaleKey);
                    }
                    break;
                case 4:
                    {
                        var curve = new Vector3Curve(path);
                        if (!m_eulers.TryGetValue(curve, out List<Keyframe<Vector3>> eulerCurve))
                        {
                            eulerCurve = new List<Keyframe<Vector3>>();
                            m_eulers.Add(curve, eulerCurve);
                        }

                        float x = curveValues[offset + 0];
                        float y = curveValues[offset + 1];
                        float z = curveValues[offset + 2];

                        float inX = inSlopeValues[0];
                        float inY = inSlopeValues[1];
                        float inZ = inSlopeValues[2];

                        float outX = outSlopeValues[0];
                        float outY = outSlopeValues[1];
                        float outZ = outSlopeValues[2];

                        Vector3 value = new Vector3(x, y, z);
                        Vector3 inSlope = new Vector3(inX, inY, inZ);
                        Vector3 outSlope = new Vector3(outX, outY, outZ);
                        Keyframe<Vector3> eulerKey = new Keyframe<Vector3>(time, value, inSlope, outSlope, AnimationClipExtensions.DefaultVector3Weight);
                        eulerCurve.Add(eulerKey);
                    }
                    break;
                default:
                    throw new NotImplementedException(transType.ToString());
            }
        }

        /// <summary>
        /// 单值轨道的统一入口：**按 customType 分派**，不要直接调 AddDefaultCurve。
        /// - `None(0)`            → AddDefaultCurve（引擎属性，名字解析不出来时写占位名）
        /// - `Transform(4)`       → AddTransformCurve（本方法不处理，调用方自己判）
        /// - `AnimatorMuscle(8)`  → AddCustomCurve → AddAnimatorMuscleCurve（真实 muscle 名）
        /// - 其它                  → AddCustomCurve
        /// </summary>
        private void AddFloatTrack(AnimationClipBindingConstant bindings, GenericBinding binding, string path, float time, float value)
        {
            if (binding.typeID == ClassIDType.Transform)
            {
                AddTransformCurve(time, binding.attribute, new[] { value }, new[] { 0f }, new[] { 0f }, 0, path);
                return;
            }
            if ((BindingCustomType)binding.customType == BindingCustomType.None)
            {
                AddDefaultCurve(binding, path, time, value);
                return;
            }
            AddCustomCurve(bindings, binding, path, time, value);
        }

        private void AddDefaultCurve(GenericBinding binding, string path, float time, float value)
        {
            switch (binding.typeID)
            {
                case ClassIDType.GameObject:
                    {
                        AddGameObjectCurve(binding, path, time, value);
                    }
                    break;

                case ClassIDType.MonoBehaviour:
                    {
                        AddScriptCurve(binding, path, time, value);
                    }
                    break;

                default:
                    AddEngineCurve(binding, path, time, value);
                    break;
            }
        }

        private void AddGameObjectCurve(GenericBinding binding, string path, float time, float value)
        {
            if (binding.attribute == CRC.CalculateDigestAscii("m_IsActive"))
            {
                FloatCurve curve = new FloatCurve(path, "m_IsActive", ClassIDType.GameObject, new PPtr<MonoScript>(0, 0, null));
                AddFloatKeyframe(curve, time, value);
                return;
            }
            else
            {
                // 组件缺失 ⇒ 属性名解析不出来，占位名同样是垃圾
                if (!KeepPlaceholderFloats)
                {
                    m_droppedPlaceholderFloats++;
                    RegisterDroppedBinding(binding);
                    return;
                }
                FloatCurve curve = new FloatCurve(path, MissedPropertyPrefix + binding.attribute, ClassIDType.GameObject, new PPtr<MonoScript>(0, 0, null));
                AddFloatKeyframe(curve, time, value);
            }
        }

        private void AddScriptCurve(GenericBinding binding, string path, float time, float value)
        {
            // 同上：脚本属性名解析不出来时写 `script_<hash>` 也是占位垃圾（Unity 里显示成不存在的属性）
            if (!KeepPlaceholderFloats)
            {
                m_droppedPlaceholderFloats++;
                RegisterDroppedBinding(binding);
                return;
            }
            FloatCurve curve = new FloatCurve(path, ScriptPropertyPrefix + binding.attribute, ClassIDType.MonoBehaviour, binding.script.Cast<MonoScript>());
            AddFloatKeyframe(curve, time, value);
        }

        private void AddEngineCurve(GenericBinding binding, string path, float time, float value)
        {
            // 引擎组件的 float 轨道：attribute 无法解析成属性名（源里是序号）⇒ 占位名无意义，
            // 默认丢弃。见 KeepPlaceholderFloats 的说明。
            if (!KeepPlaceholderFloats)
            {
                m_droppedPlaceholderFloats++;
                RegisterDroppedBinding(binding);
                return;
            }
            FloatCurve curve = new FloatCurve(path, TypeTreePropertyPrefix + binding.attribute, binding.typeID, new PPtr<MonoScript>(0, 0, null));
            AddFloatKeyframe(curve, time, value);
        }

        private void AddAnimatorMuscleCurve(GenericBinding binding, float time, float value)
        {
            // ★ 属性名必须用 **Unity 真实序列化格式**（= AssetRipper 风格枚举名）：
            //   实测 Unity 自产人形 clip（Starter Assets/FBX 导入）里写的是
            //     LeftFootT.x / LeftFootQ.w / LeftHand.Index.1 Stretched / LeftFootTDOF.x / RootT.x
            //   A/B 实测（batchmode 采样骨骼角度）：
            //     `LeftHand.Index.1 Stretched` → 骨骼转 45° ✅ 生效
            //     `Left Index 1 Stretched`（HumanTrait 显示名）→ 0° ❌ 无效
            //   ⇒ 不要改成 HumanTrait.MuscleName 的显示名，也不要把这些槽位当"不可命名"丢弃。
            FloatCurve curve = new FloatCurve(string.Empty, binding.GetHumanoidMuscle().ToAttributeString(), ClassIDType.Animator, new PPtr<MonoScript>(0, 0, null));
            AddFloatKeyframe(curve, time, value);
        }

        private void AddFloatKeyframe(FloatCurve curve, float time, float value)
        {
            if (!m_floats.TryGetValue(curve, out List<Keyframe<Float>> floatCurve))
            {
                floatCurve = new List<Keyframe<Float>>();
                m_floats.Add(curve, floatCurve);
            }

            Keyframe<Float> floatKey = new Keyframe<Float>(time, value, default, default, AnimationClipExtensions.DefaultFloatWeight);
            floatCurve.Add(floatKey);
        }

        private void AddPPtrKeyframe(PPtrCurve curve, AnimationClipBindingConstant bindings, float time, int index)
        {
            if (!m_pptrs.TryGetValue(curve, out List<PPtrKeyframe> pptrCurve))
            {
                pptrCurve = new List<PPtrKeyframe>();
                m_pptrs.Add(curve, pptrCurve);
                AddPPtrKeyframe(curve, bindings, 0.0f, index - 1);
            }

            PPtr<Object> value = bindings.pptrCurveMapping[index];
            PPtrKeyframe pptrKey = new PPtrKeyframe(time, value);
            pptrCurve.Add(pptrKey);
        }

        private void GetPreviousFrame(List<StreamedClip.StreamedFrame> streamFrames, int curveID, int currentFrame, out int frameIndex, out int curveIndex)
        {
            for (frameIndex = currentFrame - 1; frameIndex >= 0; frameIndex--)
            {
                var frame = streamFrames[frameIndex];
                for (curveIndex = 0; curveIndex < frame.keyList.Count; curveIndex++)
                {
                    var curve = frame.keyList[curveIndex];
                    if (curve.index == curveID)
                    {
                        return;
                    }
                }
            }
            throw new Exception($"There is no curve with index {curveID} in any of previous frames");
        }

        private int GetNextCurve(StreamedClip.StreamedFrame frame, int currentCurve)
        {
            var curve = frame.keyList[currentCurve];
            int i = currentCurve + 1;
            for (; i < frame.keyList.Count; i++)
            {
                if (frame.keyList[i].index != curve.index)
                {
                    return i;
                }
            }
            return i;
        }

        private static string GetCurvePath(Dictionary<uint, string> tos, uint hash)
        {
            if (tos.TryGetValue(hash, out string path))
            {
                return path;
            }
            else
            {
                return UnknownPathPrefix + hash;
            }
        }
        
    }
}