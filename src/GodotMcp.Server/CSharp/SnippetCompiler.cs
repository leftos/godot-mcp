using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection.PortableExecutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using ModelContextProtocol;

namespace GodotMcp.Server.CSharp;

/// <summary>
/// A snippet to compile: a method body, the class its wrapper derives from, the folder of the runtime it will run on (whose
/// managed dlls are its framework), the other dlls it may use and extra namespaces.
/// </summary>
internal sealed record SnippetRequest(
    string Code,
    string GlobalsType,
    string FrameworkDirectory,
    IReadOnlyList<string> References,
    IReadOnlyList<string> Usings
);

/// <summary>The compiled snippet's assembly and its portable PDB, or both null with the errors that stopped it.</summary>
internal sealed record SnippetCompilation(byte[]? Assembly, byte[]? Pdb, IReadOnlyList<SnippetDiagnostic> Errors);

/// <summary>A compile error; line and column are 1-based in the snippet's own text, or 0 when it lies outside the snippet.</summary>
internal sealed record SnippetDiagnostic(int Line, int Column, string Id, string Message);

/// <summary>
/// Compiles a snippet (statements, or one expression) into an assembly holding <c>public sealed class GodotMcpSnippet</c>,
/// which derives from the globals type and runs the snippet as <c>public async Task&lt;object?&gt; RunAsync()</c>. An expression's
/// value is returned; a statement body that runs off its end returns null. The BCL comes from the request's framework folder
/// (the runtime the snippet will run on, so an overload the runtime lacks is never bound); every other reference is a dll path
/// in the request. Only errors are reported, warnings are dropped.
/// </summary>
internal static class SnippetCompiler
{
    public const string ClassName = "GodotMcpSnippet";
    public const string MethodName = "RunAsync";

    private const string SnippetPath = "snippet";
    private const string WrapperPath = "GodotMcpSnippet.g.cs";

