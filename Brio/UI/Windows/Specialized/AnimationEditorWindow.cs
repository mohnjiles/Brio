using Brio.Capabilities.Posing;
using Brio.Entities;
using Brio.Entities.Actor;
using Brio.Game.GPose;
using Brio.Game.Posing.Animation;
using Brio.UI.Controls.Stateless;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Brio.UI.Windows.Specialized;

/// <summary>
/// Per-bone keyframe dopesheet for an actor's <see cref="AnimationCapability"/>: transport,
/// a track-per-bone timeline with draggable keyframes, playhead scrubbing, and a keyframe
/// inspector (time + easing). Operates on whichever actor is currently selected.
/// </summary>
public class AnimationEditorWindow : Window, IDisposable
{
    private readonly EntityManager _entityManager;
    private readonly GPoseService _gPoseService;

    // Layout constants (logical px, scaled by the global UI scale at draw time).
    private const float LabelWidth = 150f;
    private const float RowHeight = 22f;
    private const float RulerHeight = 22f;
    private const float KeyframeRadius = 6f;
    private const float KeyframeHitRadius = 9f;

    // Interaction state. The window is a singleton; only one actor is edited at a time.
    private Keyframe? _selectedKeyframe;
    private BoneTrack? _selectedTrack;
    private Keyframe? _dragKeyframe;
    private BoneTrack? _dragTrack;
    private bool _scrubbing;
    private int _bezierHandle = -1;

    // The capability we last told to begin editing, so we can end it on close / actor change.
    private AnimationCapability? _editingCapability;

    // Copy/paste clipboard for a single keyframe (pose + easing). Persists across selections.
    private Keyframe? _clipboard;

