using System.Reflection;
using System.Text.Json.Nodes;

namespace GodotMcp.Dotnet.Core;

/// <summary>A method (closed over its type arguments when generic) and its arguments, converted and defaulted.</summary>
public sealed record OverloadChoice(MethodBase Method, IReadOnlyList<object?> Arguments);

/// <summary>
/// Picks the one overload that takes a call's JSON arguments: by argument count, then the signature asked for, then the
/// type arguments a generic needs, then which candidates every argument converts to.
/// </summary>
public static class OverloadResolver
{
    public static OverloadChoice Choose(
        IReadOnlyList<MethodBase> candidates,
        JsonArray args,
        IReadOnlyList<string>? signature,
        IReadOnlyList<Type>? typeArgs,
        IValueResolver resolver
    )
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(resolver);
        if (candidates.Count == 0)
        {
            throw new ArgumentException("There is no candidate to choose from.", nameof(candidates));
        }
        List<Attempt> attempts = [.. candidates.Select(candidate => Try(candidate, args, signature, typeArgs, resolver))];
        List<OverloadChoice> fits = [.. attempts.Select(attempt => attempt.Choice).OfType<OverloadChoice>()];
        string name = candidates[0].Name;
        return fits.Count switch
        {
            1 => fits[0],
            0 => throw new OverloadException(
                $"no overload of '{name}' takes these arguments:"
                    + string.Concat(attempts.Select(a => $"\n  {Signatures.Format(a.Method)}: {a.Failure}"))
            ),
            _ => throw new OverloadException(
                $"{fits.Count} overloads of '{name}' take these arguments; pass options.signature:"
                    + string.Concat(fits.Select(fit => $"\n  {Signatures.Format(fit.Method)}"))
            ),
        };
    }

    private static Attempt Try(
        MethodBase candidate,
        JsonArray args,
        IReadOnlyList<string>? signature,
        IReadOnlyList<Type>? typeArgs,
        IValueResolver resolver
    )
    {
        string? failure = CountFailure(candidate.GetParameters(), args.Count);
        if (failure is not null)
        {
            return new Attempt(candidate, null, failure);
        }
        (MethodBase method, failure) = Close(candidate, typeArgs ?? []);
        if (failure is not null)
        {
            return new Attempt(method, null, failure);
        }
        if (signature is not null && !Matches(method, signature))
        {
            return new Attempt(method, null, $"its parameters are not ({string.Join(", ", signature)})");
        }
        return ConvertArguments(method, args, resolver);
    }

    private static string? CountFailure(ParameterInfo[] parameters, int given)
    {
        int required = parameters.Count(parameter => !parameter.IsOptional && !parameter.HasDefaultValue && !IsParams(parameter));
        if (given >= required && given <= parameters.Length)
        {
            return null;
        }
        string range = required == parameters.Length ? $"{required}" : $"{required} to {parameters.Length}";
        return $"takes {range} argument{(parameters.Length == 1 && required == 1 ? "" : "s")}, got {given}";
    }

    private static (MethodBase Method, string? Failure) Close(MethodBase candidate, IReadOnlyList<Type> typeArgs)
    {
        if (!candidate.IsGenericMethodDefinition)
        {
            return typeArgs.Count == 0 ? (candidate, null) : (candidate, $"takes no type arguments, got {typeArgs.Count}");
        }
        Type[] parameters = candidate.GetGenericArguments();
        if (typeArgs.Count != parameters.Length)
        {
            string display = $"{candidate.Name}<{string.Join(", ", parameters.Select(parameter => parameter.Name))}>";
            string plural = parameters.Length == 1 ? "" : "s";
            return (candidate, $"{display} needs {parameters.Length} type argument{plural}; pass options.typeArgs");
        }
        try
        {
            return (((MethodInfo)candidate).MakeGenericMethod([.. typeArgs]), null);
        }
        catch (ArgumentException e)
        {
            return (candidate, $"the type arguments do not fit its constraints: {e.Message}");
        }
    }

    private static bool Matches(MethodBase method, IReadOnlyList<string> signature)
    {
        NullabilityInfoContext context = new();
        ParameterInfo[] parameters = method.GetParameters();
        return parameters.Length == signature.Count
            && parameters
                .Select((parameter, i) => Squeeze(Signatures.ParameterType(parameter, context)) == Squeeze(signature[i]))
                .All(match => match);
    }

    private static string Squeeze(string spelling) => string.Concat(spelling.Where(c => !char.IsWhiteSpace(c)));

    private static Attempt ConvertArguments(MethodBase method, JsonArray args, IValueResolver resolver)
    {
        ParameterInfo[] parameters = method.GetParameters();
        object?[] values = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            try
            {
                values[i] = i < args.Count ? ValueReader.Read(args[i], Defaults.ValueType(parameters[i]), resolver) : Omitted(parameters[i]);
            }
            catch (ValueConversionException e)
            {
                return new Attempt(method, null, $"parameter '{parameters[i].Name}': {e.Message}");
            }
        }
        return new Attempt(method, new OverloadChoice(method, values), null);
    }

    private static object? Omitted(ParameterInfo parameter) =>
        IsParams(parameter) ? Array.CreateInstance(parameter.ParameterType.GetElementType()!, 0) : Defaults.Of(parameter);

    private static bool IsParams(ParameterInfo parameter) => parameter.IsDefined(typeof(ParamArrayAttribute), inherit: false);

    private sealed record Attempt(MethodBase Method, OverloadChoice? Choice, string? Failure);
}
