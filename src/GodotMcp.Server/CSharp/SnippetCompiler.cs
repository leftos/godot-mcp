using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
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
/// in the request. Only errors are reported, warnings are dropped. The snippet reaches the internal members of every reference
/// that is not the framework's, as if it were inside those assemblies; private members stay out of its reach. A framework
/// member or type that is below public is out of its reach too, and refusing it as CS0122 is this compiler's own work: the
/// runtime, which the snippet's assembly asks to skip the checks of every other reference, would refuse the call instead.
/// </summary>
internal static class SnippetCompiler
{
    public const string ClassName = "GodotMcpSnippet";
    public const string MethodName = "RunAsync";

    private const string SnippetPath = "snippet";
    private const string WrapperPath = "GodotMcpSnippet.g.cs";

    private static readonly string[] DefaultUsings = ["System", "System.Linq", "System.Collections.Generic", "System.Threading.Tasks"];
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    /// <summary>The hint a CS0122 error carries: the globals' members reach what C#'s access checks refuse.</summary>
    private const string AccessHint = "; reach it through Get, Set or Call instead, which take private and internal members by name";

    // Internal members are imported so a snippet can name them; private ones never are, so they stay out of reach.
    private static readonly CSharpCompilationOptions CheckedOptions = new CSharpCompilationOptions(
        OutputKind.DynamicallyLinkedLibrary,
        optimizationLevel: OptimizationLevel.Release,
        nullableContextOptions: NullableContextOptions.Enable,
        allowUnsafe: false
    ).WithMetadataImportOptions(MetadataImportOptions.Internal);

    private static readonly CSharpCompilationOptions? UncheckedOptions = WithoutAccessChecks(CheckedOptions);
    private static readonly ConcurrentDictionary<string, ImmutableArray<MetadataReference>> Frameworks = new(StringComparer.OrdinalIgnoreCase);
    private static int _compiled;

    /// <summary>What the wrapper names besides the snippet: the globals class it derives from and the assemblies whose checks it skips.</summary>
    private sealed record Wrapper(string BaseType, string[] Trusted);

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
    public static SnippetCompilation Compile(SnippetRequest request) => Compile(request, ignoreAccessChecks: true);

    /// <summary>
    /// Compiles <paramref name="request"/>, without C#'s access checks when <paramref name="ignoreAccessChecks"/> is set and
    /// Roslyn lets them be turned off; the compiled assembly is marked to skip the runtime's checks on every reference that is
    /// not the framework's, and a snippet that names a framework member below public is refused before it is emitted.
    /// </summary>
    /// <exception cref="McpException">A using is not a namespace name, or a reference cannot be read.</exception>
    /// <exception cref="IOException">The framework folder cannot be read.</exception>
    internal static SnippetCompilation Compile(SnippetRequest request, bool ignoreAccessChecks)
    {
        ValidateUsings(request.Usings);
        string assemblyName = $"{ClassName}_{Interlocked.Increment(ref _compiled).ToString(CultureInfo.InvariantCulture)}";
        MetadataReference[] own = [.. request.References.Select(ReadReference)];
        ImmutableArray<MetadataReference> framework = FrameworkIn(request.FrameworkDirectory);
        CSharpCompilationOptions? withoutChecks = ignoreAccessChecks ? UncheckedOptions : null;
        var references = CSharpCompilation.Create(assemblyName, references: [.. framework, .. own], options: withoutChecks ?? CheckedOptions);
        INamedTypeSymbol? globals = references.GetTypeByMetadataName(request.GlobalsType);
        if (globals is null)
        {
            return new SnippetCompilation(
                null,
                null,
                [new SnippetDiagnostic(0, 0, "", $"the globals type '{request.GlobalsType}' is not in the references")]
            );
        }

        Wrapper wrapper = new(globals.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), AssemblyNames(references, own));
        Shape shape = IsExpression(request.Code) ? Shape.Expression : Shape.Statements;
        CSharpCompilation compilation = WithSource(references, request, wrapper, shape);
        if (shape == Shape.Expression && IsVoid(compilation))
        {
            compilation = WithSource(references, request, wrapper, Shape.VoidExpression);
        }

