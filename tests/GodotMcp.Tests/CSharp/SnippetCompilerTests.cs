using System.Reflection;
using System.Runtime.Loader;
using GodotMcp.Server.CSharp;
using GodotMcp.TestSupport;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using ModelContextProtocol;

namespace GodotMcp.Tests.CSharp;

public sealed class SnippetCompilerTests : IDisposable
{
    private const string GlobalsType = "Fixture.Globals";
    private const string FixtureSource = """
        namespace Fixture;

        public class Globals
        {
            protected int Answer => 42;

            protected string Echo(string s) => s;

            protected void Touch() { }
        }
        """;

    private readonly TempDirectory _temp = new();
    private readonly string _fixturePath;

    public SnippetCompilerTests()
    {
        _fixturePath = _temp.Combine("Fixture.dll");
        BuildFixture(_fixturePath);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task AnExpressionReturnsItsValue() => Assert.Equal(43, await RunAsync(CompileOk("Answer + 1")));

    [Fact]
    public async Task StatementsWithReturn() => Assert.Equal("ab", await RunAsync(CompileOk("var x = Echo(\"a\"); return x + \"b\";")));

    [Fact]
    public async Task AStatementBodyWithoutReturnGivesNull() => Assert.Null(await RunAsync(CompileOk("var x = 1;")));

    [Fact]
    public async Task AwaitWorks() => Assert.Equal(42, await RunAsync(CompileOk("await Task.Delay(1); return Answer;")));

    [Fact]
    public async Task AVoidExpressionRunsAsAStatement() => Assert.Null(await RunAsync(CompileOk("Touch()")));

    [Fact]
    public void AGarbageDllIsNotManaged()
    {
        string garbage = _temp.Combine("garbage.dll");
        File.WriteAllText(garbage, "not a portable executable");

        Assert.False(SnippetCompiler.IsManaged(garbage));
        Assert.True(SnippetCompiler.IsManaged(typeof(object).Assembly.Location));
    }

    [Fact]
    public void ACompileErrorIsOnTheSnippetsOwnLine()
    {
        SnippetCompilation compilation = Compile("var x = 1;\nreturn y;");

        Assert.Null(compilation.Assembly);
        SnippetDiagnostic error = Assert.Single(compilation.Errors);
        Assert.Equal("CS0103", error.Id);
        Assert.Equal(2, error.Line);
        Assert.Equal(8, error.Column);
    }

    [Fact]
    public void AMissingGlobalsTypeIsNamed()
    {
        SnippetCompilation compilation = SnippetCompiler.Compile(new SnippetRequest("1", "Fixture.Nope", [_fixturePath], []));

        Assert.Null(compilation.Assembly);
        SnippetDiagnostic error = Assert.Single(compilation.Errors);
        Assert.Equal("the globals type 'Fixture.Nope' is not in the references", error.Message);
        Assert.Equal(0, error.Line);
    }

    [Fact]
    public void ABadUsingIsRefused()
    {
        McpException refusal = Assert.Throws<McpException>(() => Compile("1", "System.Text", "not a namespace"));

        Assert.Equal("usings[1] 'not a namespace' is not a namespace name", refusal.Message);
    }

    [Fact]
    public async Task AUsingIsHonoured() => Assert.Equal("a", await RunAsync(CompileOk("new StringBuilder(\"a\").ToString()", "System.Text")));

    [Fact]
    public async Task TwoCompilationsLoadSideBySide()
    {
        byte[] first = CompileOk("Answer");
        byte[] second = CompileOk("Echo(\"two\")");
        AssemblyLoadContext firstContext = NewContext();
        AssemblyLoadContext secondContext = NewContext();
        try
        {
            Assembly firstAssembly = firstContext.LoadFromStream(new MemoryStream(first));
            Assembly secondAssembly = secondContext.LoadFromStream(new MemoryStream(second));

            Assert.NotEqual(firstAssembly.GetName().Name, secondAssembly.GetName().Name);
            Assert.Equal(42, await InvokeAsync(firstAssembly));
            Assert.Equal("two", await InvokeAsync(secondAssembly));
        }
        finally
        {
            firstContext.Unload();
            secondContext.Unload();
        }
    }

    private static void BuildFixture(string path)
    {
        var compilation = CSharpCompilation.Create(
            "Fixture",
            [CSharpSyntaxTree.ParseText(FixtureSource)],
            SnippetCompiler.FrameworkReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)
        );
        EmitResult result = compilation.Emit(path);
        Assert.True(result.Success, string.Join('\n', result.Diagnostics));
    }

    private SnippetCompilation Compile(string code, params string[] usings) =>
        SnippetCompiler.Compile(new SnippetRequest(code, GlobalsType, [_fixturePath], usings));

    private byte[] CompileOk(string code, params string[] usings)
    {
        SnippetCompilation compilation = Compile(code, usings);
        Assert.Empty(compilation.Errors);
        return Assert.IsType<byte[]>(compilation.Assembly);
    }

    private AssemblyLoadContext NewContext()
    {
        AssemblyLoadContext context = new("snippet-test", isCollectible: true);
        // A stream, not the path, so the context never locks the fixture dll the temp folder deletes.
        context.Resolving += (loader, name) =>
            name.Name == "Fixture" ? loader.LoadFromStream(new MemoryStream(File.ReadAllBytes(_fixturePath))) : null;
        return context;
    }

    private async Task<object?> RunAsync(byte[] assembly)
    {
        AssemblyLoadContext context = NewContext();
        try
        {
            return await InvokeAsync(context.LoadFromStream(new MemoryStream(assembly)));
        }
        finally
        {
            context.Unload();
        }
    }

    private static async Task<object?> InvokeAsync(Assembly assembly)
    {
        Type type = assembly.GetType(SnippetCompiler.ClassName, throwOnError: true)!;
        object instance = Activator.CreateInstance(type)!;
        MethodInfo run = type.GetMethod(SnippetCompiler.MethodName)!;
        return await (Task<object?>)run.Invoke(instance, null)!;
    }
}
