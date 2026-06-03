using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;

namespace DeltaLinq;

/// <summary>One mapped column: a CLR property and its Delta/SQL column name.</summary>
internal sealed class ColumnMapping
{
    public required PropertyInfo Property { get; init; }
    public required string ColumnName { get; init; }
}

/// <summary>
/// Maps an entity type to Delta columns. Honours <see cref="ColumnAttribute"/> (rename) and
/// <see cref="NotMappedAttribute"/> (exclude) from System.ComponentModel.DataAnnotations.Schema.
/// Default: public read/write properties, column name == property name. Cached per type.
/// </summary>
internal sealed class EntityModel
{
    private static readonly ConcurrentDictionary<Type, EntityModel> Cache = new();

    private readonly Dictionary<string, string> _propertyToColumn;

    public Type Type { get; }
    public IReadOnlyList<ColumnMapping> Columns { get; }

    private EntityModel(Type type)
    {
        Type = type;
        Columns = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite
                        && p.GetIndexParameters().Length == 0
                        && p.GetCustomAttribute<NotMappedAttribute>() is null)
            .Select(p => new ColumnMapping
            {
                Property = p,
                ColumnName = p.GetCustomAttribute<ColumnAttribute>()?.Name ?? p.Name
            })
            .ToList();

        _propertyToColumn = Columns.ToDictionary(c => c.Property.Name, c => c.ColumnName);

        if (Columns.Count == 0)
            throw new NotSupportedException(
                $"Entity '{type.Name}' has no mapped columns. Add public get/set properties (or remove [NotMapped]).");
    }

    public static EntityModel For(Type type) => Cache.GetOrAdd(type, t => new EntityModel(t));

    public string ColumnForMember(MemberInfo member)
    {
        if (_propertyToColumn.TryGetValue(member.Name, out var column))
            return column;

        throw new NotSupportedException(
            $"'{member.DeclaringType?.Name}.{member.Name}' is not a mapped column on '{Type.Name}'. " +
            "It may be [NotMapped], read-only, or non-public.");
    }

    public bool TryColumn(MemberInfo member, out string column)
        => _propertyToColumn.TryGetValue(member.Name, out column!);
}
