using System.Collections;
using System.Reflection;

namespace Revi;

public partial class Util
{
    public static ComparisonResult[] CompareObjects<T>(T oldObject, T newObject)
    {
        if (oldObject == null || newObject == null)
        {
            throw new ArgumentNullException("Both oldObject and newObject must be non-null.");
        }

        var results = new List<ComparisonResult>();
        var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var property in properties)
        {
            var oldValue = property.GetValue(oldObject);
            var newValue = property.GetValue(newObject);
            var result = new ComparisonResult
            {
                PropertyName = property.Name,
                OldValue = oldValue,
                NewValue = newValue
            };

            if (property.PropertyType == typeof(string))
            {
                // Compare strings
                result.Changed = !string.Equals((string?)oldValue, (string?)newValue);
                if (result.Changed)
                {
                    result.Description = $"{property.Name} changed from '{oldValue}' to '{newValue}'.";
                }
            }
            else if (typeof(IEnumerable).IsAssignableFrom(property.PropertyType) && property.PropertyType != typeof(string))
            {
                // Handle collections
                var oldList = oldValue as IEnumerable ?? new List<object>();
                var newList = newValue as IEnumerable ?? new List<object>();

                var oldSet = new HashSet<object>(oldList.Cast<object>());
                var newSet = new HashSet<object>(newList.Cast<object>());

                var removed = oldSet.Except(newSet).ToList();
                var added = newSet.Except(oldSet).ToList();

                result.Changed = removed.Any() || added.Any();
                if (result.Changed)
                {
                    result.Description = $"{property.Name} removed: {string.Join(", ", removed)}; added: {string.Join(", ", added)}.";
                }
            }
            else if (property.PropertyType.IsValueType || property.PropertyType.IsPrimitive)
            {
                // Compare value types
                result.Changed = !Equals(oldValue, newValue);
                if (result.Changed)
                {
                    result.Description = $"{property.Name} changed from '{oldValue}' to '{newValue}'.";
                }
            }
            else
            {
                // Fallback for unsupported types
                result.Changed = !Equals(oldValue, newValue);
                if (result.Changed)
                {
                    result.Description = $"Complex property {property.Name} changed.";
                }
            }

            results.Add(result);
        }

        return results.ToArray();
    }
    
    public static T ObjectFromComparison<T>(T baseObject, ComparisonResult[] changes) where T : new()
    {
        if (baseObject == null)
        {
            throw new ArgumentNullException(nameof(baseObject));
        }
        if (changes == null)
        {
            throw new ArgumentNullException(nameof(changes));
        }

        var resultObject = new T();
        var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var property in properties)
        {
            var result = changes.FirstOrDefault(c => c.PropertyName == property.Name);
            if (result != null)
            {
                // If the property has changed, set the new value; otherwise, use the base object's value
                var value = result.Changed ? result.NewValue : property.GetValue(baseObject);
                property.SetValue(resultObject, value);
            }
        }

        return resultObject;
    }
}
