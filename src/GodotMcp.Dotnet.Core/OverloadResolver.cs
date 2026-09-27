using System.Reflection;
using System.Text.Json.Nodes;

namespace GodotMcp.Dotnet.Core;

/// <summary>
/// A method (closed over its type arguments when generic) and its arguments, converted and defaulted; an <c>out</c>
/// parameter's argument is null.
/// </summary>
public sealed record OverloadChoice(MethodBase Method, IReadOnlyList<object?> Arguments)
{
    /// <summary>Its <c>ref</c> and <c>out</c> parameters, whose values a call writes back into the argument array it was given.</summary>
    public IReadOnlyList<ParameterInfo> WrittenBack { get; } =
    [.. Method.GetParameters().Where(parameter => parameter.ParameterType.IsByRef && !parameter.IsIn)];
}

/// <summary>
/// Picks the one overload that takes a call's JSON arguments: by argument count, then the signature asked for, then the
/// type arguments a generic needs, then which candidates every argument converts to. A candidate with a pointer or
/// by-ref-like parameter, or a by-ref-like return, never fits; an <c>out</c> parameter takes a placeholder whose value is
/// ignored; a <c>ref</c> or <c>in</c> parameter converts its argument as a plain one does; a marker the resolver refuses
/// (a handle that is gone) fails the candidate reading it, not the choice.
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
                    + string.Concat(fits.Select(fit => $"\n  {Signatures.Format(fit.Method)} — options.signature {SignatureArray(fit.Method)}"))
            ),
        };
    }

    /// <summary>The <c>options.signature</c> that picks <paramref name="method"/>, ready to paste: <c>["int", "string?"]</c>.</summary>
    private static string SignatureArray(MethodBase method)
    {
        NullabilityInfoContext context = new();
        return $"[{string.Join(", ", method.GetParameters().Select(parameter => $"\"{Signatures.ParameterType(parameter, context)}\""))}]";
    }

    private static Attempt Try(
        MethodBase candidate,
        JsonArray args,
        IReadOnlyList<string>? signature,
        IReadOnlyList<Type>? typeArgs,
        IValueResolver resolver
    )
    {
        string? failure = CountFailure(candidate.GetParameters(), args.Count) ?? Unpassable(candidate);
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

    /// <summary>Why a call cannot pass the candidate's arguments or take its result: a pointer or a by-ref-like type.</summary>
    private static string? Unpassable(MethodBase candidate)
    {
        foreach (ParameterInfo parameter in candidate.GetParameters())
        {
            Type type = Defaults.ValueType(parameter);
            if (type.IsPointer || type.IsFunctionPointer)
            {
                return $"parameter '{parameter.Name}' is a pointer ({TypeNames.Format(type)}), which cs_call cannot pass";
            }
            if (type.IsByRefLike)
            {
                return $"parameter '{parameter.Name}' is {TypeNames.WithArticle(type)}, a by-ref-like type cs_call cannot pass";
            }
        }
        return candidate is MethodInfo { ReturnType.IsByRefLike: true } method
            ? $"it returns {TypeNames.WithArticle(method.ReturnType)}, a by-ref-like type cs_call cannot return"
            : null;
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
                values[i] = Argument(parameters[i], args, i, resolver);
            }
            catch (ValueConversionException e)
            {
                return new Attempt(method, null, $"parameter '{parameters[i].Name}': {e.Message}");
            }
            catch (HandleException e)
            {
                return new Attempt(method, null, $"parameter '{parameters[i].Name}': {e.Message}");
            }
        }
        return new Attempt(method, new OverloadChoice(method, values), null);
    }

    /// <summary>Parameter <paramref name="i"/>'s value: null for an <c>out</c>, else its argument converted, else its default.</summary>
    private static object? Argument(ParameterInfo parameter, JsonArray args, int i, IValueResolver resolver)
    {
        if (parameter.IsOut && parameter.ParameterType.IsByRef)
        {
            return null;
        }
        return i < args.Count ? ValueReader.Read(args[i], Defaults.ValueType(parameter), resolver) : Omitted(parameter);
    }

    private static object? Omitted(ParameterInfo parameter) =>
        IsParams(parameter) ? Array.CreateInstance(parameter.ParameterType.GetElementType()!, 0) : Defaults.Of(parameter);

    private static bool IsParams(ParameterInfo parameter) => parameter.IsDefined(typeof(ParamArrayAttribute), inherit: false);

    private sealed record Attempt(MethodBase Method, OverloadChoice? Choice, string? Failure);
}
