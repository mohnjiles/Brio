using Brio.Core;
using Brio.Game.Posing.Animation;
using System.Numerics;
using Xunit;

namespace Brio.Tests;

public class PoseInterpolationTests
{
    private const float Tolerance = 0.0005f;

    [Theory]
    [InlineData(EasingType.Linear, 0f, 0f)]
    [InlineData(EasingType.Linear, 0.5f, 0.5f)]
    [InlineData(EasingType.Linear, 1f, 1f)]
    [InlineData(EasingType.EaseIn, 0.5f, 0.25f)]     // t*t
    [InlineData(EasingType.EaseOut, 0.5f, 0.75f)]    // t*(2-t)
    [InlineData(EasingType.Stepped, 0.99f, 0f)]      // holds start until next key
    public void Evaluate_KnownCurves(EasingType type, float t, float expected)
    {
        var result = PoseInterpolation.Evaluate(Easing.FromType(type), t);
        Assert.Equal(expected, result, Tolerance);
    }

    [Theory]
    [InlineData(EasingType.Linear)]
    [InlineData(EasingType.EaseIn)]
    [InlineData(EasingType.EaseOut)]
    [InlineData(EasingType.EaseInOut)]
    [InlineData(EasingType.Bezier)]
    public void Evaluate_PinsEndpoints(EasingType type)
    {
        var easing = Easing.FromType(type);
        Assert.Equal(0f, PoseInterpolation.Evaluate(easing, 0f), Tolerance);
        Assert.Equal(1f, PoseInterpolation.Evaluate(easing, 1f), Tolerance);
    }

    [Fact]
    public void Evaluate_ClampsOutOfRangeInput()
    {
        var easing = Easing.Linear;
        Assert.Equal(0f, PoseInterpolation.Evaluate(easing, -2f), Tolerance);
        Assert.Equal(1f, PoseInterpolation.Evaluate(easing, 5f), Tolerance);
    }

    [Fact]
    public void CubicBezier_LinearHandlesApproximateIdentity()
    {
        // Handles on the diagonal (0.25,0.25)+(0.75,0.75) should behave like y = x.
        var p1 = new Vector2(0.25f, 0.25f);
        var p2 = new Vector2(0.75f, 0.75f);

        for(var x = 0f; x <= 1f; x += 0.1f)
            Assert.Equal(x, PoseInterpolation.CubicBezier(p1, p2, x), 0.01f);
    }

    [Fact]
    public void CubicBezier_IsMonotonicForEaseInOut()
    {
        var p1 = new Vector2(0.42f, 0f);
        var p2 = new Vector2(0.58f, 1f);

        var prev = PoseInterpolation.CubicBezier(p1, p2, 0f);
        for(var x = 0.05f; x <= 1f; x += 0.05f)
        {
            var y = PoseInterpolation.CubicBezier(p1, p2, x);
            Assert.True(y >= prev - Tolerance, $"curve decreased at x={x}: {y} < {prev}");
            prev = y;
        }
    }

    [Fact]
    public void Lerp_MidpointBlendsComponents()
    {
        var a = new Transform { Position = Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.One };
        var b = new Transform { Position = new Vector3(2, 4, 6), Rotation = Quaternion.Identity, Scale = new Vector3(3, 3, 3) };

        var mid = PoseInterpolation.Lerp(a, b, 0.5f);

        Assert.Equal(new Vector3(1, 2, 3), mid.Position);
        Assert.Equal(new Vector3(2, 2, 2), mid.Scale);
    }

    [Fact]
    public void Lerp_SlerpStaysNormalized()
    {
        var a = new Transform { Rotation = Quaternion.CreateFromYawPitchRoll(0f, 0f, 0f) };
        var b = new Transform { Rotation = Quaternion.CreateFromYawPitchRoll(1.5f, 0.4f, -0.7f) };

        var mid = PoseInterpolation.Lerp(a, b, 0.5f);

        Assert.Equal(1f, mid.Rotation.Length(), 0.001f);
    }

    [Fact]
    public void Sample_HoldsBeforeFirstAndAfterLast()
    {
        var track = new BoneTrack("test");
        track.AddOrReplace(1f, MakePos(10));
        track.AddOrReplace(3f, MakePos(30));

        Assert.Equal(10f, PoseInterpolation.Sample(track, 0f).Position.X, Tolerance);   // before first
        Assert.Equal(30f, PoseInterpolation.Sample(track, 5f).Position.X, Tolerance);   // after last
    }

    [Fact]
    public void Sample_InterpolatesBetweenKeyframesLinearly()
    {
        var track = new BoneTrack("test");
        track.AddOrReplace(0f, MakePos(0), Easing.Linear);
        track.AddOrReplace(2f, MakePos(100), Easing.Linear);

        Assert.Equal(50f, PoseInterpolation.Sample(track, 1f).Position.X, 0.01f);
        Assert.Equal(25f, PoseInterpolation.Sample(track, 0.5f).Position.X, 0.01f);
    }

    [Fact]
    public void AddOrReplace_ReplacesKeyframeAtSameTime()
    {
        var track = new BoneTrack("test");
        track.AddOrReplace(1f, MakePos(10));
        track.AddOrReplace(1f, MakePos(20));

        Assert.Single(track.Keyframes);
        Assert.Equal(20f, track.Keyframes[0].Value.Position.X, Tolerance);
    }

    [Fact]
    public void SampleClip_OmitsEmptyTracks()
    {
        var clip = new AnimationClip();
        clip.GetOrCreateTrack("empty");                       // no keyframes
        clip.GetOrCreateTrack("keyed").AddOrReplace(0f, MakePos(5));

        var pose = PoseInterpolation.SampleClip(clip, 0f);

        Assert.True(pose.Bones.ContainsKey("keyed"));
        Assert.False(pose.Bones.ContainsKey("empty"));
    }

    private static Transform MakePos(float x) => new()
    {
        Position = new Vector3(x, 0, 0),
        Rotation = Quaternion.Identity,
        Scale = Vector3.One,
    };
}
