using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Toolbox.Core.Animations;
using BfresLibrary;

namespace BfresEditor
{
    public class BfresAnimations
    {
        public static AnimCurve CreateLinearCurve(float[] frames, float[] values, int offset)
        {
            var curve = new AnimCurve();
            curve.AnimDataOffset = (uint)offset;
            curve.CurveType = AnimCurveType.Linear;
            curve.Frames = frames;
            curve.Keys = new float[values.Length, 2];
            curve.Offset = 0;
            curve.Scale = 1;
            curve.StartFrame = 0;
            curve.EndFrame = frames.LastOrDefault();
            curve.FrameType = AnimCurveFrameType.Single;
            curve.PostWrap = WrapMode.Repeat;
            curve.PreWrap = WrapMode.Repeat;
            for (int i = 0; i < values.Length; i++)
                curve.Keys[i, 0] = values[i];
            return curve;
        }

        public static void GenerateKeys(BfresAnimationTrack track, AnimCurve curve,
            bool valuesAsInts = false)
        {
            //Use the curve's post wrap.
            track.WrapMode = STLoopMode.Clamp;
            if (curve.PostWrap == WrapMode.Repeat)
                track.WrapMode = STLoopMode.Repeat;
            if (curve.PostWrap == WrapMode.Mirror)
                track.WrapMode = STLoopMode.Mirror;

            float valueScale = curve.Scale > 0 ? curve.Scale : 1;

            float[] tangentIn = null;
            float[] tangentOut = null;
            if (curve.CurveType == AnimCurveType.Cubic)
                GetSlopes(curve, out tangentIn, out tangentOut);

            track.KeyFrames.Capacity = track.KeyFrames.Count + curve.Frames.Length;

            for (int i = 0; i < curve.Frames.Length; i++)
            {
                var frame = curve.Frames[i];
                switch (curve.CurveType)
                {
                    case AnimCurveType.Cubic:
                        {
                            track.InterpolationType = STInterpoaltionType.Hermite;
                            //Important to not offset the other 3 values, just the first one!
                            var value = curve.Keys[i, 0] * valueScale + curve.Offset;
                            var coef1 = curve.Keys[i, 1] * valueScale;
                            var coef2 = curve.Keys[i, 2] * valueScale;
                            var coef3 = curve.Keys[i, 3] * valueScale;

                            track.KeyFrames.Add(new STHermiteKeyFrame()
                            {
                                Frame = frame,
                                Value = value,
                                TangentIn = tangentIn[i],
                                TangentOut = tangentOut[i],
                            });

                            /*    track.KeyFrames.Add(new STHermiteCubicKeyFrame()
                                {
                                    Frame = frame,
                                    Value = value,
                                    Coef1 = coef1,
                                    Coef2 = coef2, 
                                    Coef3 = coef3,
                                    TangentIn = slopes[0],
                                    TangentOut = slopes[1],
                                });*/
                        }
                        break;
                    case AnimCurveType.Linear:
                        {
                            track.InterpolationType = STInterpoaltionType.Linear;
                            var value = curve.Keys[i, 0] * valueScale + curve.Offset;
                            var delta = curve.Keys[i, 1];
                            track.KeyFrames.Add(new STKeyFrame()
                            {
                                Frame = frame,
                                Value = value,
                            });
                        }
                        break;
                    case AnimCurveType.StepBool:
                        {
                            track.InterpolationType = STInterpoaltionType.Step;
                            track.KeyFrames.Add(new STKeyFrame()
                            {
                                Frame = frame,
                                Value = curve.KeyStepBoolData[i] ? 1 : 0,
                            });
                        }
                        break;
                    default:
                        {
                            track.InterpolationType = STInterpoaltionType.Step;
                            var value = curve.Keys[i, 0] + curve.Offset;
                            if (valuesAsInts)
                                value = (int)curve.Keys[i, 0] + curve.Offset;

                            track.KeyFrames.Add(new STKeyFrame()
                            {
                                Frame = frame,
                                Value = value,
                            });
                        }
                        break;
                }
            }
        }

        //Extracts the in and out slope of every key of a cubic curve. A key takes its out
        //slope from its own coefficients and its in slope from the key before it, so the
        //first key has no in slope.
        public static void GetSlopes(AnimCurve curve, out float[] tangentIn, out float[] tangentOut)
        {
            int count = curve.Frames.Length;
            tangentIn = new float[count];
            tangentOut = new float[count];

            if (curve.CurveType != AnimCurveType.Cubic)
                return;

            float inSlope = 0;
            for (int i = 0; i < count; i++)
            {
                var coef0 = curve.Keys[i, 0] * curve.Scale + curve.Offset;
                var coef1 = curve.Keys[i, 1] * curve.Scale;
                var coef3 = curve.Keys[i, 3] * curve.Scale;

                float time = 0;
                float delta = 0;
                if (i < count - 1)
                {
                    var nextValue = curve.Keys[i + 1, 0] * curve.Scale + curve.Offset;
                    delta = nextValue - coef0;
                    time = curve.Frames[i + 1] - curve.Frames[i];
                }

                if (i < count - 1 && time == 0)
                {
                    tangentIn[i] = inSlope;
                    continue;
                }

                float outSlope = coef1 / time;

                tangentIn[i] = inSlope;
                tangentOut[i] = coef1 == 0 ? 0 : outSlope;

                inSlope = (coef3 - (-2 * delta)) / time - outSlope;
            }
        }
    }
}
