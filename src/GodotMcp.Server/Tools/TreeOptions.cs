using System.ComponentModel;

namespace GodotMcp.Server.Tools;

/// <summary>How deep get_scene_tree walks and which page of its list it returns.</summary>
internal sealed record TreeOptions(
    [property: Description("How many levels below root to list: 0 lists root alone; every level when left out.")] int? MaxDepth = null,
    [property: Description("How many nodes of the list to skip: 0 (the default), or the next of the previous page.")] int? Offset = null,
    [property: Description("How many nodes to return, 1 to 500; 100 by default.")] int? Limit = null
);