    public AnimationEditorWindow(EntityManager entityManager, GPoseService gPoseService)
        : base($"{Brio.Name} - ANIMATION EDITOR###brio_animation_editor_window")
    {
        Namespace = "brio_animation_editor_namespace";

        _entityManager = entityManager;
        _gPoseService = gPoseService;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 360),
            MaximumSize = new Vector2(5000, 5000)
        };

        _gPoseService.OnGPoseStateChange += OnGPoseStateChange;
    }

    public override bool DrawConditions()
    {
        if(_entityManager.SelectedEntity is ActorEntity actor && actor.IsProp)
            return false;

        if(!_entityManager.SelectedHasCapability<AnimationCapability>())
            return false;

        return base.DrawConditions();
    }

    public override void Draw()
    {
        if(!_entityManager.TryGetCapabilityFromSelectedEntity<AnimationCapability>(out var cap, considerParents: true))
        {
            StopEditing();
            return;
        }

        // Engage the actor for authoring while this window is open for it (freeze + capture base).
        if(!ReferenceEquals(_editingCapability, cap))
        {
            _editingCapability?.EndEditing();
            _editingCapability = cap;
        }
        cap.BeginEditing();

        WindowName = $"{Brio.Name} - Animation Editor - {cap.Entity.FriendlyName}###brio_animation_editor_window";

        DrawTransport(cap);
        ImGui.Separator();
        DrawDopesheet(cap);
        DrawKeyframeInspector(cap);
    }

    private void DrawTransport(AnimationCapability cap)
    {
        if(ImGui.Button(cap.IsPlaying ? "Pause" : "Play", new Vector2(70, 0)))
            cap.TogglePlay();

        ImGui.SameLine();
        if(ImGui.Button("Stop", new Vector2(70, 0)))
            cap.Stop();

        ImGui.SameLine();
        if(ImGui.Button("Edit @ Playhead", new Vector2(130, 0)))
            cap.EditAtPlayhead();
        AttachTooltip("Load the pose at the playhead onto the rig so you can keep posing from here");

        ImGui.SameLine();
        var loop = cap.Loop;
        if(ImGui.Checkbox("Loop", ref loop))
            cap.Loop = loop;

        ImGui.SameLine();
        var speed = cap.PlaybackSpeed;
        ImGui.SetNextItemWidth(110 * ImGuiHelpers.GlobalScale);
        if(ImGui.SliderFloat("Speed", ref speed, 0f, 3f, "%.2fx"))
            cap.PlaybackSpeed = speed;

        ImGui.SameLine();
        var duration = cap.Duration;
        ImGui.SetNextItemWidth(110 * ImGuiHelpers.GlobalScale);
        if(ImGui.InputFloat("Duration", ref duration, 0.5f, 1f, "%.2f"))
            cap.Duration = duration;

        if(ImGui.Button("Key Selected", new Vector2(120, 0)))
            cap.KeySelected();
        AttachTooltip("Key the selected bone(s) at the playhead");

        ImGui.SameLine();
        if(ImGui.Button("Key All Bones", new Vector2(120, 0)))
            cap.KeyAll();
        AttachTooltip("Key every body bone at the playhead");

        ImGui.SameLine();
        if(ImGui.Button("Save", new Vector2(80, 0)))
        {
            UIManager.Instance.FileDialogManager.SaveFileDialog("Save Animation###save_anim", "Brio Animation (*.bclip){.bclip}", "animation", ".bclip",
                (success, path) =>
                {
                    if(!success)
                        return;
                    if(!path.EndsWith(".bclip"))
                        path += ".bclip";
                    cap.SaveTo(path);
                });
        }

        ImGui.SameLine();
        if(ImGui.Button("Load", new Vector2(80, 0)))
        {
            UIManager.Instance.FileDialogManager.OpenFileDialog("Load Animation###load_anim", "Brio Animation (*.bclip){.bclip}",
                (success, paths) =>
                {
                    if(success && paths.Count == 1)
                        cap.LoadFrom(paths[0]);
                }, 1);
        }

        ImGui.SameLine();
        ImGui.Text($"{(int)(cap.Playhead * 1000)} ms / {cap.Duration:0.00}s");
    }

    private void DrawDopesheet(AnimationCapability cap)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var labelWidth = LabelWidth * scale;
        var rowHeight = RowHeight * scale;
        var rulerHeight = RulerHeight * scale;

        using var child = Dalamud.Interface.Utility.Raii.ImRaii.Child("##anim_dopesheet", new Vector2(0, -120 * scale), true);
        if(!child.Success)
            return;

        var clip = cap.Clip;
        var tracks = CollectTracks(clip);

        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail();

        var laneX0 = origin.X + labelWidth;
        var laneWidth = MathF.Max(10f, avail.X - labelWidth);
        var duration = MathF.Max(0.01f, clip.Duration);

        float TimeToX(float t) => laneX0 + (t / duration) * laneWidth;
        float XToTime(float x) => Math.Clamp((x - laneX0) / laneWidth, 0f, 1f) * duration;

        var rulerColor = ImGui.GetColorU32(new Vector4(0.6f, 0.6f, 0.6f, 1f));
        var gridColor = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.06f));
        var laneColor = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.03f));
        var keyColor = ImGui.GetColorU32(new Vector4(0.40f, 0.65f, 1f, 1f));
        var keySelectedColor = ImGui.GetColorU32(new Vector4(1f, 0.85f, 0.2f, 1f));
        var keyOutline = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 1f));
        var playheadColor = ImGui.GetColorU32(new Vector4(1f, 0.3f, 0.3f, 1f));

        var totalHeight = rulerHeight + tracks.Count * rowHeight;

        // Background grid + second markers on the ruler.
        for(var s = 0; s <= (int)MathF.Ceiling(duration); s++)
        {
            var x = TimeToX(s);
            drawList.AddLine(new Vector2(x, origin.Y + rulerHeight), new Vector2(x, origin.Y + totalHeight), gridColor, 1f);
            drawList.AddText(new Vector2(x + 2, origin.Y + 2), rulerColor, $"{s}s");
        }

        // Lane backgrounds + labels.
        for(var i = 0; i < tracks.Count; i++)
        {
            var rowY = origin.Y + rulerHeight + i * rowHeight;
            if((i & 1) == 0)
                drawList.AddRectFilled(new Vector2(laneX0, rowY), new Vector2(laneX0 + laneWidth, rowY + rowHeight), laneColor);

            drawList.AddText(new Vector2(origin.X + 4, rowY + 3), rulerColor, Truncate(tracks[i].Label, labelWidth - 8f));
        }

        // One invisible button captures all interaction; we hit-test keyframes manually so we
        // don't have to fight ImGui's overlapping-item rules.
        ImGui.SetCursorScreenPos(origin);
        ImGui.InvisibleButton("##anim_dopesheet_area", new Vector2(avail.X, MathF.Max(totalHeight, avail.Y)));
        var mouse = ImGui.GetMousePos();

        HandleInteraction(cap, tracks, origin.Y + rulerHeight, rowHeight, TimeToX, XToTime, mouse);

        // Keyframes.
        for(var i = 0; i < tracks.Count; i++)
        {
            var rowCenterY = origin.Y + rulerHeight + i * rowHeight + rowHeight * 0.5f;
            foreach(var kf in tracks[i].Track.Keyframes)
            {
                var cx = TimeToX(kf.Time);
                var color = ReferenceEquals(kf, _selectedKeyframe) ? keySelectedColor : keyColor;
                DrawDiamond(drawList, new Vector2(cx, rowCenterY), KeyframeRadius * scale, color, keyOutline);
            }
        }

        // Playhead.
        var playX = TimeToX(cap.Playhead);
        drawList.AddLine(new Vector2(playX, origin.Y), new Vector2(playX, origin.Y + totalHeight), playheadColor, 2f);
        drawList.AddTriangleFilled(
            new Vector2(playX - 5, origin.Y),
            new Vector2(playX + 5, origin.Y),
            new Vector2(playX, origin.Y + 8),
            playheadColor);
    }

    private void HandleInteraction(AnimationCapability cap, List<TrackRow> tracks, float tracksTop, float rowHeight,
        Func<float, float> timeToX, Func<float, float> xToTime, Vector2 mouse)
    {
        var scale = ImGuiHelpers.GlobalScale;

        if(ImGui.IsItemActivated())
        {
            // Press: try to grab a keyframe, otherwise start scrubbing.
            _dragKeyframe = null;
            _dragTrack = null;
            _scrubbing = false;

            var row = (int)((mouse.Y - tracksTop) / rowHeight);
            if(row >= 0 && row < tracks.Count)
            {
                var rowCenterY = tracksTop + row * rowHeight + rowHeight * 0.5f;
                Keyframe? best = null;
                var bestDist = KeyframeHitRadius * scale;
                foreach(var kf in tracks[row].Track.Keyframes)
                {
                    var dist = MathF.Abs(timeToX(kf.Time) - mouse.X);
                    if(dist <= bestDist && MathF.Abs(mouse.Y - rowCenterY) <= rowHeight * 0.5f)
                    {
                        best = kf;
                        bestDist = dist;
                    }
                }

                if(best is not null)
                {
                    _selectedKeyframe = best;
                    _selectedTrack = tracks[row].Track;
                    _dragKeyframe = best;
                    _dragTrack = tracks[row].Track;
                    return;
                }
            }

            _scrubbing = true;
            cap.ScrubTo(xToTime(mouse.X));
        }
        else if(ImGui.IsItemActive())
        {
            if(_dragKeyframe is not null)
                _dragKeyframe.Time = Math.Clamp(xToTime(mouse.X), 0f, cap.Duration);
            else if(_scrubbing)
                cap.ScrubTo(xToTime(mouse.X));
        }
        else if(ImGui.IsItemDeactivated())
        {
            _dragTrack?.Sort();
            _dragKeyframe = null;
            _dragTrack = null;
            _scrubbing = false;
        }
    }

    private void DrawKeyframeInspector(AnimationCapability cap)
    {
        ImGui.Separator();

        if(_selectedKeyframe is null || _selectedTrack is null)
        {
            ImGui.TextDisabled("No keyframe selected. Click a keyframe diamond to edit it.");
            return;
        }

        var kf = _selectedKeyframe;

        ImGui.Text($"Keyframe @ {kf.Time:0.000}s on '{_selectedTrack.BoneName}'");

        var time = kf.Time;
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        if(ImGui.InputFloat("Time", ref time, 0.05f, 0.1f, "%.3f"))
        {
            kf.Time = Math.Clamp(time, 0f, cap.Duration);
            _selectedTrack.Sort();
        }

        ImGui.SameLine();
        var easing = kf.EaseToNext;
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        using(var combo = Dalamud.Interface.Utility.Raii.ImRaii.Combo("Easing", easing.Type.ToString()))
        {
            if(combo.Success)
            {
                foreach(var type in Enum.GetValues<EasingType>())
                {
                    if(ImGui.Selectable(type.ToString(), easing.Type == type))
                    {
                        var next = easing;
                        next.Type = type;
                        kf.EaseToNext = next;
                    }
                }
            }
        }

        if(ImGui.Button("Copy", new Vector2(70, 0)))
            _clipboard = kf.Clone();
        AttachTooltip("Copy this keyframe's pose + easing");

        ImGui.SameLine();
        using(Dalamud.Interface.Utility.Raii.ImRaii.Disabled(_clipboard is null))
        {
            if(ImGui.Button("Paste", new Vector2(70, 0)) && _clipboard is not null)
            {
                // Paste onto the selected track at the playhead — copy frame 0 here at the end for a clean loop.
                _selectedKeyframe = _selectedTrack.AddOrReplace(cap.Playhead, _clipboard.Value, _clipboard.EaseToNext);
            }
        }
        AttachTooltip("Paste the copied pose to this track at the playhead (great for clean loops)");

        ImGui.SameLine();
        if(ImGui.Button("Delete", new Vector2(90, 0)))
        {
            _selectedTrack.Remove(kf);
            if(!_selectedTrack.HasKeyframes)
                cap.Clip.RemoveTrack(_selectedTrack.BoneName);
            _selectedKeyframe = null;
            _selectedTrack = null;
            return;
        }

        if(kf.EaseToNext.Type == EasingType.Bezier)
            DrawBezierEditor(kf);
    }

    /// <summary>
    /// Draggable cubic-bezier curve editor for a keyframe's ease-to-next. The X axis is the
    /// normalized segment time, the Y axis is the eased blend factor. Handle X is clamped to
    /// [0,1] (keeps the curve solvable); Y is allowed to overshoot slightly for anticipation/
    /// follow-through.
    /// </summary>
    private void DrawBezierEditor(Keyframe kf)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var size = 140f * scale;
        var grab = 8f * scale;

        var easing = kf.EaseToNext;

        var drawList = ImGui.GetWindowDrawList();
        var canvas = ImGui.GetCursorScreenPos();

        Vector2 ToScreen(Vector2 p) => new(canvas.X + p.X * size, canvas.Y + (1f - p.Y) * size);
        Vector2 ToCurve(Vector2 s) => new(
            Math.Clamp((s.X - canvas.X) / size, 0f, 1f),
            Math.Clamp(1f - (s.Y - canvas.Y) / size, -0.5f, 1.5f));

        var border = ImGui.GetColorU32(new Vector4(0.5f, 0.5f, 0.5f, 1f));
        var refLine = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.12f));
        var curveColor = ImGui.GetColorU32(new Vector4(0.4f, 0.8f, 1f, 1f));
        var handleColor = ImGui.GetColorU32(new Vector4(1f, 0.85f, 0.2f, 1f));

        drawList.AddRectFilled(canvas, new Vector2(canvas.X + size, canvas.Y + size), ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.25f)));
        drawList.AddRect(canvas, new Vector2(canvas.X + size, canvas.Y + size), border);
        drawList.AddLine(ToScreen(new Vector2(0, 0)), ToScreen(new Vector2(1, 1)), refLine);

        // Sample the curve.
        const int segments = 32;
        var prev = ToScreen(new Vector2(0, 0));
        for(var i = 1; i <= segments; i++)
        {
            var x = i / (float)segments;
            var y = PoseInterpolation.CubicBezier(easing.P1, easing.P2, x);
            var pt = ToScreen(new Vector2(x, y));
            drawList.AddLine(prev, pt, curveColor, 2f);
            prev = pt;
        }

        // Handle guide lines + dots.
        var p1Screen = ToScreen(easing.P1);
        var p2Screen = ToScreen(easing.P2);
        drawList.AddLine(ToScreen(new Vector2(0, 0)), p1Screen, handleColor, 1f);
        drawList.AddLine(ToScreen(new Vector2(1, 1)), p2Screen, handleColor, 1f);
        drawList.AddCircleFilled(p1Screen, 4f * scale, handleColor);
        drawList.AddCircleFilled(p2Screen, 4f * scale, handleColor);

        ImGui.InvisibleButton("##bezier_canvas", new Vector2(size, size));
        var mouse = ImGui.GetMousePos();

        if(ImGui.IsItemActivated())
            _bezierHandle = Vector2.Distance(mouse, p1Screen) <= Vector2.Distance(mouse, p2Screen) ? 0 : 1;

        if(ImGui.IsItemActive() && _bezierHandle >= 0)
        {
            var curvePt = ToCurve(mouse);
            if(_bezierHandle == 0)
                easing.P1 = curvePt;
            else
                easing.P2 = curvePt;
            kf.EaseToNext = easing;
        }

        if(ImGui.IsItemDeactivated())
            _bezierHandle = -1;
    }

    private static List<TrackRow> CollectTracks(AnimationClip clip)
    {
        var rows = new List<TrackRow>(clip.BoneTracks.Count + 1);
        if(clip.ModelTrack is { HasKeyframes: true } modelTrack)
            rows.Add(new TrackRow("Model Transform", modelTrack));

        foreach(var (name, track) in clip.BoneTracks)
            rows.Add(new TrackRow(name, track));

        return rows;
    }

    private static void DrawDiamond(ImDrawListPtr drawList, Vector2 center, float radius, uint fill, uint outline)
    {
        var top = new Vector2(center.X, center.Y - radius);
        var right = new Vector2(center.X + radius, center.Y);
        var bottom = new Vector2(center.X, center.Y + radius);
        var left = new Vector2(center.X - radius, center.Y);

        drawList.AddQuadFilled(top, right, bottom, left, fill);
        drawList.AddQuad(top, right, bottom, left, outline, 1.5f);
    }

    private static string Truncate(string text, float maxWidth)
    {
        if(ImGui.CalcTextSize(text).X <= maxWidth)
            return text;

        while(text.Length > 1 && ImGui.CalcTextSize(text + "…").X > maxWidth)
            text = text[..^1];

        return text + "…";
    }

    public override void OnClose() => StopEditing();

    private void StopEditing()
    {
        _editingCapability?.EndEditing();
        _editingCapability = null;
    }

    private static void AttachTooltip(string text)
    {
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }

    private void OnGPoseStateChange(bool newState)
    {
        if(!newState)
        {
            StopEditing();
            IsOpen = false;
        }
    }

    public void Dispose()
    {
        _gPoseService.OnGPoseStateChange -= OnGPoseStateChange;
    }

    private readonly struct TrackRow(string label, BoneTrack track)
    {
        public string Label { get; } = label;
        public BoneTrack Track { get; } = track;
    }
}
