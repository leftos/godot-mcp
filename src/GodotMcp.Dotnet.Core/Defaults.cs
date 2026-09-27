using System.Reflection;

namespace GodotMcp.Dotnet.Core;

/// <summary>What an omitted optional parameter takes, and the parameter's type with any <c>ref</c> taken off.</summary>
internal static class Defaults
{
    /// <summary>
    /// The declared default, as the parameter's enum type when it is one; null (reflection's zero value) when the
    /// parameter is optional without a default.
    /// </summary>
    public static object? Of(ParameterInfo parameter)
    {
        if (!parameter.HasDefaultValue)
        {
            return null;
        }
        object? value = parameter.DefaultValue;
        Type type = Nullable.GetUnderlyingType(ValueType(parameter)) ?? ValueType(parameter);
        return value is not null && type.IsEnum && !type.IsInstanceOfType(value) ? Enum.ToObject(type, value) : value;
    }

    public static Type ValueType(ParameterInfo parameter) =>
        parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType()! : parameter.ParameterType;
}
