using Brio.Capabilities.Actor;
using Brio.Core;
using Brio.Entities.Actor;
using Brio.Files;
using Brio.Game.Posing;
using Brio.Game.Posing.Animation;
using Brio.Game.Posing.Skeletons;
using Brio.Resources;
using Brio.UI.Widgets.Posing;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;

namespace Brio.Capabilities.Posing;

/// <summary>
/// Drives a keyframe <see cref="AnimationClip"/> on an actor: freezes the base game animation,
/// then on every framework tick samples each bone's track and writes the result straight into
/// the actor's <see cref="SkeletonPosingCapability.PoseInfo"/>. Brio's existing skeleton update
/// (<c>SkeletonService.BeginSkeletonUpdate</c>) applies that PoseInfo to the Havok skeleton, so
/// we never touch bones directly.
///
/// Why we write PoseInfo directly instead of re-importing a PoseFile each frame: the importer
/// keys deltas against <c>bone.LastRawTransform</c>, which the skeleton update overwrites with
/// the *posed* result after every frame. Re-importing each frame would therefore compute deltas
/// against an already-posed reference and apply them onto the freshly-derived base pose, causing
/// drift. Instead we capture the true (un-overridden) base pose once and compute every frame's
/// delta against that fixed reference.
/// </summary>
public class AnimationCapability : ActorCharacterCapability
{
    private readonly IFramework _framework;

    public AnimationClip Clip { get; set; } = new();

    public bool IsPlaying { get; private set; }
    public bool IsPaused { get; private set; }
    public bool IsEditing { get; private set; }
    public bool Loop { get; set; } = true;
    public float PlaybackSpeed { get; set; } = 1f;
    public float Playhead { get; set; }

    public float Duration
    {
        get => Clip.Duration;
        set => Clip.Duration = MathF.Max(0.01f, value);
    }

    // Clean (un-overridden) frozen base pose, captured once when the actor is engaged for
    // animation. Playback deltas are computed against this fixed reference so a frozen actor
    // reproduces each keyframe exactly.
    private readonly Dictionary<string, Transform> _baseTransforms = [];
    private bool _engaged;
    private bool _baseReady;
    private float _originalSpeed = 1f;
    private bool _modelOverridden;

    private SkeletonPosingCapability SkeletonPosing => Entity.GetCapability<SkeletonPosingCapability>();
    private ModelPosingCapability ModelPosing => Entity.GetCapability<ModelPosingCapability>();

    public AnimationCapability(ActorEntity parent, IFramework framework) : base(parent)
    {
        _framework = framework;
        Widget = new AnimationWidget(this);

        _framework.Update += OnFrameworkUpdate;
    }

    #region Transport

    public void TogglePlay()
    {
        if(IsPlaying)
            Pause();
        else
            Play();
    }

    public void Play()
    {
        if(!Clip.HasAnyKeyframes)
            return;

        Engage();
        // Playback owns the pose: drop any manual authoring stacks so only the sampled animation shows.
        SkeletonPosing.ResetPose();
        IsPaused = false;
        IsPlaying = true;
    }

    /// <summary>
    /// Pauses playback and bakes the evaluated pose at the current playhead onto the rig as editable
    /// stacks, so you can grab a bone, tweak it, and key the next frame from where you left off.
    /// </summary>
    public void Pause() => EditAtPlayhead();

    /// <summary>
    /// Loads the evaluated pose at the current playhead onto the rig as editable stacks without
    /// advancing, so you can continue posing from whatever the animation looks like at this time.
    /// </summary>
    public void EditAtPlayhead()
    {
        IsPlaying = false;
        IsPaused = true;

        if(_baseReady)
            ApplyEvaluatedPose();
    }

    /// <summary>
    /// Stops playback and returns to authoring. While the editor is open the actor stays frozen at
    /// its base pose so you can keep posing and keying; otherwise the freeze is fully released.
    /// </summary>
    public void Stop()
    {
        IsPlaying = false;
        IsPaused = false;
        Playhead = 0f;

        if(IsEditing)
            SkeletonPosing.ResetPose();
        else
            Disengage();
    }

