namespace GodotMcp.Dotnet.Core;

/// <summary>A generic call's type arguments from their names: a C# keyword such as <c>int</c>, or a full name with its namespace.</summary>
public static class TypeArguments
{
    /// <summary>
    /// The types <paramref name="names"/> name, each a keyword or else looked up by <paramref name="find"/>, which answers
    /// every loaded type of that full name; a name that finds none, or several, throws <see cref="OverloadException"/>.
    /// </summary>
    public static IReadOnlyList<Type> Parse(IReadOnlyList<string> names, Func<string, IReadOnlyList<Type>> find)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(find);
        var types = new Type[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            string name = names[i].Trim();
            types[i] = TypeNames.FromKeyword(name) ?? One(name, i, find(name));
        }
        return types;
    }

    private static Type One(string name, int i, IReadOnlyList<Type> found) =>
        found.Count switch
        {
            1 => found[0],
            0 => throw new OverloadException(
                $"typeArgs[{i}] '{name}' names no type: give a C# keyword such as int, or the full name with its namespace"
            ),
            _ => throw new OverloadException(
                $"typeArgs[{i}] '{name}' names a type in several assemblies "
                    + $"({string.Join(", ", found.Select(type => type.Assembly.GetName().Name).Distinct())}); cs_call cannot tell them apart"
            ),
        };
}
