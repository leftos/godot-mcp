using System.ComponentModel;

namespace GodotMcp.Server.Tools;

/// <summary>Which members cs_members returns: a name filter, whether non-public ones are included, and the page.</summary>
internal sealed record MembersOptions(
    [property: Description("Case-insensitive part of the member name; every member when left out.")] string? Name = null,
    [property: Description("Include private, protected and internal members; true when left out.")] bool? NonPublic = null,
    [property: Description("How many members of the list to skip: 0 (the default), or the next of the previous page.")] int? Offset = null,
    [property: Description("How many members to return, 1 to 500; 100 by default.")] int? Limit = null
);
