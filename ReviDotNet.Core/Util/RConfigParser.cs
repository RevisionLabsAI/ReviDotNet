// =================================================================================
//   Copyright © 2025 Revision Labs, Inc. - All Rights Reserved
// =================================================================================
//   This is proprietary and confidential source code of Revision Labs, Inc., and
//   is safeguarded by international copyright laws. Unauthorized use, copying, 
//   modification, or distribution is strictly forbidden.
//
//   If you are not authorized and have this file, notify Revision Labs at 
//   contact@rlab.ai and delete it immediately.
//
//   See LICENSE.txt in the project root for full license information.
// =================================================================================

using System.Text;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
//using Resen.Common.Researcher;

namespace Revi;

[AttributeUsage(AttributeTargets.Property)]
public class RConfigPropertyAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>
/// Manages the reading and writing from Revision Labs Configs (rcfg)
/// </summary>
public static class RConfigParser
{
    // ==============
    //  Declarations
    // ==============
    
    private static Dictionary<Type, Func<string, object>> _converters = new Dictionary<Type, Func<string, object>>
    {
        { typeof(DateTime), value => DateTime.Parse(value) },
        { typeof(Guid), value => Guid.Parse(value) }
    };
    
    
    // ======================
    //  Supporting Functions
    // ======================
    
    /// <summary>
    /// Registers a custom converter for a specific type.
    /// </summary>
    /// <typeparam name="T">The type for which the converter is registered.</typeparam>
    /// <param name="converter">The converter function that converts a string to the specified type.</param>
    public static void RegisterCustomConverter<T>(Func<string, T> converter)
    {
        _converters[typeof(T)] = value => converter(value) ?? throw new InvalidOperationException();
    }
    
    /// <summary>
    /// Converts a string to a specified type using registered converters or default conversion.
    /// </summary>
    /// <param name="value">The string value to convert.</param>
    /// <param name="type">The type to which the value should be converted.</param>
    /// <returns>An object of the specified type.</returns>
    public static object? ConvertToType(string value, Type type)
    {
        // Nullable type support
        Type? underlyingType = Nullable.GetUnderlyingType(type);
        if (underlyingType != null)
        {
            // Handle conversion for nullable types
            return string.IsNullOrEmpty(value) ? null : ConvertToType(value, underlyingType);
        }
        
        // Handle enums separately
        if (type.IsEnum)
        {
            if (Enum.TryParse(type, value, true, out var enumResult) && enumResult != null)
            {
                return enumResult;
            }
            else
            {
                throw new FormatException($"Unable to convert '{value}' to enum type {type.Name}.");
            }
        }
        
        // Handle custom converters
        if (_converters.TryGetValue(type, out var converter))
        {
            return converter(value);
        }

        // Default conversion for other types
        return System.Convert.ChangeType(value, type);
    }
    
    /// <summary>
    /// Sorts keys according to custom requirements: first by predefined categories,
    /// then by '_exin_' and '_exout_' keys in sequential numeric order.
    /// </summary>
    /// <param name="extension">File extension used to determine the comparer to use.</param>
    /// <param name="serializedData">The unsorted dictionary to sort.</param>
    /// <returns>A new dictionary sorted according to the specified key order.</returns>
    private static SortedDictionary<string, string> SortDictionary(
        string extension,
        Dictionary<string, string> serializedData)
    {
        IComparer<string> comparer = extension switch
        {
            ".pmt" => new PromptConfigComparer(),
            _ => StringComparer.Ordinal
        };

        SortedDictionary<string, string> sortedDictionary = new(comparer);

        foreach (var item in serializedData)
        {
            sortedDictionary.Add(item.Key, item.Value);
        }

        return sortedDictionary;
    }
    
