using Brio.Capabilities.Actor;
using Brio.Core;
using Brio.Entities.Actor;
using Brio.Files;
using Brio.Game.Actor.Extensions;
using Brio.Game.Posing;
using Brio.Game.Posing.Animation;
using Brio.Game.Posing.Skeletons;
using Brio.Resources;
using Brio.UI.Widgets.Posing;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
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

    private bool _engaged;
    private bool _baseReady;
    private float _originalSpeed = 1f;
    private bool _modelOverridden;

    // The pose the actor had when we engaged (e.g. one loaded in Brio's Posing panel). We lock to
    // this as the rest pose and restore it when playback stops, instead of snapping back to idle.
    private PoseInfo? _restPose;

    // Importer options used to re-apply the evaluated pose each frame (all components, all bones).
    private readonly PoseImporterOptions _importerOptions;

    private SkeletonPosingCapability SkeletonPosing => Entity.GetCapability<SkeletonPosingCapability>();
    private ModelPosingCapability ModelPosing => Entity.GetCapability<ModelPosingCapability>();

    public AnimationCapability(ActorEntity parent, IFramework framework, PosingService posingService) : base(parent)
    {
        _framework = framework;
        _importerOptions = new PoseImporterOptions(new BoneFilter(posingService), TransformComponents.All, false);
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
            RestoreRestPose();
        else
            Disengage();
    }

    /// <summary>
    /// Moves the playhead. While playing or paused this seeks the previewed frame; while authoring
    /// it only moves the time cursor (where the next keyframe lands) and never touches the pose, so
    /// you can pose freely at any time and key it.
    /// </summary>
    /// <summary>Discards the whole clip and returns to the rest pose.</summary>
    public void ClearClip()
    {
        IsPlaying = false;
        IsPaused = false;
        Playhead = 0f;
        Clip = new AnimationClip();
        RestoreRestPose();
    }

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

    #region Import from game animation

    public bool IsBaking { get; private set; }

    /// <summary>
    /// Plays a game timeline animation, scrubs its internal time across the full duration, and
    /// snapshots every bone at each sample into editable keyframes (replacing the current clip).
    /// Runs across several frames. Experimental.
    /// </summary>
    public void BakeFromGameAnimation(ushort animationId, int sampleCount)
    {
        if(IsBaking || animationId == 0 || sampleCount < 2)
            return;

        if(!Actor.TryGetCapability<ActionTimelineCapability>(out var actionTimeline) || actionTimeline is null)
            return;

        IsBaking = true;
        IsPlaying = false;
        IsPaused = false;

        // Clear our overrides, then PLAY the chosen animation at normal speed first. It must
        // actually blend in and drive the skeleton — if we freeze immediately it never takes over
        // and every sample just captures the idle pose (looks static / "nothing happens").
        SkeletonPosing.ResetPose();
        actionTimeline.ApplyBaseOverride(animationId, interrupt: true);
        actionTimeline.SetOverallSpeedOverride(1f);

        _framework.RunOnTick(() =>
        {
            // Now that it's blended in, freeze it so we can scrub it frame-by-frame.
            actionTimeline.SetOverallSpeedOverride(0f);
            _framework.RunOnTick(() => BakeStart(actionTimeline, animationId, sampleCount), delayTicks: 4);
        }, delayTicks: 20);
    }

    private void BakeStart(ActionTimelineCapability actionTimeline, ushort animationId, int sampleCount)
    {
        var duration = GetCurrentAnimationDuration();
        if(duration <= 0f)
        {
            FinishBake(actionTimeline);
            return;
        }

        Clip = new AnimationClip { Name = $"Game anim {animationId}", Duration = duration };

        var times = new float[sampleCount];
        for(var i = 0; i < sampleCount; i++)
            times[i] = duration * i / (sampleCount - 1);

        BakeSample(actionTimeline, times, 0);
    }

    private void BakeSample(ActionTimelineCapability actionTimeline, float[] times, int index)
    {
        if(index >= times.Length)
        {
            FinishBake(actionTimeline);
            return;
        }

        SetAnimationLocalTime(times[index]);

        // Let the skeleton evaluate the new local time, then snapshot it on the next tick.
        _framework.RunOnTick(() =>
        {
            CaptureAllBonesAt(times[index]);
            BakeSample(actionTimeline, times, index + 1);
        }, delayTicks: 2);
    }

    private void FinishBake(ActionTimelineCapability actionTimeline)
    {
        actionTimeline.ResetBaseOverride();
        actionTimeline.ResetOverallSpeedOverride();

        IsBaking = false;
        Playhead = 0f;

        // Force a re-engage on the next editor frame so the baked clip plays from a fresh rest pose.
        _engaged = false;
        _baseReady = false;
    }

    private void CaptureAllBonesAt(float time)
    {
        var skeleton = SkeletonPosing.CharacterSkeleton;
        if(skeleton is null)
            return;

        foreach(var bone in skeleton.Bones)
        {
            if(bone.IsPartialRoot && !bone.IsSkeletonRoot)
                continue;

            Clip.GetOrCreateTrack(bone.Name).AddOrReplace(time, bone.LastRawTransform);
        }
    }

    private unsafe float GetCurrentAnimationDuration()
    {
        var drawObject = Character.Native()->GameObject.DrawObject;
        if(drawObject == null || drawObject->Object.GetObjectType() != ObjectType.CharacterBase)
            return 0f;

        var charaBase = (CharacterBase*)drawObject;
        if(charaBase->Skeleton == null || charaBase->Skeleton->PartialSkeletonCount <= 0)
            return 0f;

        var partial = &charaBase->Skeleton->PartialSkeletons[0];
        var animatedSkele = partial->GetHavokAnimatedSkeleton(0);
        if(animatedSkele == null || animatedSkele->AnimationControls.Length <= 0)
            return 0f;

        var control = animatedSkele->AnimationControls[0].Value;
        if(control == null)
            return 0f;

        var binding = control->hkaAnimationControl.Binding;
        if(binding.ptr == null || binding.ptr->Animation.ptr == null)
            return 0f;

        return binding.ptr->Animation.ptr->Duration;
    }

    private unsafe void SetAnimationLocalTime(float time)
    {
        var drawObject = Character.Native()->GameObject.DrawObject;
        if(drawObject == null || drawObject->Object.GetObjectType() != ObjectType.CharacterBase)
            return;

        var charaBase = (CharacterBase*)drawObject;
        if(charaBase->Skeleton == null)
            return;

        for(var p = 0; p < charaBase->Skeleton->PartialSkeletonCount; ++p)
        {
            var partial = &charaBase->Skeleton->PartialSkeletons[p];
            var animatedSkele = partial->GetHavokAnimatedSkeleton(0);
            if(animatedSkele == null)
                continue;

            for(var c = 0; c < animatedSkele->AnimationControls.Length; ++c)
            {
                var control = animatedSkele->AnimationControls[c].Value;
                if(control == null)
                    continue;

                var binding = control->hkaAnimationControl.Binding;
                if(binding.ptr == null || binding.ptr->Animation.ptr == null)
                    continue;

                control->hkaAnimationControl.LocalTime = Math.Clamp(time, 0f, binding.ptr->Animation.ptr->Duration);
            }
        }
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
        // Re-import the evaluated pose each frame through Brio's importer. The importer keys each
        // bone's delta against the *live* (parent-first) bone transform, so parent rotations don't
        // compound into their children, and it reproduces each keyframe's absolute pose regardless
        // of the frozen base. (Writing fixed model-space deltas ourselves double-counted propagation
        // down the chain and exaggerated the motion.)
        var pose = PoseInterpolation.SampleClip(Clip, Playhead);
        SkeletonPosing.ResetPose();
        SkeletonPosing.ImportSkeletonPose(pose, _importerOptions);

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

        // Lock to whatever pose the actor currently has (e.g. one loaded in Brio's Posing panel) as
        // the rest pose — don't wipe it. We restore it when playback stops.
        _restPose = SkeletonPosing.PoseInfo.Clone();

        if(Actor.TryGetCapability<ActionTimelineCapability>(out var actionTimeline) && actionTimeline is not null)
        {
            _originalSpeed = actionTimeline.SpeedMultiplier;

            // Freeze the base animation (zeroes speed + each control's local time) so the underlying
            // pose is static; capture readiness once it has settled.
            actionTimeline.SetOverallSpeedOverride(0f);
            actionTimeline.StopSpeedAndResetTimeline(CaptureBase, resetSpeedAfterAction: false);
        }
        else
        {
            // No timeline to freeze (e.g. a prop) — mark ready on the next tick.
            _framework.RunOnTick(CaptureBase, delayTicks: 2);
        }
    }

    private void CaptureBase() => _baseReady = true;

    private void RestoreRestPose()
    {
        SkeletonPosing.PoseInfo = _restPose?.Clone() ?? new PoseInfo();
    }

    private void Disengage()
    {
        if(!_engaged)
            return;

        _engaged = false;
        _baseReady = false;

        RestoreRestPose();

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
