using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace GodotMcp.Dotnet.Core;

/// <summary>One member as a listing spells it: its kind, its name, its signature and whether it is static.</summary>
public sealed record MemberEntry(string Kind, string Name, string Signature, bool Static);

/// <summary>What a listing asks for: instance and static members, or statics and constructors alone.</summary>
public enum MemberScope
{
    InstanceAndStatic,
    StaticAndConstructors,
}

/// <summary>
/// The members of a type and of its bases up to <c>stopAt</c>, as <see cref="MemberEntry"/>s. Accessors, operators,
/// compiler-generated members such as backing fields, nested types and members marked
/// <see cref="System.ComponentModel.EditorBrowsableAttribute"/> <c>Never</c> are left out; an override or a <c>new</c>
/// hide is listed once, as the most derived type declares it; a constructor is the target type's own alone, since it is
/// never inherited; and the listing is sorted by name, then signature.
/// </summary>
public static class MemberListing
{
    public static IReadOnlyList<MemberEntry> List(Type type, MemberScope scope, bool nonPublic, string? name, Func<Type, bool> stopAt)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(stopAt);
        List<MemberEntry> entries = Constructors(type, scope, nonPublic);
        HashSet<string> seen = new(entries.Select(entry => entry.Signature), StringComparer.Ordinal);
        for (Type? level = type; level is not null && level != typeof(object) && !stopAt(level); level = level!.BaseType)
        {
            Add(entries, seen, level, scope, nonPublic);
        }
        List<MemberEntry> sorted = [.. entries.Where(entry => Matches(entry, name))];
        sorted.Sort(Compare);
        return sorted;
    }

    /// <summary>Adds a level's members, skipping each one whose signature a more derived level already listed.</summary>
    private static void Add(List<MemberEntry> entries, HashSet<string> seen, Type level, MemberScope scope, bool nonPublic)
    {
        foreach (MemberEntry entry in Level(level, scope, nonPublic))
        {
            if (seen.Add(entry.Signature))
            {
                entries.Add(entry);
            }
        }
    }

    /// <summary>One type's declared members, its constructors aside, as the scope asks for them.</summary>
    private static IEnumerable<MemberEntry> Level(Type level, MemberScope scope, bool nonPublic)
    {
        bool instance = scope == MemberScope.InstanceAndStatic;
        BindingFlags visible = BindingFlags.DeclaredOnly | BindingFlags.Public | (nonPublic ? BindingFlags.NonPublic : 0);
        BindingFlags flags = visible | BindingFlags.Static | (instance ? BindingFlags.Instance : 0);
        return level
            .GetMethods(flags)
            .Cast<MemberInfo>()
            .Concat(level.GetProperties(flags))
            .Concat(level.GetFields(flags))
            .Concat(level.GetEvents(flags))
            .Where(Kept)
            .Select(Entry);
    }

    /// <summary>
    /// The target type's own constructors, and none for a scope that does not ask for them or for an abstract or static
    /// type: a constructor is never inherited, so no base level contributes one.
    /// </summary>
    private static List<MemberEntry> Constructors(Type type, MemberScope scope, bool nonPublic)
    {
        if (scope != MemberScope.StaticAndConstructors || type.IsAbstract)
        {
            return [];
        }
        BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance | (nonPublic ? BindingFlags.NonPublic : 0);
        return [.. type.GetConstructors(flags).Where(Kept).Select(Entry)];
    }

    /// <summary>Whether the listing spells this member: not a way in to another member, and not hidden from a reader.</summary>
    internal static bool Kept(MemberInfo member) =>
        member is not MethodInfo { IsSpecialName: true }
        && !member.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
        && !member.Name.StartsWith('<')
        && member.GetCustomAttribute<EditorBrowsableAttribute>(inherit: false) is not { State: EditorBrowsableState.Never };

    private static MemberEntry Entry(MemberInfo member) =>
        new(Kind(member), member is ConstructorInfo ? ".ctor" : member.Name, Signatures.Format(member), IsStatic(member));

    private static string Kind(MemberInfo member) =>
        member switch
        {
            ConstructorInfo => "constructor",
            MethodInfo => "method",
            PropertyInfo => "property",
            FieldInfo => "field",
            EventInfo => "event",
            _ => throw new NotSupportedException($"{member.GetType().Name} is not a member a listing spells"),
        };

    private static bool IsStatic(MemberInfo member) =>
        member switch
        {
            MethodInfo method => method.IsStatic,
            PropertyInfo property => property.GetAccessors(nonPublic: true).Any(accessor => accessor.IsStatic),
            FieldInfo field => field.IsStatic,
            EventInfo handler => handler.AddMethod?.IsStatic ?? false,
            _ => false,
        };

    private static bool Matches(MemberEntry entry, string? name) => name is null || entry.Name.Contains(name, StringComparison.OrdinalIgnoreCase);

    private static int Compare(MemberEntry left, MemberEntry right)
    {
        int byName = string.CompareOrdinal(left.Name, right.Name);
        return byName != 0 ? byName : string.CompareOrdinal(left.Signature, right.Signature);
    }
}
