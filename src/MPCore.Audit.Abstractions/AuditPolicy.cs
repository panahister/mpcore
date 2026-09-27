using System.Linq.Expressions;

namespace MPCore.Audit;

/// <summary>How a captured value is reduced before it is stored.</summary>
public enum MaskStyle
{
    /// <summary>Replace the whole value with a fixed marker.</summary>
    Redact = 0,

    /// <summary>Keep the last four characters, redact the rest. For account and card style identifiers.</summary>
    KeepLastFour = 1,
}

/// <summary>
/// Capture policy for one entity type. Default deny: a property is recorded only when it has been
/// allowlisted here. Some names can never be recorded at all, and some are recorded only masked.
/// </summary>
public sealed class AuditEntityPolicy
{
    private readonly Dictionary<string, MaskStyle?> _properties = new(StringComparer.Ordinal);

    internal AuditEntityPolicy(Type entityType, string module)
    {
        EntityType = entityType;
        Module = module;
    }

    /// <summary>The audited entity type.</summary>
    public Type EntityType { get; }
    /// <summary>Module the entity belongs to.</summary>
    public string Module { get; }

    /// <summary>
    /// True by default: a failure to record the audit fails the operation. Set to false only for an
    /// entity whose history is informative rather than obligatory.
    /// </summary>
    public bool Required { get; internal set; } = true;

    /// <summary>Allowlisted properties and how each is masked. Null means recorded verbatim.</summary>
    public IReadOnlyDictionary<string, MaskStyle?> Properties => _properties;

    internal void Include(string name, MaskStyle? mask)
    {
        if (AuditMasking.IsHardExcluded(name))
        {
            // The builder refuses rather than silently dropping: a developer who allowlists a
            // password field should find out at startup, not by reading the audit table.
            throw new InvalidOperationException(
                $"'{name}' cannot be audited: credential-like properties are never recorded, masked or not.");
        }

        _properties[name] = mask ?? (AuditMasking.RequiresMask(name) ? MaskStyle.Redact : null);
    }
}

/// <summary>Fluent builder for one entity's policy.</summary>
public sealed class AuditEntityPolicyBuilder<TEntity>
{
    private readonly AuditEntityPolicy _policy;

    internal AuditEntityPolicyBuilder(AuditEntityPolicy policy) => _policy = policy;

    /// <summary>Record the property verbatim, unless its name is one that policy always masks.</summary>
    public AuditEntityPolicyBuilder<TEntity> Include(Expression<Func<TEntity, object?>> property)
    {
        _policy.Include(PropertyName(property), null);
        return this;
    }

    /// <summary>Record the property masked.</summary>
    public AuditEntityPolicyBuilder<TEntity> Mask(Expression<Func<TEntity, object?>> property, MaskStyle style = MaskStyle.Redact)
    {
        _policy.Include(PropertyName(property), style);
        return this;
    }

    /// <summary>Audit failures for this entity are logged, not raised. Use sparingly and deliberately.</summary>
    public AuditEntityPolicyBuilder<TEntity> BestEffort()
    {
        _policy.Required = false;
        return this;
    }

    private static string PropertyName(Expression<Func<TEntity, object?>> property)
    {
        var body = property.Body is UnaryExpression { NodeType: ExpressionType.Convert } unary ? unary.Operand : property.Body;
        return body is MemberExpression member
            ? member.Member.Name
            : throw new ArgumentException("Expression must select a property.", nameof(property));
    }
}

/// <summary>The complete capture policy of an application: which entities, which properties, which module.</summary>
public sealed class AuditPolicy
{
    private readonly Dictionary<Type, AuditEntityPolicy> _entities = [];

    /// <summary>All entity policies, keyed by entity type.</summary>
    public IReadOnlyDictionary<Type, AuditEntityPolicy> Entities => _entities;

    /// <summary>Declare an entity as audited. Nothing is captured for it until properties are included.</summary>
    public AuditEntityPolicyBuilder<TEntity> Entity<TEntity>(string module)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        if (!_entities.TryGetValue(typeof(TEntity), out var policy))
        {
            policy = new AuditEntityPolicy(typeof(TEntity), module);
            _entities[typeof(TEntity)] = policy;
        }

        return new AuditEntityPolicyBuilder<TEntity>(policy);
    }

    /// <summary>Finds the policy for a type or any of its base types.</summary>
    public AuditEntityPolicy? Find(Type entityType)
    {
        for (var type = entityType; type is not null; type = type.BaseType)
        {
            if (_entities.TryGetValue(type, out var policy))
            {
                return policy;
            }
        }

        return null;
    }
}

/// <summary>Name-based safety net. Applies regardless of what a policy says.</summary>
public static class AuditMasking
{
    private static readonly string[] HardExcluded =
        ["password", "passwd", "pwd", "secret", "token", "apikey", "api_key", "clientsecret", "cvv", "cvc", "pin", "otp", "privatekey"];

    private static readonly string[] AlwaysMasked =
        ["iban", "cardnumber", "card_number", "pan", "accountnumber", "account_number", "nationalid", "national_id", "nationalcode", "ssn", "passport", "phone", "mobile", "email"];

    /// <summary>Never recorded, masked or not. A policy that includes one throws at build time.</summary>
    public static bool IsHardExcluded(string propertyName) =>
        HardExcluded.Any(fragment => propertyName.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    /// <summary>Recorded only masked, even when included verbatim by policy.</summary>
    public static bool RequiresMask(string propertyName) =>
        AlwaysMasked.Any(fragment => propertyName.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    /// <summary>Applies a mask style to a value. Null values and null styles pass through.</summary>
    public static string? Apply(string? value, MaskStyle? style)
    {
        if (value is null || style is null)
        {
            return value;
        }

        return style switch
        {
            MaskStyle.KeepLastFour when value.Length > 4 => new string('*', value.Length - 4) + value[^4..],
            MaskStyle.KeepLastFour => new string('*', value.Length),
            _ => "***",
        };
    }
}