    /// <summary>
    /// Custom comparer to sort dictionary keys according to specific rules.
    /// </summary>
    private class PromptConfigComparer : IComparer<string>
    {
        public int Compare(string x, string y)
        {
            if (x.StartsWith("_ex") && y.StartsWith("_ex"))
            {
                var xParts = x.Split('_');
                var yParts = y.Split('_');
                int xNum = int.Parse(xParts[2]);
                int yNum = int.Parse(yParts[2]);
                int comparison = xNum.CompareTo(yNum);
                if (comparison != 0)
                    return comparison;

                return String.Compare(xParts[1], yParts[1], StringComparison.Ordinal); // Compare 'in' vs 'out'
            }

            // Define explicit order for predefined categories
            var order = new List<string> { "information", "settings", "tuning", "_system", "_instruction" };
            int indexX = order.IndexOf(order.Find(p => x.StartsWith(p)));
            int indexY = order.IndexOf(order.Find(p => y.StartsWith(p)));

            if (indexX == -1) indexX = int.MaxValue; // Unknown items go to the end
            if (indexY == -1) indexY = int.MaxValue;

            return indexX.CompareTo(indexY);
        }
    }
    
    public static void CallInitIfExists(object obj)
    {
        //Util.Log("CallInitIfExists called");
        // Get the type of the object
        Type type = obj.GetType();

        // Try to find the 'Init' method with no parameters
        MethodInfo? methodInfo = type.GetMethod(
            "Init", 
            BindingFlags.Public | BindingFlags.Instance, 
            null, 
            Type.EmptyTypes,
            null);

        // Check if the method exists
        if (methodInfo != null)
        {
            // Call the method on the object if it exists
            methodInfo.Invoke(obj, null);
        }
        else
        {
            Console.WriteLine("Method 'Init' not found.");
        }
    }

    
    
    // ======================
    //  File Reading/Writing
    // ======================
    
    /// <summary>
    /// Reads rcfg data from a file and converts it into a dictionary.
    /// </summary>
    /// <param name="filePath">The path of the file to read.</param>
    /// <returns>A dictionary containing the rcfg data.</returns>
    public static Dictionary<string, string> ReadEmbedded(string content)
    {
        var configDictionary = new Dictionary<string, string>();
        string currentSection = "";
        StringBuilder sectionContent = new();

        try
        {
            using var reader = new StringReader(content);
            string line;
        
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                
                ProcessLine(
                    line, 
                    ref currentSection, 
                    ref sectionContent, 
                    ref configDictionary);
            }
        }
        catch (Exception ex)
        {
            throw new IOException("An error occurred while reading the configuration content.", ex);
        }