        return withoutChecks is null ? Emit(compilation) : RefuseOrEmit(compilation, framework);
    }

    /// <summary>
    /// <paramref name="options"/> with Roslyn's internal <c>BinderFlags.IgnoreAccessibility</c> set through its internal
    /// <c>WithTopLevelBinderFlags</c>, as Roslyn's own expression evaluator compiles (<c>CompilationExtensions.cs</c> in
    /// dotnet/roslyn's <c>src/ExpressionEvaluator/CSharp/Source/ExpressionCompiler</c>); null, logged once to stderr, when this
    /// Roslyn has neither, so snippets keep C#'s access checks.
    /// </summary>
    private static CSharpCompilationOptions? WithoutAccessChecks(CSharpCompilationOptions options)
    {
        Type? flags = typeof(CSharpCompilationOptions).Assembly.GetType("Microsoft.CodeAnalysis.CSharp.BinderFlags");
        MethodInfo? with = flags is null
            ? null
            : typeof(CSharpCompilationOptions).GetMethod("WithTopLevelBinderFlags", BindingFlags.NonPublic | BindingFlags.Instance, [flags]);
        if (with is null || !Enum.TryParse(flags!, "IgnoreAccessibility", out object? ignore))
        {
            Console.Error.WriteLine(
                "godot-mcp: this Roslyn has no BinderFlags.IgnoreAccessibility to set, so run_csharp snippets keep C#'s access checks."
            );
            return null;
        }

        return (CSharpCompilationOptions)with.Invoke(options, [ignore])!;
    }

    /// <summary>The simple name of every assembly <paramref name="own"/> references, which the snippet's runtime access checks skip.</summary>
    private static string[] AssemblyNames(CSharpCompilation compilation, MetadataReference[] own) =>
        [
            .. own.Select(compilation.GetAssemblyOrModuleSymbol)
                .OfType<IAssemblySymbol>()
                .Select(assembly => assembly.Identity.Name)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];

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

    private static CSharpCompilation WithSource(CSharpCompilation references, SnippetRequest request, Wrapper wrapper, Shape shape)
    {
        StringBuilder source = new();
        foreach (string name in DefaultUsings.Concat(request.Usings))
        {
            source.Append("using ").Append(name).Append(";\n");
        }

        // The runtime (CoreCLR's Assembly::IgnoresAccessChecksTo) then lets the snippet's IL reach these assemblies' internals.
        foreach (string name in wrapper.Trusted)
        {
            source.Append("[assembly: global::System.Runtime.CompilerServices.IgnoresAccessChecksTo(");
            source.Append(SymbolDisplay.FormatLiteral(name, quote: true)).Append(")]\n");
        }

        source.Append("public sealed class ").Append(ClassName).Append(" : ").Append(wrapper.BaseType).Append("\n{\n");
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
        // The BCL does not declare the attribute; the runtime recognises it by name wherever it is declared.
        source.Append(
            "namespace System.Runtime.CompilerServices\n{\n"
                + "[global::System.AttributeUsage(global::System.AttributeTargets.Assembly, AllowMultiple = true)]\n"
                + "internal sealed class IgnoresAccessChecksToAttribute(string assemblyName) : global::System.Attribute\n{\n"
                + "public string AssemblyName { get; } = assemblyName;\n}\n}\n"
        );
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
        string message = diagnostic.GetMessage(CultureInfo.InvariantCulture) + (diagnostic.Id == "CS0122" ? AccessHint : "");
        FileLinePositionSpan span = diagnostic.Location.GetMappedLineSpan();
        return InSnippet(span)
            ? new SnippetDiagnostic(span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1, diagnostic.Id, message)
            : new SnippetDiagnostic(0, 0, diagnostic.Id, message);
    }

    /// <summary>Whether a span is text the snippet wrote itself: it maps through the <c>#line</c> directive to the snippet's file.</summary>
    private static bool InSnippet(FileLinePositionSpan span) => span.IsValid && span.Path == SnippetPath;

    /// <summary>
    /// Emits <paramref name="compilation"/>, or refuses it when the snippet names a member or type of a framework assembly
    /// that is below public. The wrapper asks the runtime to skip the access checks of every reference that is not the
    /// framework's, so the framework's own checks are the ones a snippet still hits, at run time in the game.
    /// </summary>
    private static SnippetCompilation RefuseOrEmit(CSharpCompilation compilation, ImmutableArray<MetadataReference> framework)
    {
        // A snippet that does not compile at all has nothing to say about access; Emit reports it as it always did.
        if (compilation.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            return Emit(compilation);
        }

        List<SnippetDiagnostic> refusals = Refusals(compilation, framework);
        return refusals.Count == 0 ? Emit(compilation) : new SnippetCompilation(null, null, refusals);
    }

    /// <summary>One refusal for each symbol the snippet itself names that is below public in one of the framework's assemblies.</summary>
    private static List<SnippetDiagnostic> Refusals(CSharpCompilation compilation, ImmutableArray<MetadataReference> framework)
    {
        HashSet<AssemblyIdentity> assemblies = FrameworkIdentities(compilation, framework);
        if (assemblies.Count == 0)
        {
            return [];
        }

        SemanticModel model = compilation.GetSemanticModel(compilation.SyntaxTrees.Single());
        HashSet<ISymbol> named = new(SymbolEqualityComparer.Default);
        List<SnippetDiagnostic> refusals = [];
        foreach (SyntaxNode node in model.SyntaxTree.GetRoot().DescendantNodes())
        {
            if (Refusal(node, model, assemblies, named) is { } refusal)
            {
                refusals.Add(refusal);
            }
        }

        return refusals;
    }

    /// <summary>
    /// The refusal <paramref name="node"/> earns, or null when it lies in the wrapper, names nothing out of the snippet's
    /// reach, or names a symbol <paramref name="named"/> has already refused: one refusal per symbol is one thing to fix.
    /// </summary>
    private static SnippetDiagnostic? Refusal(SyntaxNode node, SemanticModel model, HashSet<AssemblyIdentity> assemblies, HashSet<ISymbol> named)
    {
        FileLinePositionSpan span = node.GetLocation().GetMappedLineSpan();
        if (!InSnippet(span))
        {
            return null;
        }

        ISymbol? symbol = BelowPublic(model.GetSymbolInfo(node).Symbol, assemblies);
        if (symbol is null || !named.Add(symbol))
        {
            return null;
        }

        return new SnippetDiagnostic(
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1,
            "CS0122",
            $"'{symbol.ToDisplayString()}' is inaccessible due to its protection level" + AccessHint
        );
    }

    /// <summary>The symbol <paramref name="symbol"/> names, when it is below public in one of <paramref name="assemblies"/>.</summary>
    private static ISymbol? BelowPublic(ISymbol? symbol, HashSet<AssemblyIdentity> assemblies) =>
        Referenced(symbol) is { ContainingAssembly: { } assembly } referenced
        && assemblies.Contains(assembly.Identity)
        && !IsPublicThrough(referenced)
            ? referenced
            : null;

    // What a node names: a reduced extension method and a constructed generic are the declarations behind them, whose own
    // accessibility is the one that decides.
    private static ISymbol? Referenced(ISymbol? symbol) =>
        symbol switch
        {
            IMethodSymbol method => (method.ReducedFrom ?? method).OriginalDefinition,
            INamedTypeSymbol type => type.OriginalDefinition,
            _ => symbol,
        };

    // C#'s own reach: public, with every containing type public too. A snippet declares no type, so a protected member is no
    // more reachable to it than a private one, whatever the derived type the wrapper gives it.
    private static bool IsPublicThrough(ISymbol symbol)
    {
        for (INamedTypeSymbol? type = symbol.ContainingType; type is not null; type = type.ContainingType)
        {
            if (type.DeclaredAccessibility != Accessibility.Public)
            {
                return false;
            }
        }

        return symbol.DeclaredAccessibility == Accessibility.Public;
    }

    /// <summary>The identity of every assembly the framework's <paramref name="framework"/> references, to compare a symbol's own with.</summary>
    private static HashSet<AssemblyIdentity> FrameworkIdentities(CSharpCompilation compilation, ImmutableArray<MetadataReference> framework) =>
        [.. framework.Select(compilation.GetAssemblyOrModuleSymbol).OfType<IAssemblySymbol>().Select(assembly => assembly.Identity)];

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