    /// <summary>
    /// Moves the playhead. While playing or paused this seeks the previewed frame; while authoring
    /// it only moves the time cursor (where the next keyframe lands) and never touches the pose, so
    /// you can pose freely at any time and key it.
    /// </summary>
    public void ScrubTo(float time)
    {
        Playhead = Math.Clamp(time, 0f, Duration);

        // While paused for editing, seeking reloads the evaluated pose at the new time so you can
        // pick the frame to tweak. (While authoring from scratch this stays a pure cursor move.)
        if(IsPaused && _baseReady)
            ApplyEvaluatedPose();
    }

    #endregion

    #region Keying

    /// <summary>Keys a single bone at the current playhead, capturing its current posed transform.</summary>
    public void KeyBone(string boneName) => KeyBoneAt(boneName, Playhead);

    /// <summary>Keys a single bone at an explicit time, growing the clip if needed.</summary>
    public void KeyBoneAt(string boneName, float time)
    {
        var bone = SkeletonPosing.GetBone(boneName, PoseInfoSlot.Character);
        if(bone is null)
            return;

        time = MathF.Max(0f, time);
        Clip.GetOrCreateTrack(boneName).AddOrReplace(time, bone.LastRawTransform);

        if(time > Duration)
            Duration = time;
    }

    /// <summary>Keys every character bone at the current playhead (full-body snapshot).</summary>
    public void KeyAll()
    {
        var skeleton = SkeletonPosing.CharacterSkeleton;
        if(skeleton is null)
            return;

        foreach(var bone in skeleton.Bones)
        {
            if(bone.IsPartialRoot && !bone.IsSkeletonRoot)
                continue;

            Clip.GetOrCreateTrack(bone.Name).AddOrReplace(Playhead, bone.LastRawTransform);
        }
    }

    /// <summary>Keys the actor's root model transform at the current playhead.</summary>
    public void KeyModelTransform() => KeyModelTransformAt(Playhead);

    /// <summary>Keys the actor's root model transform at an explicit time, growing the clip if needed.</summary>
    public void KeyModelTransformAt(float time)
    {
        time = MathF.Max(0f, time);
        Clip.GetOrCreateModelTrack().AddOrReplace(time, ModelPosing.Transform);

        if(time > Duration)
            Duration = time;
    }

    /// <summary>Keys whichever bones are currently selected in the posing tools.</summary>
    public void KeySelected()
    {
        if(!Entity.TryGetCapability<PosingCapability>(out var posing) || posing is null)
            return;

        foreach(var selected in posing.SelectedBones)
            KeyBone(selected.BoneName);
    }

    #endregion

    #region Loop / pose tools

    /// <summary>
    /// Keys every track at the current playhead using its value sampled at <paramref name="sourceTime"/>.
    /// Scrub to the end and call with 0 for a seamless loop (end frame == start frame).
    /// </summary>
    public void KeyAllTracksFromTime(float sourceTime)
    {
        foreach(var track in Clip.BoneTracks.Values)
            if(track.HasKeyframes)
                track.AddOrReplace(Playhead, PoseInterpolation.Sample(track, sourceTime));

        if(Clip.ModelTrack?.HasKeyframes == true)
            Clip.ModelTrack.AddOrReplace(Playhead, PoseInterpolation.Sample(Clip.ModelTrack, sourceTime));

        if(Playhead > Duration)
            Duration = Playhead;
    }

    private readonly Dictionary<string, Transform> _frameClipboard = [];
    private Transform? _frameModelClipboard;
    public bool HasFrameClipboard => _frameClipboard.Count > 0 || _frameModelClipboard.HasValue;

    /// <summary>Copies the whole evaluated pose at the playhead (every track) into the frame clipboard.</summary>
    public void CopyFrame()
    {
        _frameClipboard.Clear();
        _frameModelClipboard = null;

        foreach(var (name, track) in Clip.BoneTracks)
            if(track.HasKeyframes)
                _frameClipboard[name] = PoseInterpolation.Sample(track, Playhead);

        if(Clip.ModelTrack?.HasKeyframes == true)
            _frameModelClipboard = PoseInterpolation.Sample(Clip.ModelTrack, Playhead);
    }