    private static readonly string[] DefaultUsings = ["System", "System.Linq", "System.Collections.Generic", "System.Threading.Tasks"];
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);
    private static readonly CSharpCompilationOptions Options = new(
        OutputKind.DynamicallyLinkedLibrary,
        optimizationLevel: OptimizationLevel.Release,
        nullableContextOptions: NullableContextOptions.Enable,
        allowUnsafe: false
    );
    private static readonly ConcurrentDictionary<string, ImmutableArray<MetadataReference>> Frameworks = new(StringComparer.OrdinalIgnoreCase);
    private static int _compiled;

    private enum Shape
    {
        Statements,
        Expression,
        VoidExpression,
    }

    /// <summary>Every managed dll of the runtime folder <paramref name="directory"/>, in ordinal order, read once per folder.</summary>
    /// <exception cref="IOException">The folder or one of its dlls cannot be read.</exception>
    internal static ImmutableArray<MetadataReference> FrameworkIn(string directory) =>
        Frameworks.GetOrAdd(Path.GetFullPath(directory), LoadFramework);

    /// <summary>Compiles <paramref name="request"/>; a using that is not a dotted name is refused before compiling.</summary>
    /// <exception cref="McpException">A using is not a namespace name, or a reference cannot be read.</exception>
    /// <exception cref="IOException">The framework folder cannot be read.</exception>
    public static SnippetCompilation Compile(SnippetRequest request)
    {
        ValidateUsings(request.Usings);
        string assemblyName = $"{ClassName}_{Interlocked.Increment(ref _compiled).ToString(CultureInfo.InvariantCulture)}";
        var references = CSharpCompilation.Create(
            assemblyName,
            references: [.. FrameworkIn(request.FrameworkDirectory), .. request.References.Select(ReadReference)],
            options: Options
        );
        INamedTypeSymbol? globals = references.GetTypeByMetadataName(request.GlobalsType);
        if (globals is null)
        {
            return new SnippetCompilation(
                null,
                null,
                [new SnippetDiagnostic(0, 0, "", $"the globals type '{request.GlobalsType}' is not in the references")]
            );
        }

        string baseType = globals.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        Shape shape = IsExpression(request.Code) ? Shape.Expression : Shape.Statements;
        CSharpCompilation compilation = WithSource(references, request, baseType, shape);
        if (shape == Shape.Expression && IsVoid(compilation))
        {
            compilation = WithSource(references, request, baseType, Shape.VoidExpression);
        }

        return Emit(compilation);
    }

    private static void ValidateUsings(IReadOnlyList<string> usings)
    {
        for (int i = 0; i < usings.Count; i++)
        {
            if (!IsNamespaceName(usings[i]))
            {
                throw new McpException($"usings[{i}] '{usings[i]}' is not a namespace name");
            }
        }
    }

    /// <summary>Whether <paramref name="text"/> is a dotted name a using directive takes: identifiers, none of them a keyword.</summary>
    internal static bool IsNamespaceName(string text) =>
        text.Split('.').All(part => SyntaxFacts.IsValidIdentifier(part) && SyntaxFacts.GetKeywordKind(part) == SyntaxKind.None);

    private static MetadataReference ReadReference(string path)
    {
        try
        {
            return MetadataReference.CreateFromFile(path);
        }
        catch (IOException exception)
        {
            throw new McpException($"the reference '{path}' cannot be read: {exception.Message}", exception);
        }
    }

    // The whole text must parse as one expression; anything else (a statement, a block, trailing tokens) is a body.
    private static bool IsExpression(string code) =>
        !SyntaxFactory.ParseExpression(code, options: ParseOptions, consumeFullText: true).ContainsDiagnostics;

    // A void expression (a call to a void method, an awaited Task) cannot be returned, so it runs as a statement instead.
    private static bool IsVoid(CSharpCompilation compilation)
    {
        SyntaxTree tree = compilation.SyntaxTrees[0];
        MethodDeclarationSyntax method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().First();
        return method.Body?.Statements.FirstOrDefault()
                is ReturnStatementSyntax { Expression: CastExpressionSyntax { Expression: ParenthesizedExpressionSyntax snippet } }
            && compilation.GetSemanticModel(tree).GetTypeInfo(snippet.Expression).Type?.SpecialType == SpecialType.System_Void;
    }

    private static CSharpCompilation WithSource(CSharpCompilation references, SnippetRequest request, string baseType, Shape shape)
    {
        StringBuilder source = new();
        foreach (string name in DefaultUsings.Concat(request.Usings))
        {
            source.Append("using ").Append(name).Append(";\n");
        }

        source.Append("public sealed class ").Append(ClassName).Append(" : ").Append(baseType).Append("\n{\n");
        source.Append("public async global::System.Threading.Tasks.Task<object?> ").Append(MethodName).Append("()\n{\n");
        source.Append(shape == Shape.Expression ? "return (object?)(\n" : "");
        source.Append("#line 1 \"").Append(SnippetPath).Append("\"\n").Append(request.Code).Append("\n#line default\n");
        source.Append(
            shape switch
            {
                Shape.Expression => ");\n",
                Shape.VoidExpression => ";\nreturn null;\n",
                // The appended return is unreachable when the body ends in its own return; the warning is not the snippet's.
                _ => "#pragma warning disable CS0162\nreturn null;\n",
            }
        );
        source.Append("}\n}\n");
        // The PDB records a checksum of the source, which needs the text's encoding.
        return references.AddSyntaxTrees(CSharpSyntaxTree.ParseText(source.ToString(), ParseOptions, WrapperPath, Encoding.UTF8));
    }

    // The portable PDB travels with the bytes, so a stack the snippet throws names the snippet's own lines.
    private static SnippetCompilation Emit(CSharpCompilation compilation)
    {
        using MemoryStream stream = new();
        using MemoryStream pdb = new();
        EmitResult result = compilation.Emit(stream, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
        return result.Success
            ? new SnippetCompilation(stream.ToArray(), pdb.ToArray(), [])
            : new SnippetCompilation(
                null,
                null,
                [.. result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(ToSnippetDiagnostic)]
            );
    }

    // A diagnostic under the snippet's #line mapping is placed in the snippet's text; one in the wrapper has line 0.
    private static SnippetDiagnostic ToSnippetDiagnostic(Diagnostic diagnostic)
    {
        string message = diagnostic.GetMessage(CultureInfo.InvariantCulture);
        FileLinePositionSpan span = diagnostic.Location.GetMappedLineSpan();
        return span.IsValid && span.Path == SnippetPath
            ? new SnippetDiagnostic(span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1, diagnostic.Id, message)
            : new SnippetDiagnostic(0, 0, diagnostic.Id, message);
    }

    private static ImmutableArray<MetadataReference> LoadFramework(string directory) =>
        [
            .. Directory
                .EnumerateFiles(directory, "*.dll")
                .Where(IsManaged)
                .Order(StringComparer.Ordinal)
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)),
        ];

    // The framework folder also holds native dlls (coreclr, clrjit, hostpolicy); only those with metadata are references.
    // A file that is not a PE image at all is not a reference either, so it is skipped rather than failing every compile.
    internal static bool IsManaged(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using PEReader reader = new(stream);
        try
        {
            return reader.HasMetadata;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }
}
