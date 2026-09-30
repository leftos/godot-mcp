using System.ComponentModel;

namespace GodotMcp.Server.Tools;

/// <summary>How attach_project sets up the game's window and pads, the session's name, and which dormant game it joins.</summary>
internal sealed record AttachOptions(
    [property: Description(ProjectTools.AttachQuietEffect + ProjectTools.ArmedDefault)] bool? Quiet = null,
    [property: Description(
        ProjectTools.MuteEffect + ProjectTools.ArmedDefault + " Unlike quiet, it may differ from the arm's: it is this game's alone."
    )]
        bool? Mute = null,
    [property: Description(ProjectTools.ShutOutEffect + ProjectTools.ArmedDefault)] bool? ShutOutRealGamepads = null,
    [property: Description(ProjectTools.NewSessionDescription)] string? Session = null,
    [property: Description(
        "The process id of the dormant game to join on an armed folder, as arm_project's and list_sessions' dormant lists give it. "
            + "Needed only when several games wait; when left out, the only dormant game is joined, and with none the attach "
            + "waits for a game launched after the call."
    )]
        int? Pid = null
);

/// <summary>How arm_project sets up the window, sound and pads of the games started on the folder while it is armed.</summary>
internal sealed record ArmOptions(
    [property: Description(ProjectTools.AttachQuietDescription)] bool Quiet = false,
    [property: Description(
        ProjectTools.MuteEffect
            + " A dormant game waits muted when mute or quiet is set, and an attach takes it when it leaves "
            + "mute out. Default false."
    )]
        bool Mute = false,
    [property: Description(ProjectTools.ShutOutDescription)] bool ShutOutRealGamepads = false
);
