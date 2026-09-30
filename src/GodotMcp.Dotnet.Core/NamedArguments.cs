using System.Reflection;
using System.Text.Json.Nodes;

namespace GodotMcp.Dotnet.Core;

/// <summary>Binds a JSON object keyed by parameter name to the arguments of one method a game marked as a tool.</summary>
public static class NamedArguments
{
    /// <summary>
    /// <paramref name="method"/> with <paramref name="args"/> bound to its parameter positions: each key names a
    /// parameter, each value is converted by <see cref="ValueReader"/>, and an omitted parameter takes its own default,
    /// null when it is nullable, or an empty array when it is <c>params</c>. A method no game tool can be built from, an
    /// unknown or missing name, and a value that does not convert all throw <see cref="OverloadException"/>, and no game
    /// code runs before one: every key is checked, then every required parameter, and only then are the values
    /// converted. A <see cref="CancellationToken"/> parameter is out of the binding as it is out of the schema: no key
    /// names it, and it always takes <see cref="CancellationToken.None"/>.
    /// </summary>
    public static OverloadChoice Bind(MethodInfo method, JsonObject args, IValueResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(resolver);
        if (Passability.Unavailable(method) is { } unavailable)
        {
            throw new OverloadException(unavailable);
        }
        ParameterInfo[] parameters = method.GetParameters();
        ParameterInfo[] named = [.. parameters.Where(parameter => parameter.ParameterType != typeof(CancellationToken))];
        foreach (string key in args.Select(pair => pair.Key))
        {
            if (named.All(parameter => parameter.Name != key))
            {
                throw new OverloadException(Unknown(method.Name, named, key));
            }
        }
        foreach (ParameterInfo parameter in named)
        {
            if (Passability.Required(parameter) && !args.ContainsKey(parameter.Name!))
            {
                throw new OverloadException(
                    $"{method.Name} needs '{parameter.Name}' ({Passability.Spelling(parameter)}); list_game_tools shows its schema."
                );
            }
        }
        return new OverloadChoice(method, Values(parameters, args, resolver));
    }

    private static object?[] Values(ParameterInfo[] parameters, JsonObject args, IValueResolver resolver)
    {
        object?[] values = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            values[i] = Value(parameters[i], args, resolver);
        }
        return values;
    }

    private static object? Value(ParameterInfo parameter, JsonObject args, IValueResolver resolver)
    {
        if (parameter.ParameterType == typeof(CancellationToken))
        {
            return CancellationToken.None;
        }
        return args.TryGetPropertyValue(parameter.Name!, out JsonNode? node) ? Read(parameter, node, resolver) : Omitted(parameter);
    }

    private static object? Read(ParameterInfo parameter, JsonNode? node, IValueResolver resolver)
    {
        try
        {
            return ValueReader.Read(node, Defaults.ValueType(parameter), resolver);
        }
        catch (ValueConversionException e)
        {
            throw new OverloadException($"parameter '{parameter.Name}': {e.Message}", e);
        }
        catch (HandleException e)
        {
            throw new OverloadException($"parameter '{parameter.Name}': {e.Message}", e);
        }
    }

    private static object? Omitted(ParameterInfo parameter) =>
        Passability.IsParams(parameter) ? Array.CreateInstance(parameter.ParameterType.GetElementType()!, 0) : Defaults.Of(parameter);

    private static string Unknown(string method, ParameterInfo[] named, string given)
    {
        string takes = named.Length == 0 ? "none" : string.Join(", ", named.Select(parameter => parameter.Name));
        string[] similar =
        [
            .. named
                .Where(parameter => string.Equals(parameter.Name, given, StringComparison.OrdinalIgnoreCase))
                .Select(parameter => $"'{parameter.Name}'"),
        ];
        string hint = similar.Length == 0 ? "" : $" Did you mean {string.Join(" or ", similar)}?";
        return $"{method} has no parameter '{given}'; it takes {takes}.{hint}";
    }
}