    /// <summary>Keys the copied whole-pose at the current playhead.</summary>
    public void PasteFrame()
    {
        foreach(var (name, value) in _frameClipboard)
            Clip.GetOrCreateTrack(name).AddOrReplace(Playhead, value);

        if(_frameModelClipboard.HasValue)
            Clip.GetOrCreateModelTrack().AddOrReplace(Playhead, _frameModelClipboard.Value);

        if(Playhead > Duration)
            Duration = Playhead;
    }

    /// <summary>Re-keys every existing track at the playhead from the rig's current pose.</summary>
    public void ReKeyExistingTracksAtPlayhead()
    {
        foreach(var name in new List<string>(Clip.BoneTracks.Keys))
            KeyBoneAt(name, Playhead);

        if(Clip.ModelTrack?.HasKeyframes == true)
            KeyModelTransformAt(Playhead);
    }

    /// <summary>
    /// Mirrors the current pose left/right (reusing Brio's MirrorPose) and re-keys the existing
    /// tracks at the playhead once it settles. Best-effort / timing-dependent.
    /// </summary>
    public void MirrorAtPlayhead()
    {
        if(!Entity.TryGetCapability<PosingCapability>(out var posing) || posing is null)
            return;

        EditAtPlayhead();
        posing.MirrorPose();
        _framework.RunOnTick(ReKeyExistingTracksAtPlayhead, delayTicks: 6);
    }

    /// <summary>Moves the playhead to the nearest keyframe before it (any track).</summary>
    public void JumpToPrevKeyframe()
    {
        float? best = null;
        foreach(var t in AllKeyframeTimes())
            if(t < Playhead - 1e-4f && (best is null || t > best))
                best = t;

        if(best.HasValue)
            ScrubTo(best.Value);
    }

    /// <summary>Moves the playhead to the nearest keyframe after it (any track).</summary>
    public void JumpToNextKeyframe()
    {
        float? best = null;
        foreach(var t in AllKeyframeTimes())
            if(t > Playhead + 1e-4f && (best is null || t < best))
                best = t;

        if(best.HasValue)
            ScrubTo(best.Value);
    }

    private IEnumerable<float> AllKeyframeTimes()
    {
        foreach(var track in Clip.BoneTracks.Values)
            foreach(var kf in track.Keyframes)
                yield return kf.Time;

        if(Clip.ModelTrack is not null)
            foreach(var kf in Clip.ModelTrack.Keyframes)
                yield return kf.Time;
    }

    #endregion

    #region Save / load

    public void SaveTo(string path)
    {
        var file = AnimationFile.FromClip(Clip);
        ResourceProvider.Instance.SaveFileDocument(path, file);
    }

    public void LoadFrom(string path)
    {
        var file = ResourceProvider.Instance.GetFileDocument<AnimationFile>(path);
        Stop();
        Clip = file.ToClip();
        Playhead = 0f;
    }

    #endregion

    #region Editing

    /// <summary>
    /// Marks the actor as being authored (the editor window is open for it). Freezes the base
    /// animation and captures a clean base pose so manual posing is stable and keyframes reproduce
    /// exactly. Idempotent — safe to call every frame.
    /// </summary>
    public void BeginEditing()
    {
        IsEditing = true;
        Engage();
    }

    /// <summary>Ends authoring (editor closed). Stops any playback and releases the freeze.</summary>
    public void EndEditing()
    {
        IsEditing = false;
        IsPlaying = false;
        IsPaused = false;
        Disengage();
    }

    #endregion

    #region Playback core

    private void OnFrameworkUpdate(IFramework framework)
    {
        // Only drive the pose while actually playing. When paused/authoring we leave the skeleton
        // alone so manual posing isn't clobbered each frame (the pose was baked in once on pause).
        if(!IsPlaying || !_baseReady)
            return;

        var dt = (float)framework.UpdateDelta.TotalSeconds;
        AdvancePlayhead(dt * PlaybackSpeed);
        ApplyEvaluatedPose();
    }

