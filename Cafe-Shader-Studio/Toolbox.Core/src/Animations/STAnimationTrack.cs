using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Toolbox.Core.Animations
{
    /// <summary>
    /// Represents a animation track used to store key frames
    /// </summary>
    public class STAnimationTrack
    {
        /// <summary>
        /// The name of the track displayed in the timeline
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// The interpolation type used to interpoalte key frames.
        /// </summary>
        public STInterpoaltionType InterpolationType { get; set; }

        public STLoopMode WrapMode { get; set; } = STLoopMode.Clamp;

        /// <summary>
        /// A list of key frames used for the track.
        /// </summary>
        public List<STKeyFrame> KeyFrames = new List<STKeyFrame>();

        /// <summary>
        /// Determines if the track has key data used.
        /// </summary>
        public bool HasKeys => KeyFrames.Count > 0;

        public virtual int ChannelIndex { get; set; }

        public STAnimationTrack() { }

        public STAnimationTrack(STInterpoaltionType interpolation)
        {
            InterpolationType = interpolation;
        }

        public void RemoveKey(float frame)
        {
            var keyed = KeyFrames.FindIndex(x => x.Frame == frame);
            if (keyed != -1)
                KeyFrames.RemoveAt(keyed);
        }

        public void Insert(STKeyFrame keyFrame)
        {
            var keyed = KeyFrames.FindIndex(x => x.Frame == keyFrame.Frame);
            if (keyed != -1)
                KeyFrames.RemoveAt(keyed);

            KeyFrames.Add(keyFrame);
            KeyFrames = KeyFrames.OrderBy(x => x.Frame).ToList();
        }

        /// <summary>
        /// Determines if a keyframe is keyed at the given frame value
        /// </summary>
        /// <param name="frame"></param>
        /// <returns></returns>
        public bool IsKeyed(float frame)
        {
            var matches = KeyFrames.Where(p => p.Frame == frame);
            return matches != null && matches.Count() > 0;
        }

        /// <summary>
        /// Gets the left and right key frame given the frame value.
        /// </summary>
        /// <param name="frame"></param>
        /// <returns></returns>
        public STKeyFrame[] GetFrame(float frame)
        {
            if (KeyFrames.Count == 0) return null;
            STKeyFrame k1 = (STKeyFrame)KeyFrames[0], k2 = (STKeyFrame)KeyFrames[0];
            foreach (STKeyFrame k in KeyFrames)
            {
                if (k.Frame < frame)
                {
                    k1 = k;
                }
                else
                {
                    k2 = k;
                    break;
                }
            }

            return new STKeyFrame[] { k1, k2 };
        }

        /// <summary>
        /// Finds the keys either side of a frame: the last one at or before it and the
        /// first one at or after it.
        /// </summary>
        private void GetSurroundingKeys(float frame, ref STKeyFrame left, ref STKeyFrame right)
        {
            if (KeyFrames.Count == 0)
                return;

            int lo = 0, hi = KeyFrames.Count;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (KeyFrames[mid].Frame < frame)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            int first = lo;
            while (lo < KeyFrames.Count && KeyFrames[lo].Frame <= frame)
                lo++;

            var last = KeyFrames[KeyFrames.Count - 1];

            left = lo > 0 ? KeyFrames[lo - 1] : KeyFrames[0];
            //Of a key written twice at the last frame, the segment ends at the first.
            right = first < KeyFrames.Count ? KeyFrames[first] : last;
        }

        private float GetWrapFrame(float frame)
        {
            var lastFrame = KeyFrames.Last().Frame;
            if (WrapMode == STLoopMode.Clamp)
            {
                if (frame > lastFrame)
                    return lastFrame;
                else
                    return frame;
            }
            else if (WrapMode == STLoopMode.Repeat)
            {
                while (frame > lastFrame)
                    frame -= lastFrame;
                return frame;
            }
            return frame;
        }

        public virtual STKeyFrame[] GetFrames(float frame, float startFrame = 0)
        {
            STKeyFrame LK = KeyFrames.First();
            STKeyFrame RK = KeyFrames.Last();

            float Frame = GetWrapFrame(frame - startFrame);
            GetSurroundingKeys(Frame, ref LK, ref RK);

            return new STKeyFrame[2] { LK, RK };
        }

        //Key frame setup based on
        //https://github.com/gdkchan/SPICA/blob/42c4181e198b0fd34f0a567345ee7e75b54cb58b/SPICA/Formats/CtrH3D/Animation/H3DFloatKeyFrameGroup.cs

        /// <summary>
        /// Gets the current value at the given frame based on the interpolation the track uses and the keyframes.
        /// </summary>
        /// <param name="frame"></param>
        /// <param name="startFrame"></param>
        /// <returns></returns>
        public virtual float GetFrameValue(float frame, float startFrame = 0)
        {
            if (KeyFrames.Count == 0) return 0;
            if (KeyFrames.Count == 1) return KeyFrames[0].Value;
            STKeyFrame LK = KeyFrames.First();
            STKeyFrame RK = KeyFrames.Last();

            float Frame = GetWrapFrame(frame - startFrame);
            GetSurroundingKeys(Frame, ref LK, ref RK);

            if (LK.Frame != RK.Frame)
            {
                float FrameDiff = Frame - LK.Frame;
                float Weight = FrameDiff / (RK.Frame - LK.Frame);

                switch (InterpolationType)
                {
                    case STInterpoaltionType.Constant: return LK.Value;
                    case STInterpoaltionType.Step: return LK.Value;
                    case STInterpoaltionType.Linear: return InterpolationHelper.Lerp(LK.Value, RK.Value, Weight);
                    case STInterpoaltionType.Bezier:
                        {
                            float length = RK.Frame - LK.Frame;
                            if (LK is STBezierKeyFrame)
                            {
                                var p0 = LK.Value;
                                var p1 = ((STBezierKeyFrame)LK).SlopeOut;
                                var p2 = ((STBezierKeyFrame)RK).SlopeIn;
                                var p3 = RK.Value;
                                return InterpolationHelper.BezierInterpolate(Frame,
                                    LK.Frame, RK.Frame,
                                    p1, p2,
                                    p0, p3);
                            }

                            //Default to no control point usage for this key
                            return InterpolationHelper.BezierInterpolate(Frame,
                                LK.Frame, RK.Frame, 0, 0, LK.Value, RK.Value);
                        }
                    case STInterpoaltionType.Hermite:

                        if (LK is STHermiteCubicKeyFrame)
                        {
                            if (!(RK is STHermiteCubicKeyFrame))
                            {
                                RK = new STHermiteCubicKeyFrame() { Value = RK.Value };
                            }

                            STHermiteCubicKeyFrame hermiteKeyLK = (STHermiteCubicKeyFrame)LK;
                            STHermiteCubicKeyFrame hermiteKeyRK = (STHermiteCubicKeyFrame)RK;

                            return InterpolationHelper.CubicHermiteInterpolate(Frame,
                                 hermiteKeyLK.Frame,
                                 hermiteKeyRK.Frame,
                                 hermiteKeyLK.Value,
                                 hermiteKeyLK.Coef1,
                                 hermiteKeyLK.Coef2,
                                 hermiteKeyLK.Coef3);
                        }

                        if (LK is STHermiteKeyFrame)
                        {
                            if (!(RK is STHermiteKeyFrame))
                            {
                                RK = new STHermiteKeyFrame() { Value = RK.Value };
                            }

                            STHermiteKeyFrame hermiteKeyLK = (STHermiteKeyFrame)LK;
                            STHermiteKeyFrame hermiteKeyRK = (STHermiteKeyFrame)RK;

                            float length = RK.Frame - LK.Frame;

                            return InterpolationHelper.HermiteInterpolate(Frame,
                             hermiteKeyLK.Frame,
                             hermiteKeyRK.Frame,
                             hermiteKeyLK.Value,
                             hermiteKeyRK.Value,
                             hermiteKeyLK.TangentOut * length,
                             hermiteKeyRK.TangentIn * length);
                        }

                        return InterpolationHelper.Herp(
                            LK.Value, RK.Value,
                            LK.Slope, RK.Slope,
                            FrameDiff,
                            Weight);
                }
            }

            return LK.Value;
        }
    }
}
