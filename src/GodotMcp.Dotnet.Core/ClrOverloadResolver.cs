using System.Reflection;

namespace GodotMcp.Dotnet.Core;

/// <summary>
/// Picks the one overload that takes a call's CLR arguments, as a snippet's <c>Call</c> passes them. A candidate fits when it
/// takes that many arguments (an optional parameter left off takes its default; a <c>params</c> last parameter takes the rest,
/// packed into its array, or an array given as that one argument) and each argument is null for a reference or
/// <see cref="Nullable{T}"/> parameter, else an instance of the parameter's type. A fit whose every argument matches its
/// parameter's type at least as exactly as another fit's, and one argument more exactly (its runtime type is the parameter's
/// own), wins over that one. A generic method definition, an <c>out</c> or <c>ref</c> parameter, and a pointer or by-ref-like
/// parameter never fit.
/// </summary>
public static class ClrOverloadResolver
{
    /// <exception cref="OverloadException">No candidate, or more than one equally exact candidate, takes the arguments.</exception>
    public static OverloadChoice Choose(IReadOnlyList<MethodBase> candidates, IReadOnlyList<object?> args)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(args);
        if (candidates.Count == 0)
        {
            throw new ArgumentException("There is no candidate to choose from.", nameof(candidates));
        }
        List<Attempt> attempts = [.. candidates.Select(candidate => Try(candidate, args))];
        List<Attempt> fits = [.. attempts.Where(attempt => attempt.Choice is not null)];
        List<Attempt> best = [.. fits.Where(fit => !fits.Any(other => Dominates(other.Exact, fit.Exact)))];
        string name = candidates[0].Name;
        return best.Count switch
        {
            1 => best[0].Choice!,
            0 => throw new OverloadException(
                $"no overload of '{name}' takes these arguments:"
                    + string.Concat(attempts.Select(a => $"\n  {Signatures.Format(a.Method)}: {a.Failure}"))
            ),
            _ => throw new OverloadException(
                $"{best.Count} overloads of '{name}' take these arguments; cast an argument to its parameter's type to pick one:"
                    + string.Concat(best.Select(fit => $"\n  {Signatures.Format(fit.Method)}"))
            ),
        };
    }

    private static Attempt Try(MethodBase candidate, IReadOnlyList<object?> args)
    {
        ParameterInfo[] parameters = candidate.GetParameters();
        string? failure = Unfit(candidate) ?? CountFailure(parameters, args.Count);
        return failure is null ? Bind(candidate, parameters, args) : Failed(candidate, failure);
    }

    /// <summary>Why the candidate can never take CLR arguments: it is generic, or a parameter is passed by reference or cannot be boxed.</summary>
    private static string? Unfit(MethodBase candidate)
    {
        if (candidate.IsGenericMethodDefinition)
        {
            return "it is generic, and Call cannot infer its type arguments; cs_call with options.typeArgs calls it";
        }
        return candidate.GetParameters().Select(Unpassable).FirstOrDefault(failure => failure is not null);
    }

    private static string? Unpassable(ParameterInfo parameter)
    {
        if (parameter.ParameterType.IsByRef && !parameter.IsIn)
        {
            return $"parameter '{parameter.Name}' is {(parameter.IsOut ? "out" : "ref")}, which Call cannot pass";
        }
        Type type = Defaults.ValueType(parameter);
        return type.IsPointer || type.IsFunctionPointer || type.IsByRefLike
            ? $"parameter '{parameter.Name}' is {TypeNames.WithArticle(type)}, which Call cannot pass"
            : null;
    }

    private static string? CountFailure(ParameterInfo[] parameters, int given)
    {
        int required = parameters.Count(IsRequired);
        bool takesRest = parameters.Length > 0 && IsParams(parameters[^1]);
        if (given >= required && (takesRest || given <= parameters.Length))
        {
            return null;
        }
        return $"takes {Range(required, parameters.Length, takesRest)}, got {given}";
    }

    private static bool IsRequired(ParameterInfo parameter) => !parameter.IsOptional && !parameter.HasDefaultValue && !IsParams(parameter);

    /// <summary>How many arguments a candidate takes, in words: <c>1 argument</c>, <c>2 to 3 arguments</c>, <c>1 or more arguments</c>.</summary>
    private static string Range(int required, int total, bool takesRest)
    {
        if (takesRest)
        {
            return $"{required} or more arguments";
        }
        if (required != total)
        {
            return $"{required} to {total} arguments";
        }
        return required == 1 ? "1 argument" : $"{required} arguments";
    }

    /// <summary>The candidate with each parameter's value, or the first argument its parameter does not accept.</summary>
    private static Attempt Bind(MethodBase candidate, ParameterInfo[] parameters, IReadOnlyList<object?> args)
    {
        object?[] values = new object?[parameters.Length];
        bool[] exact = new bool[args.Count];
        for (int i = 0; i < parameters.Length; i++)
        {
            string? failure =
                i == parameters.Length - 1 && IsParams(parameters[i])
                    ? Rest(parameters[i], args, values, exact)
                    : Single(parameters[i], args, values, exact);
            if (failure is not null)
            {
                return Failed(candidate, failure);
            }
        }
        return new Attempt(candidate, new OverloadChoice(candidate, values), exact, null);
    }

    /// <summary>A plain parameter's value: its argument when it accepts it, else its default when it was left off.</summary>
    private static string? Single(ParameterInfo parameter, IReadOnlyList<object?> args, object?[] values, bool[] exact)
    {
        int i = parameter.Position;
        if (i >= args.Count)
        {
            values[i] = Defaults.Of(parameter);
            return null;
        }
        Type type = Defaults.ValueType(parameter);
        values[i] = args[i];
        exact[i] = IsExact(type, args[i]);
        return Refusal(type, args[i]) is { } refusal ? $"parameter '{parameter.Name}': {refusal}" : null;
    }

    /// <summary>
    /// A <c>params</c> parameter's value: an empty array when nothing is left, the one argument left when it is null or already
    /// such an array, else every argument left packed into a new array of its element type.
    /// </summary>
    private static string? Rest(ParameterInfo parameter, IReadOnlyList<object?> args, object?[] values, bool[] exact)
    {
        int start = parameter.Position;
        Type arrayType = parameter.ParameterType;
        if (args.Count == start + 1 && (args[start] is null || arrayType.IsInstanceOfType(args[start])))
        {
            values[start] = args[start];
            exact[start] = IsExact(arrayType, args[start]);
            return null;
        }
        Type element = arrayType.GetElementType()!;
        var packed = Array.CreateInstance(element, Math.Max(0, args.Count - start));
        for (int i = start; i < args.Count; i++)
        {
            if (Refusal(element, args[i]) is { } refusal)
            {
                return $"parameter '{parameter.Name}' item {i - start}: {refusal}";
            }
            packed.SetValue(args[i], i - start);
            exact[i] = IsExact(element, args[i]);
        }
        values[start] = packed;
        return null;
    }

    /// <summary>Why a parameter of <paramref name="type"/> does not accept <paramref name="arg"/>, or null when it does.</summary>
    private static string? Refusal(Type type, object? arg)
    {
        if (arg is null)
        {
            return !type.IsValueType || Nullable.GetUnderlyingType(type) is not null ? null : $"null is not {TypeNames.WithArticle(type)}";
        }
        return type.IsInstanceOfType(arg) ? null : $"{TypeNames.WithArticle(arg.GetType())} is not {TypeNames.WithArticle(type)}";
    }

    private static bool IsExact(Type type, object? arg) =>
        arg is not null && (arg.GetType() == type || arg.GetType() == Nullable.GetUnderlyingType(type));

    /// <summary>Whether <paramref name="better"/> is at least as exact at every argument as <paramref name="worse"/>, and more so at one.</summary>
    private static bool Dominates(bool[] better, bool[] worse) =>
        better.Zip(worse).All(pair => pair.First || !pair.Second) && better.Zip(worse).Any(pair => pair.First && !pair.Second);

    private static bool IsParams(ParameterInfo parameter) => parameter.IsDefined(typeof(ParamArrayAttribute), inherit: false);

    private static Attempt Failed(MethodBase candidate, string failure) => new(candidate, null, [], failure);

    /// <summary>A candidate, its choice when it fits, which arguments match their parameter's type exactly, and why it does not fit.</summary>
    private sealed record Attempt(MethodBase Method, OverloadChoice? Choice, bool[] Exact, string? Failure);
}
