using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// The headless signal tools against the real Godot: get_node_signals, connect_signal and disconnect_signal on scenes the tests
/// write into InputProbe copies (never into the tracked fixtures), read back as text.
/// </summary>
public sealed class HeadlessSignalTests : IAsyncDisposable
{
    private const int TestTimeoutMs = 120_000;
    private const int BuildTestTimeoutMs = 240_000;

    private const string LevelScript =
        "extends Node2D\n\n\nfunc on_pressed() -> void:\n\tpass\n\n\nfunc on_bound(_count: int, _what: String) -> void:\n\tpass\n\n\n"
        + "func on_untyped(_count, _what) -> void:\n\tpass\n\n\nfunc on_squad(_units: Array[Node2D]) -> void:\n\tpass\n";

    // A connection a script makes itself is not persistent, so no signal tool lists or saves it.
    private const string ButtonScript = "extends Button\n\n\nfunc _init() -> void:\n\tpressed.connect(_ping)\n\n\nfunc _ping() -> void:\n\tpass\n";

    private const string EnemyScript =
        "extends CharacterBody2D\n\nsignal hit(amount: int)\n\n\nfunc on_hit(_amount: int) -> void:\n\tpass\n\n\nfunc on_shown() -> void:\n\tpass\n";

    private const string EnemyScene =
        "[gd_scene load_steps=2 format=3 uid=\"uid://bsenemy0000a\"]\n\n[ext_resource type=\"Script\" path=\"res://enemy.gd\" id=\"1\"]\n\n"
        + "[node name=\"Enemy\" type=\"CharacterBody2D\"]\nscript = ExtResource(\"1\")\n\n"
        + "[node name=\"Sprite\" type=\"Sprite2D\" parent=\".\"]\n\n[connection signal=\"hit\" from=\".\" to=\".\" method=\"on_hit\"]\n"
        + "[connection signal=\"visibility_changed\" from=\"Sprite\" to=\".\" method=\"on_shown\"]\n";

    private const string BoundScene =
        "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://level.gd\" id=\"1\"]\n\n"
        + "[node name=\"Level\" type=\"Node2D\"]\nscript = ExtResource(\"1\")\n\n[node name=\"Btn\" type=\"Button\" parent=\".\"]\n\n"
        + "[connection signal=\"pressed\" from=\"Btn\" to=\".\" method=\"on_bound\" binds= [7, \"gold\"]]\n";

    private const string CSharpBuildFailed =
        "res://main.tscn uses C# scripts and the project's C# build failed; fix it first (validate lists the errors).";

    private const string LevelScene =
        "[gd_scene load_steps=4 format=3 uid=\"uid://bslevel00000a\"]\n\n"
        + "[ext_resource type=\"Script\" path=\"res://level.gd\" id=\"1\"]\n[ext_resource type=\"Script\" path=\"res://btn.gd\" id=\"2\"]\n"
        + "[ext_resource type=\"PackedScene\" path=\"res://enemy.tscn\" id=\"3\"]\n\n"
        + "[node name=\"Level\" type=\"Node2D\"]\nscript = ExtResource(\"1\")\n\n"
        + "[node name=\"Btn\" type=\"Button\" parent=\".\"]\nscript = ExtResource(\"2\")\n\n"
        + "[node name=\"Boss\" parent=\".\" instance=ExtResource(\"3\")]\n\n"
        + "[connection signal=\"button_down\" from=\"Btn\" to=\".\" method=\"on_pressed\"]\n";

    private const string EliteScene =
        "[gd_scene load_steps=2 format=3 uid=\"uid://bselite00000a\"]\n\n[ext_resource type=\"PackedScene\" path=\"res://enemy.tscn\" id=\"1\"]\n\n"
        + "[node name=\"Elite\" instance=ExtResource(\"1\")]\n";

    private const string EditableScene =
        "[gd_scene load_steps=3 format=3]\n\n[ext_resource type=\"Script\" path=\"res://level.gd\" id=\"1\"]\n"
        + "[ext_resource type=\"PackedScene\" path=\"res://enemy.tscn\" id=\"2\"]\n\n"
        + "[node name=\"Level\" type=\"Node2D\"]\nscript = ExtResource(\"1\")\n\n"
        + "[node name=\"Boss\" parent=\".\" instance=ExtResource(\"2\")]\n\n[editable path=\"Boss\"]\n";

