using Brio.Core;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Brio.Game.Posing.Animation;

/// <summary>
/// How a keyframe interpolates toward the next keyframe on its track.
/// </summary>
public enum EasingType
{
    Linear,
    EaseIn,
    EaseOut,
    EaseInOut,
    Stepped,
    Bezier,
}

/// <summary>
/// Easing applied over the segment between a keyframe and the next one on the same track.
/// <see cref="P1"/> / <see cref="P2"/> are CSS-style cubic-bezier control handles in the unit
/// square, used only when <see cref="Type"/> is <see cref="EasingType.Bezier"/>.
/// </summary>
public struct Easing
{
    public EasingType Type { get; set; }
    public Vector2 P1 { get; set; }
    public Vector2 P2 { get; set; }

    public static Easing Linear => new() { Type = EasingType.Linear, P1 = new Vector2(0.25f, 0.25f), P2 = new Vector2(0.75f, 0.75f) };
    public static Easing Default => Linear;

    public static Easing FromType(EasingType type) => new()
    {
        Type = type,
        // Sensible default handles so switching to Bezier in the editor starts from this curve's shape.
        P1 = new Vector2(0.25f, 0.25f),
        P2 = new Vector2(0.75f, 0.75f),
    };
}

/// <summary>
/// A single keyed value at a point in time. <see cref="Value"/> is an absolute model-space
/// transform captured from a bone's <c>LastRawTransform</c> (or the model transform).
/// </summary>
public class Keyframe
{
    public float Time { get; set; }
    public Transform Value { get; set; }
    public Easing EaseToNext { get; set; } = Easing.Default;

    public Keyframe() { }

    public Keyframe(float time, Transform value)
    {
        Time = time;
        Value = value;
    }

    public Keyframe(float time, Transform value, Easing easeToNext)
    {
        Time = time;
        Value = value;
        EaseToNext = easeToNext;
    }

    public Keyframe Clone() => new(Time, Value, EaseToNext);
}

/// <summary>
/// An ordered list of keyframes for one bone (keyed by bone name). Kept sorted by time.
/// </summary>
public class BoneTrack
{
    public string BoneName { get; set; } = string.Empty;
    public List<Keyframe> Keyframes { get; set; } = [];

    public BoneTrack() { }

    public BoneTrack(string boneName)
    {
        BoneName = boneName;
    }

    public bool HasKeyframes => Keyframes.Count > 0;

    public float FirstTime => Keyframes.Count > 0 ? Keyframes[0].Time : 0f;
    public float LastTime => Keyframes.Count > 0 ? Keyframes[^1].Time : 0f;

    /// <summary>
    /// Adds a keyframe at <paramref name="time"/>, or replaces the existing one if a keyframe
    /// already sits at (approximately) that time. Keeps the list sorted.
    /// </summary>
    public Keyframe AddOrReplace(float time, Transform value, Easing? easeToNext = null)
    {
        var existing = Keyframes.FirstOrDefault(k => System.MathF.Abs(k.Time - time) < KeyframeTimeEpsilon);
        if(existing != null)
        {
            existing.Value = value;
            if(easeToNext.HasValue)
                existing.EaseToNext = easeToNext.Value;
            return existing;
        }

        var keyframe = new Keyframe(time, value, easeToNext ?? Easing.Default);
        Keyframes.Add(keyframe);
        Sort();
        return keyframe;
    }

    public bool Remove(Keyframe keyframe) => Keyframes.Remove(keyframe);

    public void Sort() => Keyframes.Sort((a, b) => a.Time.CompareTo(b.Time));

    public const float KeyframeTimeEpsilon = 0.0005f;
}

/// <summary>
/// A keyframe animation for a single actor. Each bone that is animated gets its own
/// <see cref="BoneTrack"/>; bones with no track simply hold the (frozen) base pose during playback.
/// </summary>
public class AnimationClip
{
    public string Name { get; set; } = "Animation";

    /// <summary>Total clip length in seconds. Playback loops/clamps against this.</summary>
    public float Duration { get; set; } = 5f;

    /// <summary>Per-bone tracks, keyed by game bone name (matches <c>PoseFile.Bones</c> keys).</summary>
    public Dictionary<string, BoneTrack> BoneTracks { get; set; } = [];

    /// <summary>
    /// Optional track for the actor's root model transform (whole-body translate/rotate/scale).
    /// Sampled separately from bone tracks. Null when the body root is not animated.
    /// </summary>
    public BoneTrack? ModelTrack { get; set; }

    public bool HasAnyKeyframes =>
        BoneTracks.Values.Any(t => t.HasKeyframes) || (ModelTrack?.HasKeyframes ?? false);

    /// <summary>The latest keyframe time across all tracks (useful for sizing the timeline).</summary>
    public float MaxKeyframeTime
    {
        get
        {
            var max = 0f;
            foreach(var track in BoneTracks.Values)
                if(track.HasKeyframes)
                    max = System.MathF.Max(max, track.LastTime);
            if(ModelTrack?.HasKeyframes == true)
                max = System.MathF.Max(max, ModelTrack.LastTime);
            return max;
        }
    }

    public BoneTrack GetOrCreateTrack(string boneName)
    {
        if(!BoneTracks.TryGetValue(boneName, out var track))
        {
            track = new BoneTrack(boneName);
            BoneTracks[boneName] = track;
        }
        return track;
    }

    public BoneTrack GetOrCreateModelTrack()
    {
        ModelTrack ??= new BoneTrack("__model__");
        return ModelTrack;
    }

    /// <summary>Removes a bone track entirely. Returns true if a track was removed.</summary>
    public bool RemoveTrack(string boneName) => BoneTracks.Remove(boneName);
}