    private void AdvancePlayhead(float delta)
    {
        Playhead += delta;

        if(Playhead < 0f)
            Playhead = 0f;

        if(Playhead >= Duration)
        {
            if(Loop && Duration > 0f)
            {
                Playhead %= Duration;
            }
            else
            {
                Playhead = Duration;
                IsPlaying = false;
            }
        }
    }

    private void ApplyEvaluatedPose()
    {
        foreach(var (boneName, track) in Clip.BoneTracks)
        {
            if(!track.HasKeyframes)
                continue;

            var bone = SkeletonPosing.GetBone(boneName, PoseInfoSlot.Character);
            if(bone is null)
                continue;

            // Lazily capture the base for tracks that appeared after activation. Safe because a
            // brand-new track's bone has no override yet, so LastRawTransform is still the base.
            if(!_baseTransforms.TryGetValue(boneName, out var baseTransform))
            {
                baseTransform = bone.LastRawTransform;
                _baseTransforms[boneName] = baseTransform;
            }

            var target = PoseInterpolation.Sample(track, Playhead);

            var poseInfo = SkeletonPosing.GetBonePose(bone);
            poseInfo.ClearStacks();
            poseInfo.Apply(
                target,
                baseTransform,
                TransformComponents.All,
                TransformComponents.All,
                BoneIKInfo.Disabled,
                PoseMirrorMode.None,
                forceNewStack: true);
        }

        if(Clip.ModelTrack?.HasKeyframes == true)
        {
            ModelPosing.Transform = PoseInterpolation.Sample(Clip.ModelTrack, Playhead);
            _modelOverridden = true;
        }
    }

    #endregion

    #region Engage / freeze

    /// <summary>
    /// Freezes the actor's base animation and captures a clean base pose. Both editing and playback
    /// require this fixed, override-free reference. Idempotent.
    /// </summary>
    private void Engage()
    {
        if(_engaged)
            return;

        _engaged = true;
        _baseReady = false;
        _baseTransforms.Clear();

        if(Actor.TryGetCapability<ActionTimelineCapability>(out var actionTimeline) && actionTimeline is not null)
        {
            _originalSpeed = actionTimeline.SpeedMultiplier;

            // Clear any manual pose so the captured base is the clean frozen animation pose, then
            // freeze the base animation (zeroes speed + each control's local time) and capture the
            // base once it has settled.
            SkeletonPosing.ResetPose();
            actionTimeline.SetOverallSpeedOverride(0f);
            actionTimeline.StopSpeedAndResetTimeline(CaptureBase, resetSpeedAfterAction: false);
        }
        else
        {
            // No timeline to freeze (e.g. a prop) — capture on the next tick.
            SkeletonPosing.ResetPose();
            _framework.RunOnTick(CaptureBase, delayTicks: 2);
        }
    }

    private void CaptureBase()
    {
        _baseTransforms.Clear();

        // Capture every character bone (not just currently-keyed ones) so tracks added later during
        // authoring already have a clean base reference.
        var skeleton = SkeletonPosing.CharacterSkeleton;
        if(skeleton is not null)
        {
            foreach(var bone in skeleton.Bones)
                _baseTransforms[bone.Name] = bone.LastRawTransform;
        }

        _baseReady = true;
    }

    private void Disengage()
    {
        if(!_engaged)
            return;

        _engaged = false;
        _baseReady = false;
        _baseTransforms.Clear();

        SkeletonPosing.ResetPose();

        if(_modelOverridden)
        {
            ModelPosing.ResetTransform();
            _modelOverridden = false;
        }

        if(Actor.TryGetCapability<ActionTimelineCapability>(out var actionTimeline) && actionTimeline is not null)
        {
            actionTimeline.SetOverallSpeedOverride(_originalSpeed <= 0f ? 1f : _originalSpeed);
            actionTimeline.ResetOverallSpeedOverride();
        }
    }

    #endregion

    public override void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
        Disengage();
        base.Dispose();
    }

    public static AnimationCapability? CreateIfEligible(IServiceProvider provider, ActorEntity entity)
    {
        if(entity.GameObject is ICharacter)
            return ActivatorUtilities.CreateInstance<AnimationCapability>(provider, entity);

        return null;
    }
}
