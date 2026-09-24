using AnimeStudio;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AnimeStudio.CLI
{
    /// <summary>
    /// Serialises a parsed <see cref="AnimatorController"/> to a readable JSON document.
    ///
    /// Why JSON and not a Unity <c>.controller</c>: the shipped bundle stores only the
    /// <b>baked runtime</b> form (<c>ControllerConstant</c> + a name table).  Unity's
    /// editor form (<c>m_AnimatorParameters</c> / <c>m_AnimatorLayers</c>) is EditorOnly and
    /// is stripped from builds, so a file Unity can import would have to be <i>reconstructed</i>
    /// from this data.  This export therefore transcribes what the bundle actually contains —
    /// no inferred fields — and resolves every hash through <c>AnimatorController.m_TOS</c>.
    /// </summary>
    internal static class AnimatorControllerExporter
    {
        /// <summary>
        /// <paramref name="referenceOf"/> maps an AnimationClip to the Unity reference node that
        /// points at the exported <c>.anim</c> (and pulls that clip into the export).
        /// </summary>
        public static string ToJson(AnimatorController controller,
                                    Func<AnimationClip, IDictionary<string, object>> referenceOf)
        {
            var unresolved = new SortedSet<uint>();
            Func<uint, string> name = hash => Resolve(controller, hash, unresolved);

            string Name(uint h) => name(h);

            var clips = new List<object>();
            var clipObjects = new List<AnimationClip>();
            for (var i = 0; i < controller.m_AnimationClips.Count; i++)
            {
                var pptr = controller.m_AnimationClips[i];
                var entry = new Dictionary<string, object> { ["index"] = i };
                if (pptr != null && pptr.TryGet(out var clip))
                {
                    clipObjects.Add(clip);
                    entry["name"] = clip.m_Name;
                    entry["pathId"] = clip.m_PathID;
                    entry["reference"] = referenceOf(clip);
                }
                else
                {
                    clipObjects.Add(null);
                    entry["name"] = null;
                    entry["reference"] = null;
                }
                clips.Add(entry);
            }

            var ctl = controller.m_Controller;
            var valueIds = new List<uint>();
            if (ctl?.m_Values?.m_ValueArray != null)
            {
                foreach (var v in ctl.m_Values.m_ValueArray)
                {
                    valueIds.Add(v.m_ID);
                }
            }

            var layers = new List<object>();
            if (ctl?.m_LayerArray != null)
            {
                for (var li = 0; li < ctl.m_LayerArray.Count; li++)
                {
                    var layer = ctl.m_LayerArray[li];
                    var sm = (ctl.m_StateMachineArray != null && layer.m_StateMachineIndex < ctl.m_StateMachineArray.Count)
                        ? ctl.m_StateMachineArray[(int)layer.m_StateMachineIndex]
                        : null;

                    layers.Add(new Dictionary<string, object>
                    {
                        ["index"] = li,
                        ["name"] = Name(layer.m_Binding),
                        ["bindingHash"] = layer.m_Binding,
                        ["stateMachineIndex"] = layer.m_StateMachineIndex,
                        ["stateMachineSynchronizedLayerIndex"] = layer.m_StateMachineMotionSetIndex,
                        ["layerBlendingMode"] = layer.m_LayerBlendingMode,
                        ["defaultWeight"] = layer.m_DefaultWeight,
                        ["ikPass"] = layer.m_IKPass,
                        ["syncedLayerAffectsTiming"] = layer.m_SyncedLayerAffectsTiming,
                        ["stateMachine"] = sm == null ? null : StateMachineToModel(sm, Name, clipObjects, unresolved),
                    });
                }
            }

            var root = new Dictionary<string, object>
            {
                ["name"] = controller.m_Name,
                ["pathId"] = controller.m_PathID,
                ["animationClips"] = clips,
                ["layers"] = layers,
                ["parameters"] = ParameterTable(ctl, Name, unresolved),
                ["defaultValues"] = DefaultValues(ctl),
                ["nameTable"] = controller.m_TOSData,
                ["nameTableSize"] = controller.m_TOSData?.Count ?? 0,
                ["animationCurveMaskCount"] = controller.m_AnimationCurveMasks?.Length ?? 0,
                ["unresolvedHashes"] = unresolved.ToArray(),
                ["notes"] = new[]
                {
                    "Transcription of the baked runtime controller; no fields are inferred.",
                    "All name/parameter strings come from AnimatorController.m_TOSData and are matched " +
                    "by CRC-32 (Unity's Animator.StringToHash) against the stored hashes.",
                    "Unity's editor form (m_AnimatorParameters / m_AnimatorLayers) is not present in the " +
                    "bundle, so a directly importable .controller must be reconstructed from this data.",
                },
            };

            return JsonConvert.SerializeObject(root, Formatting.Indented);
        }

        private static string Resolve(AnimatorController controller, uint hash,
                                      ISet<uint> unresolved)
        {
            if (hash == 0 || hash == uint.MaxValue)
            {
                return null;
            }
            if (controller.m_TOS != null && controller.m_TOS.TryGetValue(hash, out var value))
            {
                return value;
            }
            unresolved.Add(hash);
            return null;
        }

        private static object ParameterTable(ControllerConstant ctl, Func<uint, string> name, ISet<uint> unresolved)
        {
            var list = new List<object>();
            if (ctl?.m_Values?.m_ValueArray == null)
            {
                return list;
            }
            foreach (var v in ctl.m_Values.m_ValueArray)
            {
                list.Add(new Dictionary<string, object>
                {
                    ["name"] = name(v.m_ID),
                    ["hash"] = v.m_ID,
                    ["type"] = v.m_Type,
                    ["defaultValueIndex"] = v.m_Index,
                });
            }
            return list;
        }

        private static object DefaultValues(ControllerConstant ctl)
        {
            var dv = ctl?.m_DefaultValues;
            if (dv == null)
            {
                return null;
            }
            return new Dictionary<string, object>
            {
                ["boolValues"] = dv.m_BoolValues,
                ["intValues"] = dv.m_IntValues,
                ["floatValues"] = dv.m_FloatValues,
                ["positionValueCount"] = dv.m_PositionValues?.Length ?? 0,
                ["quaternionValueCount"] = dv.m_QuaternionValues?.Length ?? 0,
                ["scaleValueCount"] = dv.m_ScaleValues?.Length ?? 0,
            };
        }

        private static object StateMachineToModel(StateMachineConstant sm, Func<uint, string> name,
                                                  List<AnimationClip> clips, ISet<uint> unresolved)
        {
            var states = new List<object>();
            for (var si = 0; si < sm.m_StateConstantArray.Count; si++)
            {
                states.Add(StateToModel(sm.m_StateConstantArray[si], si, sm, name, clips, unresolved));
            }

            var anyState = new List<object>();
            foreach (var t in sm.m_AnyStateTransitionConstantArray ?? new List<TransitionConstant>())
            {
                anyState.Add(TransitionToModel(t, sm, name, unresolved, anyState: true));
            }

            return new Dictionary<string, object>
            {
                ["defaultStateIndex"] = sm.m_DefaultState,
                ["defaultState"] = sm.m_DefaultState < states.Count ? NameOf(sm.m_StateConstantArray[(int)sm.m_DefaultState], name) : null,
                ["motionSetCount"] = sm.m_MotionSetCount,
                ["states"] = states,
                ["anyStateTransitions"] = anyState,
                ["selectorStateCount"] = sm.m_SelectorStateConstantArray?.Count ?? 0,
            };
        }

        private static string NameOf(StateConstant s, Func<uint, string> name)
        {
            return name(s.m_NameID) ?? name(s.m_FullPathID) ?? name(s.m_PathID);
        }

        private static object StateToModel(StateConstant s, int index, StateMachineConstant sm,
                                           Func<uint, string> name, List<AnimationClip> clips,
                                           ISet<uint> unresolved)
        {
            var transitions = new List<object>();
            foreach (var t in s.m_TransitionConstantArray ?? new List<TransitionConstant>())
            {
                transitions.Add(TransitionToModel(t, sm, name, unresolved, anyState: false));
            }

            var blendTrees = new List<object>();
            foreach (var bt in s.m_BlendTreeConstantArray ?? new List<BlendTreeConstant>())
            {
                var nodes = new List<object>();
                foreach (var n in bt.m_NodeArray ?? new List<BlendTreeNodeConstant>())
                {
                    object clip = null;
                    if (n.m_ClipID < clips.Count)
                    {
                        var c = clips[(int)n.m_ClipID];
                        clip = c == null ? null : new Dictionary<string, object>
                        {
                            ["index"] = n.m_ClipID,
                            ["name"] = c.m_Name,
                            ["pathId"] = c.m_PathID,
                        };
                    }
                    nodes.Add(new Dictionary<string, object>
                    {
                        ["blendType"] = n.m_BlendType,
                        ["blendEvent"] = name(n.m_BlendEventID),
                        ["blendEventY"] = name(n.m_BlendEventYID),
                        ["childIndices"] = n.m_ChildIndices,
                        ["clipId"] = n.m_ClipID,
                        ["clip"] = clip,
                        ["duration"] = n.m_Duration,
                        ["cycleOffset"] = n.m_CycleOffset,
                        ["stateName"] = name(n.m_StateNameHash),
                        ["mirror"] = n.m_Mirror,
                        ["childThresholds"] = n.m_Blend1dData?.m_ChildThresholdArray,
                        ["childPositionCount"] = n.m_Blend2dData?.m_ChildPositionArray?.Length ?? 0,
                        ["childBlendEventIds"] = n.m_BlendDirectData?.m_ChildBlendEventIDArray,
                    });
                }
                blendTrees.Add(new Dictionary<string, object> { ["nodes"] = nodes });
            }

            return new Dictionary<string, object>
            {
                ["index"] = index,
                ["name"] = name(s.m_NameID),
                ["path"] = name(s.m_PathID),
                ["fullPath"] = name(s.m_FullPathID),
                ["tagHash"] = s.m_TagID,
                ["speed"] = s.m_Speed,
                ["cycleOffset"] = s.m_CycleOffset,
                ["loop"] = s.m_Loop,
                ["mirror"] = s.m_Mirror,
                ["ikOnFeet"] = s.m_IKOnFeet,
                ["writeDefaultValues"] = s.m_WriteDefaultValues,
                ["speedParameter"] = name(s.m_SpeedParamID),
                ["mirrorParameter"] = name(s.m_MirrorParamID),
                ["cycleOffsetParameter"] = name(s.m_CycleOffsetParamID),
                ["transitions"] = transitions,
                ["blendTreeConstantIndexArray"] = s.m_BlendTreeConstantIndexArray,
                ["blendTrees"] = blendTrees,
            };
        }

        private static object TransitionToModel(TransitionConstant t, StateMachineConstant sm,
                                                Func<uint, string> name, ISet<uint> unresolved, bool anyState)
        {
            var conditions = new List<object>();
            foreach (var c in t.m_ConditionConstantArray ?? new List<ConditionConstant>())
            {
                conditions.Add(new Dictionary<string, object>
                {
                    ["mode"] = c.m_ConditionMode,
                    ["parameter"] = name(c.m_EventID),
                    ["parameterHash"] = c.m_EventID,
                    ["threshold"] = c.m_EventThreshold,
                    ["exitTime"] = c.m_ExitTime,
                });
            }

            string destination = null;
            if (!anyState && sm != null && t.m_DestinationState < sm.m_StateConstantArray.Count)
            {
                destination = NameOf(sm.m_StateConstantArray[(int)t.m_DestinationState], name);
            }

            return new Dictionary<string, object>
            {
                ["name"] = name(t.m_FullPathID),
                ["fullPathHash"] = t.m_FullPathID,
                ["destinationStateIndex"] = t.m_DestinationState,
                ["destinationState"] = destination,
                ["conditions"] = conditions,
                ["duration"] = t.m_TransitionDuration,
                ["offset"] = t.m_TransitionOffset,
                ["exitTime"] = t.m_ExitTime,
                ["hasExitTime"] = t.m_HasExitTime,
                ["hasFixedDuration"] = t.m_HasFixedDuration,
                ["interruptionSource"] = t.m_InterruptionSource,
                ["orderedInterruption"] = t.m_OrderedInterruption,
                ["canTransitionToSelf"] = t.m_CanTransitionToSelf,
            };
        }
    }
}
