using System.ComponentModel;
using System.Text.Json.Nodes;

namespace GodotMcp.Server.Tools;

/// <summary>One step of batch_scene_operations: a scene edit tool by its name, and its own arguments but the batch's two.</summary>
internal sealed record SceneBatchStep(
    [property: Description(
        "One of: delete_nodes, attach_script, duplicate_node, move_node, load_sprite, add_node, set_node_properties, "
            + "connect_signal, disconnect_signal, export_mesh_library."
    )]
        string Tool,
    [property: Description("The tool's own arguments, as it takes them on its own, without projectPath and scenePath.")] JsonObject? Args
);
