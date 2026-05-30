using Brio.Capabilities.Posing;
using Brio.UI.Widgets.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using System.Numerics;

namespace Brio.UI.Widgets.Posing;

/// <summary>
/// Compact transport panel for the actor's <see cref="AnimationCapability"/>: play/pause/stop,
/// loop, speed and a playhead scrubber, plus a button to open the full per-bone dopesheet editor
/// (where keyframes are authored).
/// </summary>
public class AnimationWidget(AnimationCapability capability) : Widget<AnimationCapability>(capability)
{
    public override string HeaderName => "Animation";

    public override WidgetFlags Flags =>
        Capability.Actor.IsProp ? WidgetFlags.None : WidgetFlags.DrawBody | WidgetFlags.HasAdvanced;

    public override void DrawBody()
    {
        var cap = Capability;

        if(ImGui.Button(cap.IsPlaying ? "Pause" : "Play"))
            cap.TogglePlay();

        ImGui.SameLine();

        if(ImGui.Button("Stop"))
            cap.Stop();

        ImGui.SameLine();

        var loop = cap.Loop;
        if(ImGui.Checkbox("Loop", ref loop))
            cap.Loop = loop;

        var playhead = cap.Playhead;
        ImGui.SetNextItemWidth(-1);
        if(ImGui.SliderFloat("###anim_playhead", ref playhead, 0f, cap.Duration, "%.2fs"))
            cap.ScrubTo(playhead);

        var speed = cap.PlaybackSpeed;
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        if(ImGui.SliderFloat("Speed", ref speed, 0f, 3f, "%.2fx"))
            cap.PlaybackSpeed = speed;

        ImGui.Separator();

        if(ImGui.Button("Open Animation Editor", new Vector2(-1, 0)))
            UIManager.Instance.ToggleAnimationEditorWindow();

        ImGui.TextDisabled($"{cap.Clip.BoneTracks.Count} track(s) · key bones in the editor");
    }

    public override void ToggleAdvancedWindow()
    {
        UIManager.Instance.ToggleAnimationEditorWindow();
    }
}
