namespace GodotMcp.TestSupport;

/// <summary>
/// A stand-in for the godot-xaml addon, as extra CsProbe sources: a <c>[GlobalClass]</c> loader and saver for <c>.xscn</c>
/// scenes and <c>.xres</c> resources. A <c>.xscn</c> is <c>.tscn</c> text and a <c>.xres</c> <c>.tres</c> text, each after
/// any number of leading lines starting <c>"; "</c>, which a save keeps from the file it overwrites, and each saved line
/// starts <c>"| "</c>, so a file read as <c>.tscn</c> text does not parse; a load strips that prefix where a line has it, so
/// a test can write a body without it. The uid lives in a <c>&lt;path&gt;.uid</c> file the saver writes, and a scene whose root
/// is named <c>FailSave</c> fails its save with <c>Error.Failed</c>.
/// </summary>
public static class FakeXamlFormat
{
    /// <summary>What every fake-format file keeps on a save: its leading lines starting this.</summary>
    public const string CommentPrefix = "; ";

    /// <summary>What every body line of a saved fake-format file starts with.</summary>
    public const string BodyPrefix = "| ";

    private const string Loader = """
        using System.Linq;
        using Godot;

        [GlobalClass, Tool]
        public partial class FakeXamlLoader : ResourceFormatLoader
        {
            public override string[] _GetRecognizedExtensions() => ["xscn", "xres"];

            public override bool _HandlesType(StringName type) => type.ToString() == "Resource" || ClassDB.IsParentClass("PackedScene", type);

            public override string _GetResourceType(string path) =>
                path.GetExtension().ToLowerInvariant() switch { "xscn" => "PackedScene", "xres" => "Resource", _ => "" };

            public override Variant _Load(string path, string originalPath, bool useSubThreads, int cacheMode)
            {
                string[] lines = FileAccess.GetFileAsString(path).Split('\n');
                int start = 0;
                while (start < lines.Length && lines[start].StartsWith("; ", System.StringComparison.Ordinal))
                {
                    start++;
                }

                string temp = FakeXamlSaver.TempPath(path.GetExtension().ToLowerInvariant() == "xscn" ? "tscn" : "tres");
                using (FileAccess file = FileAccess.Open(temp, FileAccess.ModeFlags.Write))
                {
                    file.StoreString(
                        string.Join('\n', lines[start..].Select(line => line.StartsWith("| ", System.StringComparison.Ordinal) ? line[2..] : line))
                    );
                }

                Resource loaded = ResourceLoader.Load(temp, "", ResourceLoader.CacheMode.Ignore);
                DirAccess.RemoveAbsolute(temp);
                return loaded is null ? (int)Error.FileCorrupt : loaded;
            }
        }
        """;

    private const string Saver = """
        using System.Linq;
        using Godot;

        [GlobalClass, Tool]
        public partial class FakeXamlSaver : ResourceFormatSaver
        {
            public override bool _Recognize(Resource resource) => resource is not null and not Script;

            public override string[] _GetRecognizedExtensions(Resource resource) => resource is PackedScene ? ["xscn"] : ["xres"];

            public override Error _Save(Resource resource, string path, uint flags)
            {
                if (resource is PackedScene scene && scene.GetState().GetNodeName(0).ToString() == "FailSave")
                {
                    return Error.Failed;
                }

                string temp = TempPath(resource is PackedScene ? "tscn" : "tres");
                Error error = ResourceSaver.Save(resource, temp);
                if (error != Error.Ok)
                {
                    return error;
                }

                string saved = FileAccess.GetFileAsString(temp);
                DirAccess.RemoveAbsolute(temp);
                string kept = FileAccess.FileExists(path)
                    ? string.Concat(
                        FileAccess.GetFileAsString(path)
                            .Split('\n')
                            .TakeWhile(line => line.StartsWith("; ", System.StringComparison.Ordinal))
                            .Select(line => line + "\n")
                    )
                    : "";
                using FileAccess file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
                if (file is null)
                {
                    return FileAccess.GetOpenError();
                }

                file.StoreString(kept + string.Join('\n', saved.Split('\n').Select(line => "| " + line)));
                return Error.Ok;
            }

            public override Error _SetUid(string path, long uid)
            {
                if (path.GetExtension().ToLowerInvariant() is not ("xscn" or "xres"))
                {
                    return Error.FileUnrecognized;
                }

                using FileAccess file = FileAccess.Open(path + ".uid", FileAccess.ModeFlags.Write);
                if (file is null)
                {
                    return Error.FileCantWrite;
                }

                file.StoreLine(ResourceUid.IdToText(uid));
                return Error.Ok;
            }

            public static string TempPath(string extension)
            {
                DirAccess.MakeDirRecursiveAbsolute("user://fake_xaml");
                return $"user://fake_xaml/{System.Guid.NewGuid():N}.{extension}";
            }
        }
        """;

    /// <summary>The loader's and saver's sources, file name to content, for <see cref="CsProbeProject.BuiltWith"/>.</summary>
    public static IReadOnlyDictionary<string, string> Sources { get; } =
        new Dictionary<string, string> { ["FakeXamlLoader.cs"] = Loader, ["FakeXamlSaver.cs"] = Saver };
}
