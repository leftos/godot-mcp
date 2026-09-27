using System.Text.Json.Nodes;
using GodotMcp.Dotnet.Core;

namespace GodotMcp.Dotnet;

/// <summary>
/// The helper's <c>members</c> op: the members of the type behind the request's <c>{node}</c>, <c>{type}</c> or
/// <c>{handle}</c> target, as <see cref="MemberListing"/> lists them, or the reply's refusal.
/// </summary>
internal static class Members
{
    private static readonly TargetHints Hints = new("cs_members", "describe_class lists its API");

    public static JsonObject Answer(JsonObject request)
    {
        bool nonPublic = request["nonPublic"]?.GetValue<bool>() ?? true;
        string? name = request["name"]?.GetValue<string>();
        Resolution resolution = Targets.Resolve(request["target"]!.AsObject(), Hints);
        if (resolution.Failure is { } failure)
        {
            return failure;
        }
        Target target = resolution.Found!;
        MemberScope scope = target.Instance is null ? MemberScope.StaticAndConstructors : MemberScope.InstanceAndStatic;
        return Listing(target.Type, scope, nonPublic, name);
    }

    private static JsonObject Listing(Type type, MemberScope scope, bool nonPublic, string? name)
    {
        JsonArray members = [];
        foreach (MemberEntry entry in MemberListing.List(type, scope, nonPublic, name, Targets.StopAtGodot))
        {
            members.Add(
                new JsonObject
                {
                    ["kind"] = entry.Kind,
                    ["name"] = entry.Name,
                    ["signature"] = entry.Signature,
                    ["static"] = entry.Static,
                }
            );
        }
        return new JsonObject
        {
            ["ok"] = true,
            ["result"] = new JsonObject { ["type"] = type.FullName, ["members"] = members },
        };
    }
}
