using Brio.Game.Posing.Animation;
using System.Collections.Generic;
using System.Numerics;

namespace Brio.Files;

/// <summary>
/// On-disk representation of an <see cref="AnimationClip"/>. Uses property-based types
/// (<see cref="PoseFile.Bone"/> for transforms, <see cref="Vector2"/> for bezier handles) so it
/// serializes cleanly through Brio's System.Text.Json setup — the runtime <c>Transform</c> struct
/// stores its data in public fields, which that serializer ignores.
/// </summary>
public class AnimationFile
{
    public string TypeName { get; set; } = "Brio Animation";
    public string Name { get; set; } = "Animation";
    public float Duration { get; set; } = 5f;

    public List<BoneTrackData> Tracks { get; set; } = [];
    public BoneTrackData? ModelTrack { get; set; }

    public class BoneTrackData
    {
        public string BoneName { get; set; } = string.Empty;
        public List<KeyframeData> Keyframes { get; set; } = [];
    }

    public class KeyframeData
    {
        public float Time { get; set; }
        public PoseFile.Bone Value { get; set; } = new();
        public EasingData Ease { get; set; } = new();
    }

    public class EasingData
    {
        public EasingType Type { get; set; } = EasingType.Linear;
        public Vector2 P1 { get; set; } = new(0.25f, 0.25f);
        public Vector2 P2 { get; set; } = new(0.75f, 0.75f);
    }

    public static AnimationFile FromClip(AnimationClip clip)
    {
        var file = new AnimationFile
        {
            Name = clip.Name,
            Duration = clip.Duration,
        };

        foreach(var track in clip.BoneTracks.Values)
            file.Tracks.Add(ToData(track));

        if(clip.ModelTrack is not null)
            file.ModelTrack = ToData(clip.ModelTrack);

        return file;
    }

    public AnimationClip ToClip()
    {
        var clip = new AnimationClip
        {
            Name = Name,
            Duration = Duration,
        };

        foreach(var trackData in Tracks)
        {
            var track = clip.GetOrCreateTrack(trackData.BoneName);
            foreach(var kf in trackData.Keyframes)
                track.Keyframes.Add(ToKeyframe(kf));
            track.Sort();
        }

        if(ModelTrack is not null)
        {
            var modelTrack = clip.GetOrCreateModelTrack();
            foreach(var kf in ModelTrack.Keyframes)
                modelTrack.Keyframes.Add(ToKeyframe(kf));
            modelTrack.Sort();
        }

        return clip;
    }

    private static BoneTrackData ToData(BoneTrack track)
    {
        var data = new BoneTrackData { BoneName = track.BoneName };

        foreach(var kf in track.Keyframes)
        {
            data.Keyframes.Add(new KeyframeData
            {
                Time = kf.Time,
                Value = kf.Value, // Transform -> PoseFile.Bone (implicit)
                Ease = new EasingData { Type = kf.EaseToNext.Type, P1 = kf.EaseToNext.P1, P2 = kf.EaseToNext.P2 },
            });
        }

        return data;
    }

    private static Keyframe ToKeyframe(KeyframeData data)
    {
        return new Keyframe(data.Time, data.Value) // PoseFile.Bone -> Transform (implicit)
        {
            EaseToNext = new Easing { Type = data.Ease.Type, P1 = data.Ease.P1, P2 = data.Ease.P2 },
        };
    }
}
