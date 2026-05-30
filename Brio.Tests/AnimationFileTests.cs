using Brio.Core;
using Brio.Files;
using Brio.Game.Posing.Animation;
using System.Numerics;
using Xunit;

namespace Brio.Tests;

public class AnimationFileTests
{
    private const float Tolerance = 0.0005f;

    [Fact]
    public void RoundTrips_ThroughJson_PreservingTracksKeysAndEasing()
    {
        var clip = BuildSampleClip();

        // Save -> JSON (Brio's serializer with vector/quaternion/enum converters) -> load.
        var json = JsonSerializer.Serialize(AnimationFile.FromClip(clip));
        var restored = JsonSerializer.Deserialize<AnimationFile>(json).ToClip();

        Assert.Equal(clip.Name, restored.Name);
        Assert.Equal(clip.Duration, restored.Duration, Tolerance);
        Assert.Equal(clip.BoneTracks.Count, restored.BoneTracks.Count);
        Assert.NotNull(restored.ModelTrack);

        var original = clip.BoneTracks["j_asi_a_l"];
        var loaded = restored.BoneTracks["j_asi_a_l"];
        Assert.Equal(original.Keyframes.Count, loaded.Keyframes.Count);

        for(var i = 0; i < original.Keyframes.Count; i++)
        {
            var a = original.Keyframes[i];
            var b = loaded.Keyframes[i];

            Assert.Equal(a.Time, b.Time, Tolerance);
            Assert.Equal(a.Value.Position, b.Value.Position);
            Assert.Equal(a.Value.Rotation, b.Value.Rotation);
            Assert.Equal(a.Value.Scale, b.Value.Scale);
            Assert.Equal(a.EaseToNext.Type, b.EaseToNext.Type);
            Assert.Equal(a.EaseToNext.P1, b.EaseToNext.P1);
            Assert.Equal(a.EaseToNext.P2, b.EaseToNext.P2);
        }
    }

    [Fact]
    public void RoundTrips_SamplesIdenticallyAfterReload()
    {
        var clip = BuildSampleClip();

        var json = JsonSerializer.Serialize(AnimationFile.FromClip(clip));
        var restored = JsonSerializer.Deserialize<AnimationFile>(json).ToClip();

        // Same evaluated pose at a mid-segment time means the curve survived the round-trip.
        var before = PoseInterpolation.Sample(clip.BoneTracks["j_asi_a_l"], 0.7f);
        var after = PoseInterpolation.Sample(restored.BoneTracks["j_asi_a_l"], 0.7f);

        Assert.Equal(before.Position, after.Position);
        Assert.Equal(before.Scale, after.Scale);
        Assert.Equal(before.Rotation.X, after.Rotation.X, Tolerance);
        Assert.Equal(before.Rotation.W, after.Rotation.W, Tolerance);
    }

    private static AnimationClip BuildSampleClip()
    {
        var clip = new AnimationClip { Name = "Test Clip", Duration = 3f };

        var leg = clip.GetOrCreateTrack("j_asi_a_l");
        leg.AddOrReplace(0f, MakeTransform(new Vector3(0, 0, 0), 0f), Easing.FromType(EasingType.EaseInOut));
        leg.AddOrReplace(1.5f, MakeTransform(new Vector3(0.2f, 0.1f, 0f), 0.6f), new Easing
        {
            Type = EasingType.Bezier,
            P1 = new Vector2(0.3f, 0.1f),
            P2 = new Vector2(0.7f, 0.9f),
        });

        var arm = clip.GetOrCreateTrack("j_ude_a_r");
        arm.AddOrReplace(0.5f, MakeTransform(new Vector3(-0.1f, 0, 0.05f), 1.2f), Easing.Linear);

        clip.GetOrCreateModelTrack().AddOrReplace(0f, MakeTransform(new Vector3(1, 0, 1), 0f));

        return clip;
    }

    private static Transform MakeTransform(Vector3 position, float yaw) => new()
    {
        Position = position,
        Rotation = Quaternion.CreateFromYawPitchRoll(yaw, 0f, 0f),
        Scale = Vector3.One,
    };
}
