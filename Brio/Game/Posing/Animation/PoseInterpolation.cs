using Brio.Core;
using Brio.Files;
using System;
using System.Numerics;

namespace Brio.Game.Posing.Animation;

/// <summary>
/// Pure interpolation + easing math for keyframe playback. No game dependencies, so this is
/// fully unit-testable (see the BrioTester project).
/// </summary>
public static class PoseInterpolation
{
    /// <summary>
    /// Interpolates between two absolute transforms. Position/scale use linear interpolation;
    /// rotation uses spherical linear interpolation (shortest path — <see cref="Quaternion.Slerp"/>
    /// negates one operand internally when the dot product is negative).
    /// </summary>
    public static Transform Lerp(Transform a, Transform b, float t)
    {
        return new Transform
        {
            Position = Vector3.Lerp(a.Position, b.Position, t),
            Rotation = Quaternion.Slerp(a.Rotation, b.Rotation, t),
            Scale = Vector3.Lerp(a.Scale, b.Scale, t),
        };
    }

    /// <summary>
    /// Maps a normalized segment parameter (0..1) through an easing curve, returning the eased
    /// 0..1 blend factor. <see cref="EasingType.Stepped"/> holds the start value until the next
    /// keyframe is reached.
    /// </summary>
    public static float Evaluate(Easing easing, float t)
    {
        t = Math.Clamp(t, 0f, 1f);

        return easing.Type switch
        {
            EasingType.Linear => t,
            EasingType.Stepped => 0f,
            EasingType.EaseIn => t * t,
            EasingType.EaseOut => t * (2f - t),
            EasingType.EaseInOut => t < 0.5f ? 2f * t * t : -1f + (4f - 2f * t) * t,
            EasingType.Bezier => CubicBezier(easing.P1, easing.P2, t),
            _ => t,
        };
    }

    /// <summary>
    /// Samples a bone track at the given time. Holds the first/last keyframe value before/after
    /// the track's range. Assumes <see cref="BoneTrack.Keyframes"/> is sorted by time.
    /// </summary>
    public static Transform Sample(BoneTrack track, float time)
    {
        var kfs = track.Keyframes;
        if(kfs.Count == 0)
            return Transform.Identity;
        if(kfs.Count == 1 || time <= kfs[0].Time)
            return kfs[0].Value;
        if(time >= kfs[^1].Time)
            return kfs[^1].Value;

        for(int i = 0; i < kfs.Count - 1; i++)
        {
            var a = kfs[i];
            var b = kfs[i + 1];
            if(time >= a.Time && time <= b.Time)
            {
                var span = b.Time - a.Time;
                var u = span <= 0f ? 0f : (time - a.Time) / span;
                var eased = Evaluate(a.EaseToNext, u);
                return Lerp(a.Value, b.Value, eased);
            }
        }

        return kfs[^1].Value;
    }

    /// <summary>
    /// Builds a <see cref="PoseFile"/> for the clip at the given time, containing one entry per
    /// animated bone. Bones without a track are omitted so they keep their (frozen) base pose.
    /// </summary>
    public static PoseFile SampleClip(AnimationClip clip, float time)
    {
        var pose = new PoseFile();

        foreach(var (boneName, track) in clip.BoneTracks)
        {
            if(!track.HasKeyframes)
                continue;

            // Implicit Transform -> PoseFile.Bone conversion.
            pose.Bones[boneName] = Sample(track, time);
        }

        return pose;
    }

    /// <summary>
    /// CSS-style cubic-bezier easing. The curve runs from (0,0) to (1,1) with control points
    /// <paramref name="p1"/> and <paramref name="p2"/>. Given an x (the normalized time),
    /// solves for the curve parameter s such that bezierX(s) == x, then returns bezierY(s).
    /// </summary>
    public static float CubicBezier(Vector2 p1, Vector2 p2, float x)
    {
        x = Math.Clamp(x, 0f, 1f);

        // Solve bezierX(s) = x for s via bisection. The x-component is monotonic for the
        // sane control handles the editor allows, so bisection is robust and allocation-free.
        var lo = 0f;
        var hi = 1f;
        var s = x;

        for(int i = 0; i < 24; i++)
        {
            s = (lo + hi) * 0.5f;
            var xAtS = BezierComponent(p1.X, p2.X, s);
            if(xAtS < x)
                lo = s;
            else
                hi = s;
        }

        return Math.Clamp(BezierComponent(p1.Y, p2.Y, s), 0f, 1f);
    }

    // Cubic Bezier on a single axis with endpoints 0 and 1: B(s) = 3(1-s)^2 s c1 + 3(1-s) s^2 c2 + s^3
    private static float BezierComponent(float c1, float c2, float s)
    {
        var oneMinus = 1f - s;
        return 3f * oneMinus * oneMinus * s * c1
             + 3f * oneMinus * s * s * c2
             + s * s * s;
    }
}