    private readonly SessionHarness _harness = new();
    private readonly HeadlessTools _tools;
    private readonly List<IDisposable> _projects = [];

    public HeadlessSignalTests() => _tools = new HeadlessTools(_harness.Sessions);

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        foreach (IDisposable project in _projects)
        {
            project.Dispose();
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task GetNodeSignalsListsSignalsAndPersistentConnections()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string directory = WriteScenes();

        JsonNode read = JsonNode.Parse(await _tools.GetNodeSignalsAsync(directory, "level.tscn", "Btn", null, cancellation))!;

        Assert.Equal("Btn", read["path"]!.GetValue<string>());
        Assert.Equal("Button", read["type"]!.GetValue<string>());
        Assert.Equal(
            """{"name":"button_down","args":[],"connections":[{"target":".","method":"on_pressed"}]}""",
            Signal(read, "button_down").ToJsonString()
        );
        Assert.Equal("""{"name":"pressed","args":[],"connections":[]}""", Signal(read, "pressed").ToJsonString());
        Assert.Equal("""["toggled_on"]""", Signal(read, "toggled")["args"]!.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task GetNodeSignalsMarksAnInheritedConnection()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string directory = WriteScenes();

        JsonNode read = JsonNode.Parse(await _tools.GetNodeSignalsAsync(directory, "elite.tscn", ".", null, cancellation))!;

        Assert.Equal(
            """{"name":"hit","args":["amount"],"connections":[{"target":".","method":"on_hit","inherited":true}]}""",
            Signal(read, "hit").ToJsonString()
        );
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ConnectSignalSavesAConnectionLine()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string directory = WriteScenes();

        string connected = await _tools.ConnectSignalAsync(
            directory,
            "level.tscn",
            "Btn",
            "pressed",
            new ConnectTarget(".", "on_pressed"),
            cancellation
        );

        Assert.Equal("""{"from":"Btn","signal":"pressed","target":".","method":"on_pressed"}""", connected);
        Assert.Equal(
            [
                "[connection signal=\"button_down\" from=\"Btn\" to=\".\" method=\"on_pressed\"]",
                "[connection signal=\"pressed\" from=\"Btn\" to=\".\" method=\"on_pressed\"]",
            ],
            Connections(directory, "level.tscn")
        );
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ConnectSignalWithBindsSavesThem()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string directory = WriteScenes();
        ConnectTarget target = new(".", "on_bound", [Json("7"), Json("\"gold\"")]);

        await _tools.ConnectSignalAsync(directory, "level.tscn", "Btn", "pressed", target, cancellation);

        Assert.Contains(
            "[connection signal=\"pressed\" from=\"Btn\" to=\".\" method=\"on_bound\" binds= [7, \"gold\"]]",
            Connections(directory, "level.tscn")
        );
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ConnectSignalSavesAnIntegralBindForAnUntypedParameterAsAnInt()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string directory = WriteScenes();
        ConnectTarget target = new(".", "on_untyped", [Json("7"), Json("\"gold\"")]);

        await _tools.ConnectSignalAsync(directory, "level.tscn", "Btn", "pressed", target, cancellation);

        Assert.Contains(
            "[connection signal=\"pressed\" from=\"Btn\" to=\".\" method=\"on_untyped\" binds= [7, \"gold\"]]",
            Connections(directory, "level.tscn")
        );
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ConnectSignalRefusesAnArityMismatch()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string directory = WriteScenes();
        ConnectTarget target = new(".", "on_bound", [Json("7")]);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.ConnectSignalAsync(directory, "level.tscn", "Btn", "pressed", target, cancellation)
        );

        Assert.Equal("connect_signal failed: ..on_bound takes 2 arguments; pressed passes 0 and binds 1.", refused.Message);
        Assert.Equal(LevelScene, Read(directory, "level.tscn"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ConnectSignalRefusesABindThatDoesNotConvert()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string directory = WriteScenes();
        ConnectTarget target = new(".", "on_bound", [Json("\"seven\""), Json("\"gold\"")]);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.ConnectSignalAsync(directory, "level.tscn", "Btn", "pressed", target, cancellation)
        );

        Assert.Equal("connect_signal failed: Argument 1 of 'on_bound' on '.' is int; \"seven\" does not convert to it.", refused.Message);
        Assert.Equal(LevelScene, Read(directory, "level.tscn"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ConnectSignalRefusesAnObjectArrayBindSayingWhy()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string directory = WriteScenes();
        ConnectTarget target = new(".", "on_squad", [Json("[]")]);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.ConnectSignalAsync(directory, "level.tscn", "Btn", "pressed", target, cancellation)
        );

        Assert.Equal(
            "connect_signal failed: Argument 1 of 'on_squad' on '.': arrays of Object types (here Array[Node2D]) cannot be set from JSON.",
            refused.Message
        );
        Assert.Equal(LevelScene, Read(directory, "level.tscn"));
    }

    [Fact(Timeout = BuildTestTimeoutMs)]
    public async Task ConnectSignalRefusesACSharpMethodWhileTheBuildFailed()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        CsProbeProject csProbe = BrokenCsProbe();
        string before = Read(csProbe.Directory, "main.tscn");

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.ConnectSignalAsync(csProbe.Directory, "main.tscn", ".", "ready", new ConnectTarget(".", "PlayStep"), cancellation)
        );

        // The C# script's missing class is what Godot logs while the scene loads.
        Assert.StartsWith("connect_signal failed: " + CSharpBuildFailed + "\nGodot logged:\n", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, Read(csProbe.Directory, "main.tscn"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ConnectSignalRefusesAMissingSignal()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string directory = WriteScenes();

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.ConnectSignalAsync(directory, "level.tscn", "Btn", "nope", new ConnectTarget(".", "on_pressed"), cancellation)
        );

        Assert.Equal("connect_signal failed: Btn has no signal nope.", refused.Message);
        Assert.Equal(LevelScene, Read(directory, "level.tscn"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ConnectSignalRefusesAMissingMethod()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string directory = WriteScenes();

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.ConnectSignalAsync(directory, "level.tscn", "Btn", "pressed", new ConnectTarget(".", "nope"), cancellation)
        );

        Assert.Equal("connect_signal failed: . has no method nope.", refused.Message);
        Assert.Equal(LevelScene, Read(directory, "level.tscn"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ConnectSignalRefusesAnExistingConnection()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string directory = WriteScenes();
        ConnectTarget bound = new(".", "on_pressed", [Json("1")]);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.ConnectSignalAsync(directory, "level.tscn", "Btn", "button_down", bound, cancellation)
        );

        Assert.Equal("connect_signal failed: Btn.button_down is already connected to ..on_pressed.", refused.Message);
        Assert.Equal(LevelScene, Read(directory, "level.tscn"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ConnectSignalRefusesASourceInsideAnInstance()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string directory = WriteScenes();

        McpException fromInside = await Assert.ThrowsAsync<McpException>(() =>
            _tools.ConnectSignalAsync(
                directory,
                "level.tscn",
                "Boss/Sprite",
                "visibility_changed",
                new ConnectTarget(".", "on_pressed"),
                cancellation
            )
        );

        Assert.Equal(
            "connect_signal failed: Boss/Sprite is inside the instance of res://enemy.tscn at Boss; its changes would not be saved. "
                + "Edit res://enemy.tscn instead.",
            fromInside.Message
        );
        Assert.Equal(LevelScene, Read(directory, "level.tscn"));
    }

    // The packer drops a connection whose source is inside a non-editable instance; a target there is saved by its path and
    // found again on load (4.7.2 packed_scene.cpp L1142-1143, measured).
    [Fact(Timeout = TestTimeoutMs)]
    public async Task ConnectSignalToANodeInsideAnInstanceIsSaved()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string directory = WriteScenes();

        string connected = await _tools.ConnectSignalAsync(
            directory,
            "level.tscn",
            "Btn",
            "pressed",
            new ConnectTarget("Boss/Sprite", "hide"),
            cancellation
        );
        JsonNode read = JsonNode.Parse(await _tools.GetNodeSignalsAsync(directory, "level.tscn", "Btn", null, cancellation))!;

        Assert.Equal("""{"from":"Btn","signal":"pressed","target":"Boss/Sprite","method":"hide"}""", connected);
        Assert.Contains("[connection signal=\"pressed\" from=\"Btn\" to=\"Boss/Sprite\" method=\"hide\"]", Connections(directory, "level.tscn"));
        Assert.Equal("""[{"target":"Boss/Sprite","method":"hide"}]""", Signal(read, "pressed")["connections"]!.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ConnectSignalFromAnEditableInstanceChildIsSaved()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string directory = WriteScenes();

        string connected = await _tools.ConnectSignalAsync(
            directory,
            "editable.tscn",
            "Boss/Sprite",
            "visibility_changed",
            new ConnectTarget(".", "on_pressed"),
            cancellation
        );

        Assert.Equal("""{"from":"Boss/Sprite","signal":"visibility_changed","target":".","method":"on_pressed"}""", connected);
        Assert.Equal(
            ["[connection signal=\"visibility_changed\" from=\"Boss/Sprite\" to=\".\" method=\"on_pressed\"]"],
            Connections(directory, "editable.tscn")
        );
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task DisconnectSignalRemovesTheLine()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string directory = WriteScenes();

        string disconnected = await _tools.DisconnectSignalAsync(
            directory,
            "level.tscn",
            "Btn",
            "button_down",
            new DisconnectTarget(".", "on_pressed"),
            cancellation
        );

        Assert.Equal("""{"from":"Btn","signal":"button_down","target":".","method":"on_pressed"}""", disconnected);
        Assert.Empty(Connections(directory, "level.tscn"));
        Assert.Contains("[node name=\"Btn\" ", Read(directory, "level.tscn"), StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task DisconnectSignalRefusesAnInheritedConnection()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string directory = WriteScenes();

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.DisconnectSignalAsync(directory, "elite.tscn", ".", "hit", new DisconnectTarget(".", "on_hit"), cancellation)
        );

        Assert.Equal("disconnect_signal failed: ..hit → ..on_hit comes from res://enemy.tscn; disconnect it there.", refused.Message);
        Assert.Equal(EliteScene, Read(directory, "elite.tscn"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task DisconnectSignalRefusesAMissingConnection()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string directory = WriteScenes();

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.DisconnectSignalAsync(directory, "level.tscn", "Btn", "pressed", new DisconnectTarget(".", "on_pressed"), cancellation)
        );

        Assert.Equal("disconnect_signal failed: Btn.pressed is not connected to ..on_pressed.", refused.Message);
        Assert.Equal(LevelScene, Read(directory, "level.tscn"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task DisconnectSignalRemovesABoundConnectionGivenNoBinds()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string directory = WriteScenes();

        await _tools.DisconnectSignalAsync(directory, "bound.tscn", "Btn", "pressed", new DisconnectTarget(".", "on_bound"), cancellation);

        Assert.Empty(Connections(directory, "bound.tscn"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task DisconnectSignalNamesTheInstancedSceneAnInheritedConnectionComesFrom()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string directory = WriteScenes();

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.DisconnectSignalAsync(
                directory,
                "level.tscn",
                "Boss/Sprite",
                "visibility_changed",
                new DisconnectTarget("Boss", "on_shown"),
                cancellation
            )
        );

        Assert.Equal(
            "disconnect_signal failed: Boss/Sprite.visibility_changed → Boss.on_shown comes from res://enemy.tscn; disconnect it there.",
            refused.Message
        );
        Assert.Equal(LevelScene, Read(directory, "level.tscn"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task GetNodeSignalsListsABoundConnectionWithItsBinds()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string directory = WriteScenes();

        JsonNode read = JsonNode.Parse(await _tools.GetNodeSignalsAsync(directory, "bound.tscn", "Btn", null, cancellation))!;

        Assert.Equal("""[{"target":".","method":"on_bound","binds":[7,"gold"]}]""", Signal(read, "pressed")["connections"]!.ToJsonString());
    }

    [Fact(Timeout = BuildTestTimeoutMs)]
    public async Task GetNodeSignalsWarnsThatAFailedBuildLeavesOutCSharpSignals()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        CsProbeProject csProbe = BrokenCsProbe();

        JsonNode read = JsonNode.Parse(await _tools.GetNodeSignalsAsync(csProbe.Directory, "main.tscn", ".", null, cancellation))!;

        Assert.Equal(".'s C# script is not built (the build failed), so its script signals are missing.", read["warning"]!.GetValue<string>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task GetNodeSignalsWarnsThatASkippedBuildMayLeaveOutCSharpSignals()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        CsProbeProject csProbe = Track(CsProbeProject.Unbuilt());

        JsonNode read = JsonNode.Parse(
            await _tools.GetNodeSignalsAsync(csProbe.Directory, "main.tscn", ".", new HeadlessOptions("never"), cancellation)
        )!;

        Assert.Equal(
            ".'s C# script may not be built (prepare never skips the build), so its script signals may be missing.",
            read["warning"]!.GetValue<string>()
        );
    }

    [Fact(Timeout = BuildTestTimeoutMs)]
    public async Task GetNodeSignalsListsACSharpScriptSignalWhenBuilt()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        CsProbeProject csProbe = Track(CsProbeProject.Unbuilt());
        string source = File.ReadAllText(csProbe.SourcePath("CsProbeNode.cs"));
        const string Declaration = "[Signal]\n    public delegate void ScoredEventHandler(int points);\n\n    public int PlayStep";
        csProbe.WriteSource("CsProbeNode.cs", source.Replace("public int PlayStep", Declaration, StringComparison.Ordinal));

        JsonNode read = JsonNode.Parse(await _tools.GetNodeSignalsAsync(csProbe.Directory, "main.tscn", ".", null, cancellation))!;

        Assert.Equal("""{"name":"Scored","args":["points"],"connections":[]}""", Signal(read, "Scored").ToJsonString());
        Assert.Null(read["warning"]);
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private T Track<T>(T project)
        where T : IDisposable
    {
        _projects.Add(project);
        return project;
    }

    /// <summary>An unbuilt CsProbe copy whose C# source does not compile, so the prep's build fails.</summary>
    private CsProbeProject BrokenCsProbe()
    {
        CsProbeProject csProbe = Track(CsProbeProject.Unbuilt());
        string source = File.ReadAllText(csProbe.SourcePath("CsProbeNode.cs"));
        csProbe.WriteSource("CsProbeNode.cs", source.Replace("\"hidden\";", "\"hidden\"", StringComparison.Ordinal));
        return csProbe;
    }

    private static JsonNode Signal(JsonNode read, string name) =>
        Assert.Single(read["signals"]!.AsArray(), entry => entry!["name"]!.GetValue<string>() == name)!;

    private static string Read(string directory, string relative) => File.ReadAllText(Path.Combine(directory, relative));

    private static string[] Connections(string directory, string scene) =>
        [.. File.ReadAllLines(Path.Combine(directory, scene)).Where(line => line.StartsWith("[connection ", StringComparison.Ordinal))];

    /// <summary>A new InputProbe copy holding the tests' scenes and scripts; its folder.</summary>
    private string WriteScenes()
    {
        ProbeProject probe = new();
        _projects.Add(probe);
        string directory = probe.Directory;
        File.WriteAllText(Path.Combine(directory, "level.gd"), LevelScript);
        File.WriteAllText(Path.Combine(directory, "btn.gd"), ButtonScript);
        File.WriteAllText(Path.Combine(directory, "enemy.gd"), EnemyScript);
        File.WriteAllText(Path.Combine(directory, "enemy.tscn"), EnemyScene);
        File.WriteAllText(Path.Combine(directory, "level.tscn"), LevelScene);
        File.WriteAllText(Path.Combine(directory, "elite.tscn"), EliteScene);
        File.WriteAllText(Path.Combine(directory, "editable.tscn"), EditableScene);
        File.WriteAllText(Path.Combine(directory, "bound.tscn"), BoundScene);
        return directory;
    }
}
