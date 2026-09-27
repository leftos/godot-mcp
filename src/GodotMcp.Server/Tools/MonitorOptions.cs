using System.ComponentModel;

namespace GodotMcp.Server.Tools;

/// <summary>How many frames monitor_property samples, which frames, and whether it keeps only the changes.</summary>
internal sealed record MonitorOptions(
    [property: Description("How many samples to take, one a frame (or physics tick), 1 to 600.")] int Samples = 60,
    [property: Description("process (the default): sample at the start of each process frame; physics: at the start of each physics tick.")]
        string Unit = "process",
    [property: Description(
        "Drop a sample equal to the last one kept (numbers within 1e-6) and count it in droppedDuplicates; the first is always kept."
    )]
        bool ChangesOnly = true
);