        return configDictionary;
    }
    
    /// <summary>
    /// Reads rcfg data from a file and converts it into a dictionary.
    /// </summary>
    /// <param name="filePath">The path of the file to read.</param>
    /// <returns>A dictionary containing the rcfg data.</returns>
    public static Dictionary<string, string> Read(string filePath)
    {
        //Util.Log($"Read: {filePath}");
        
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"The specified file was not found: {filePath}");

        var configDictionary = new Dictionary<string, string>();
        string currentSection = "";
        StringBuilder sectionContent = new();

        try
        {
            foreach (string line in File.ReadAllLines(filePath))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                
                ProcessLine(
                    line, 
                    ref currentSection, 
                    ref sectionContent, 
                    ref configDictionary);
            }
        }
        catch (Exception ex)
        {
            string fileType = Path.GetExtension(filePath);
            throw new IOException($"An error occurred while reading the {fileType} file.", ex);
        }

        return configDictionary;
    }
    
    private static void ProcessLine(
        string line, 
        ref string currentSection, 
        ref StringBuilder sectionContent, 
        ref Dictionary<string, string> configDictionary)
    {
        if (line.StartsWith("[[") && line.EndsWith("]]"))
        {
            if (currentSection.StartsWith("_") && sectionContent.Length > 0)
            {
                configDictionary[currentSection] = sectionContent.ToString().Trim();
                sectionContent.Clear();
            }

            currentSection = line.Substring(2, line.Length - 4).Trim();
        }
        else if (currentSection.StartsWith("_"))
        {
            sectionContent.AppendLine(line);
        }
        else
        {
            var separatorIndex = line.IndexOf('=');
            if (separatorIndex != -1)
            {
                string key = $"{currentSection}_{line.Substring(0, separatorIndex).Trim()}";
                string value = line.Substring(separatorIndex + 1).Trim();
                configDictionary[key] = value;
            }
        }

        if (currentSection.StartsWith("_") && sectionContent.Length > 0)
        {
            configDictionary[currentSection] = sectionContent.ToString().Trim();
        }
    }
    
    /// <summary>
    /// Writes rcfg data to a file from a dictionary.
    /// </summary>
    /// <param name="filePath">The path of the file to write to.</param>
    /// <param name="data">The rcfg data to write.</param>
    public static void Write(
        string filePath, 
        Dictionary<string, string> data)
    {
        SortedDictionary<string, string> sortedData = SortDictionary(Path.GetExtension(filePath), data);
        try
        {
            using (StreamWriter file = new StreamWriter(filePath))
            {
                foreach (var item in sortedData)
                {
                    if (item.Key.StartsWith("_"))
                    {
                        file.WriteLine($"[[{item.Key}]]");
                        file.WriteLine(item.Value);
                        file.WriteLine();
                    }
                    else
                    {
                        string[] parts = item.Key.Split('_');
                        if (parts.Length == 2)
                        {
                            file.WriteLine($"[[{parts[0]}]]");
                            file.WriteLine($"{parts[1]} = {item.Value}");
                            file.WriteLine();
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            string fileType = Path.GetExtension(filePath);
            throw new IOException($"An error occurred while writing to the {fileType} file.", ex);
        }
    }
    
    
    // ===============
    //  Serialization
    // ===============
    
    /// <summary>
    /// Serializes an object into a dictionary of string key-value pairs.
    /// Only properties decorated with the RConfigPropertyAttribute will be serialized.
    /// </summary>
    /// <param name="obj">The object to serialize.</param>
    /// <returns>A dictionary representing the serialized form of the object.</returns>
    /// <exception cref="ArgumentNullException">Thrown if the provided object is null.</exception>
    public static Dictionary<string, string> ToDictionary(object obj)
    { 
        ArgumentNullException.ThrowIfNull(obj);

        var type = obj.GetType();
        var properties = type.GetProperties();
        var serializedData = new Dictionary<string, string>();

        foreach (var property in properties)
        {
           var attribute = property.GetCustomAttributes(typeof(RConfigPropertyAttribute), false)
                       .FirstOrDefault() as RConfigPropertyAttribute;

           if (attribute != null)
           {
               var value = property.GetValue(obj)?.ToString() ?? "";
               serializedData[attribute.Name] = value;
           }
        } 
        
        return serializedData;
    }

    /// <summary>
    /// Deserializes a dictionary of string key-value pairs into an object.
    /// Only properties decorated with the RConfigPropertyAttribute will be deserialized.
    /// </summary>
    /// <typeparam name="T">The type of object to deserialize into.</typeparam>
    /// <param name="data">The dictionary containing the serialized data.</param>
    /// <param name="namePrefix">Optional prefix for the property name.</param>
    /// <returns>An object of type T, deserialized from the dictionary.</returns>
    /// <exception cref="ArgumentNullException">Thrown if the provided data is null.</exception>
    /// <exception cref="FormatException">Thrown if a value in the provided data cannot
    /// be converted to the type of the corresponding property in the object.</exception>
    public static T? ToObject<T>(Dictionary<string, string> data, string? namePrefix = "") where T : new()
    {
        ArgumentNullException.ThrowIfNull(data);
        var obj = new T();
        var properties = typeof(T).GetProperties();

        foreach (var property in properties)
        {
            var attribute = property.GetCustomAttributes(typeof(RConfigPropertyAttribute), false)
                .FirstOrDefault() as RConfigPropertyAttribute;

            if (attribute != null && data.TryGetValue(attribute.Name, out var value))
            {
                // Skip adding this property to the object/leave null
                if ((value.ToLower() == "default") || (value.ToLower() == "prompt"))
                    continue;

                if (property.Name == "Name" && namePrefix != null)
                {
                    value = $"{namePrefix}{value}";
                }

                try
                {
                    // Check if property type is nullable and convert appropriately
                    object? convertedValue = ConvertToType(value, property.PropertyType);
                    property.SetValue(obj, convertedValue);
                }
                catch (Exception ex) // Catch exceptions during type conversion and handle them accordingly
                {
                    throw new FormatException($"Failed to convert value to target type. Property: {property.Name}", ex);
                }
            }
        }

        try
        {
            CallInitIfExists(obj);
        }
        catch (Exception e)
        {
            Util.Log($"Init exists but failed! Message: {e.Message}");
        }

        return obj;
    }
}
