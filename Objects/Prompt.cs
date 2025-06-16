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

using System.Reflection;
using Newtonsoft.Json;
using System.Text.RegularExpressions;
using Sys = System;

namespace Revi;

public class Prompt
{
    // ==========================
    //  Prompt Object Definition
    // ==========================
    
    #region Prompt Object Definition
    // Information
    [JsonProperty("id"), RConfigProperty("information_id")]
    public string ID { get; set; }

    [JsonProperty("name"), RConfigProperty("information_name")]
    public string? Name { get; set; }

    [JsonProperty("version"), RConfigProperty("information_version")]
    public int? Version { get; set; }

    [JsonProperty("date-created"), RConfigProperty("information_date-created")]
    public DateTime? DateCreated { get; set; }

    [JsonProperty("date-updated"), RConfigProperty("information_date-updated")]
    public DateTime? DateUpdated { get; set; }

    
    // Settings
    [JsonProperty("filter"), RConfigProperty("settings_filter")]
    public string? Filter { get; set; }

    [JsonProperty("retry"), RConfigProperty("settings_retry")]
    public int? Retry { get; set; }

    [JsonProperty("chain-of-thought"), RConfigProperty("settings_chain-of-thought")]
    public bool? ChainOfThought { get; set; }

    [JsonProperty("request-json"), RConfigProperty("settings_request-json")]
    public bool? RequestJson { get; set; }

    [JsonProperty("guidance-schema-type"), RConfigProperty("settings_guidance-schema-type")]
    public GuidanceSchemaType? GuidanceSchema { get; set; }

    [JsonProperty("few-shot-examples"), RConfigProperty("settings_few-shot-examples")]
    public int? FewShotExamples { get; set; }
    
    [JsonProperty("best-of"), RConfigProperty("settings_best-of")]
    public int? BestOf { get; set; }
    
    [JsonProperty("max-tokens"), RConfigProperty("settings_max-tokens")]
    public int? MaxTokens { get; set; }

    [JsonProperty("model-pref"), RConfigProperty("settings_model-pref")]
    public string? ModelPref { get; set; }

    [JsonProperty("min-tier"), RConfigProperty("settings_min-tier")]
    public string? MinTier { get; set; }

    [JsonProperty("completion-type"), RConfigProperty("settings_completion-type")]
    public string? CompletionType { get; set; }

    
    // Tuning
    [JsonProperty("temperature"), RConfigProperty("tuning_temperature")]
    public float? Temperature { get; set; }

    [JsonProperty("top-k"), RConfigProperty("tuning_top-k")]
    public int? TopK { get; set; }

    [JsonProperty("top-p"), RConfigProperty("tuning_top-p")]
    public float? TopP { get; set; }

    [JsonProperty("min-p"), RConfigProperty("tuning_min-p")]
    public float? MinP { get; set; }

    [JsonProperty("presence-penalty"), RConfigProperty("tuning_presence-penalty")]
    public float? PresencePenalty { get; set; }

    [JsonProperty("frequency-penalty"), RConfigProperty("tuning_frequency-penalty")]
    public float? FrequencyPenalty { get; set; }

    [JsonProperty("repetition-penalty"), RConfigProperty("tuning_repetition-penalty")]
    public float? RepetitionPenalty { get; set; }

    
    // Prompt
    [JsonProperty("system"), RConfigProperty("_system")]
    public string? System { get; set; }

    [JsonProperty("instruction"), RConfigProperty("_instruction")]
    public string? Instruction { get; set; }
    
    
    // Output & Examples
    [JsonProperty("schema"), RConfigProperty("_schema")]
    public string? Schema { get; set; }

    [JsonProperty("examples")]
    public List<Example>? Examples { get; set; }
    #endregion
    
    
    // ==============
    //  Constructors
    // ==============
    
    #region Constructors
    public void Init()
    {
        if (string.IsNullOrWhiteSpace(ID))
            ID = Guid.NewGuid().ToString();
        
        if (string.IsNullOrWhiteSpace(Name))
            throw new ArgumentException("Name must not be null or empty.");
        
        if (Version is null)
            throw new ArgumentException("Version must not be null.");
        
        if (DateCreated is null)
            throw new ArgumentException("DateCreated must not be null.");
        
        if (DateUpdated is null)
            throw new ArgumentException("DateUpdated must not be null.");

        if (string.IsNullOrWhiteSpace(System) && string.IsNullOrWhiteSpace(Instruction))
            throw new ArgumentException("System and instruction cannot both be null or empty.");
        
        if (string.IsNullOrWhiteSpace(Schema))
            Schema = CreateSchemaFromExamples(Examples);
    }
    
    public Prompt()
    { 
        ID = Guid.NewGuid().ToString();
    }
    
    public Prompt(
        string? ID, 
        string? Name, 
        int? Version, 
        DateTime? DateCreated, 
        DateTime? DateUpdated, 
        string? Filter = null,
        int? Retry = null, 
        bool? ChainOfThought = false, 
        bool? RequestJson = false, 
        GuidanceSchemaType? GuidanceSchema = null,
        int? FewShotExamples = null,
        string? ModelPref = null, 
        string? MinTier = null,
        string? CompletionType = null,
        float? Temperature = null, 
        int? TopK = null, 
        float? TopP = null, 
        float? MinP = null, 
        float? PresencePenalty = null, 
        float? FrequencyPenalty = null, 
        float? RepetitionPenalty = null, 
        string? System = null, 
        string? Instruction = null, 
        string? Schema = null,
        List<Example>? Examples = null)
    {
        if (string.IsNullOrWhiteSpace(ID))
            ID = Guid.NewGuid().ToString();
        this.ID = ID;
        this.Name = Name;
        this.Version = Version;
        this.DateCreated = DateCreated;
        this.DateUpdated = DateUpdated;
        this.Filter = Filter;
        this.Retry = Retry;
        this.ChainOfThought = ChainOfThought;
        this.RequestJson = RequestJson;
        this.GuidanceSchema = GuidanceSchema;
        this.FewShotExamples = FewShotExamples;
        this.ModelPref = ModelPref;
        this.MinTier = MinTier;
        this.CompletionType = CompletionType;
        this.Temperature = Temperature;
        this.TopK = TopK;
        this.TopP = TopP;
        this.MinP = MinP;
        this.PresencePenalty = PresencePenalty;
        this.FrequencyPenalty = FrequencyPenalty;
        this.RepetitionPenalty = RepetitionPenalty;
        this.System = System;
        this.Instruction = Instruction;
        this.Schema = Schema;
        this.Examples = Examples;

        Init();
    }
    
    // Deep Copy Constructor
    public Prompt(Prompt original)
    {
        // Copy value type Information
        ID = Guid.NewGuid().ToString();
        Name = original.Name;
        Version = original.Version;
        DateCreated = original.DateCreated;
        DateUpdated = original.DateUpdated;

        // Copy value type Settings
        Filter = original.Filter;
        Retry = original.Retry;
        ChainOfThought = original.ChainOfThought;
        RequestJson = original.RequestJson;
        GuidanceSchema = original.GuidanceSchema;
        FewShotExamples = original.FewShotExamples;
        ModelPref = original.ModelPref;
        MinTier = original.MinTier;
        CompletionType = original.CompletionType;

        // Copy value type Tuning
        Temperature = original.Temperature;
        TopK = original.TopK;
        TopP = original.TopP;
        MinP = original.MinP;
        PresencePenalty = original.PresencePenalty;
        FrequencyPenalty = original.FrequencyPenalty;
        RepetitionPenalty = original.RepetitionPenalty;

        // Copy value type Prompt
        System = original.System;
        Instruction = original.Instruction;

        // Copy reference type Schema & Examples
        Schema = original.Schema;
        if (original.Examples != null)
        {
            Examples = new List<Example>(original.Examples.Select(item => new Example(item)));
        }
    }
    #endregion
    

    // ======================
    //  Supporting Functions
    // ======================
    
    #region Supporting Functions
    public static bool IsCompletion(string? chatOrCompletion)
    {
        switch (chatOrCompletion)
        {
            case "chat": return false;
            case "completion": return true;
            default: return false; //throw new Exception("Invalid chat-or-completion value");
        }
    }

    public bool IsCompletion()
    {
        return IsCompletion(CompletionType);
    }
    
    public static bool IsChat(string? chatOrCompletion)
    {
        switch (chatOrCompletion)
        {
            case "chat": return true;
            case "completion": return false;
            //default: return null; //throw new Exception("Invalid chat-or-completion value");
            default: return false;
        }
    }

    public bool IsChat()
    {
        return IsChat(CompletionType);
    }
    
    private static string CreateSchemaFromExamples(List<Example> examples)
    {
        return "";
    }

    /// <summary>
    /// Extracts inputs from the given string.
    /// </summary>
    /// <param name="info">The information from which inputs need to be extracted.</param>
    /// <returns>A list of inputs extracted from the information.</returns>
    public static List<Input> ExtractInputs(string info)
    {
        List<Input> inputs = new List<Input>();
        string currentLabel = null;
        List<string> contentBuilder = new List<string>();

        foreach (var line in info.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None))
        {
            var match = Regex.Match(line, @"^\[(.*?)\](.*)");
            if (match.Success)
            {
                if (currentLabel != null)
                {
                    // Add the previous label and its content to inputs
                    inputs.Add(new Input(currentLabel, string.Join("\n", contentBuilder)));
                    //Util.Log($"Adding input to example: {currentLabel} : {string.Join("\n", contentBuilder)}");
                    contentBuilder.Clear();
                }
                // Set new label and start content with the rest of the line if exists
                currentLabel = match.Groups[1].Value;
                string restOfLine = match.Groups[2].Value.Trim();
                if (!string.IsNullOrEmpty(restOfLine))
                {
                    contentBuilder.Add(restOfLine);
                }
            }
            else
            {
                // Accumulate content
                contentBuilder.Add(line);
            }
        }

        // Don't forget to add the last captured input
        if (currentLabel != null)
        {
            inputs.Add(new Input(currentLabel, string.Join("\n", contentBuilder)));
            //Util.Log($"Adding input to example: {currentLabel} : {string.Join("\n", contentBuilder)}");
        }

        return inputs;
    }
    
    //public static 
    
    /*public static List<Input> ExtractInputs(string info)
    {
        List<Input> inputs = new List<Input>();

        string pattern = @"\[(.*?)\](.*?)(?=\[|\z)";
        foreach (Match match in Regex.Matches(info, pattern, RegexOptions.Singleline))
        {
            string label = match.Groups[1].Value;
            string text = match.Groups[2].Value.Trim();
            inputs.Add(new Input(label, text));
        }

        return inputs;
    }*/

    /// <summary>
    /// Converts the example pairs from a dictionary into a list of Example objects.
    /// </summary>
    /// <param name="examplePairs">The example pairs as a dictionary, where each key is an input and the corresponding value is an output.</param>
    /// <param name="requestJson">An optional parameter to specify if the output should be in JSON format.</param>
    /// <returns>A list of Example objects representing the converted example pairs.</returns>
    public static List<Example> ConvertExamples(Dictionary<string, string> examplePairs, bool? requestJson = false)
    {
        List<Example> output = new();

        foreach (var pair in examplePairs)
        {
            List<Input> exin = ExtractInputs(pair.Key);
            string exout =
                Util.JsonifyExample(pair.Value, requestJson); //string exout = RlonConverter.ToJson(pair.Value);
            output.Add(new Example(exin, exout));
            
            //Util.Log($"Example Output:\nOriginal:\n'''\n{pair.Value}\n'''\n\nJsonified:\n'''\n{exout}\n'''\n");
        }

        return output;
    }

    /// <summary>
    /// Extracts input-output example pairs from a dictionary.
    /// The input example keys should follow the pattern "_exin_number",
    /// and the corresponding output example keys should follow the pattern "_exout_number".
    /// </summary>
    /// <param name="data">The dictionary containing input-output example pairs.</param>
    /// <returns>A dictionary where the key is the value of "_exin_number" and the value is the value of "_exout_number".</returns>
    public static Dictionary<string, string> ExtractExamples(Dictionary<string, string> data)
    {
        var inputPattern = @"^_exin_(\d+)$"; // Pattern to match _exin_ followed by numbers
        var outputPattern = @"^_exout_(\d+)$"; // Pattern to match _exout_ followed by numbers

        var inputValues = new Dictionary<int, string>();
        var outputValues = new Dictionary<int, string>();

        // Collect all matching input and output examples
        foreach (var entry in data)
        {
            var key = entry.Key;
            var value = entry.Value;
            var inputMatch = Regex.Match(key, inputPattern);
            var outputMatch = Regex.Match(key, outputPattern);

            if (inputMatch.Success)
            {
                int index = int.Parse(inputMatch.Groups[1].Value);
                inputValues[index] = value;
            }
            else if (outputMatch.Success)
            {
                int index = int.Parse(outputMatch.Groups[1].Value);
                outputValues[index] = value;
            }
        }

        var pairedExamples = new Dictionary<string, string>();

        // Pair inputs with corresponding outputs
        foreach (var input in inputValues)
        {
            if (outputValues.TryGetValue(input.Key, out var correspondingOutput))
            {
                pairedExamples[input.Value] = correspondingOutput;
            }
        }

        return pairedExamples;
    }
    
    public static void CallInitIfExists(object obj)
    {
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
    #endregion
    
    
    // ==============================
    //  RConfig Conversion Functions
    // ==============================
    
    #region RConfig Conversion Functions
    /// <summary>
    /// Converts a Prompt object into a dictionary of string key-value pairs.
    /// </summary>
    /// <param name="prompt">The Prompt object to convert.</param>
    /// <returns>A dictionary representing the serialized form of the Prompt.</returns>
    public static Dictionary<string, string> ToDictionary(Prompt prompt)
    {
        var type = prompt.GetType();
        var properties = type.GetProperties();
        var serializedData = new Dictionary<string, string>();

        foreach (var property in properties)
        {
            var attribute = property.GetCustomAttributes(typeof(RConfigPropertyAttribute), false)
                        .FirstOrDefault() as RConfigPropertyAttribute;

            if (attribute != null)
            {
                var value = property.GetValue(prompt)?.ToString() ?? "";
                serializedData[attribute.Name] = value;
            }
        }

        // Special handling for examples
        for (int index = 0; index < prompt.Examples?.Count; ++index)
        {
            var example = prompt.Examples[index];
            serializedData[$"_exin_{index + 1}"] = "TODO"; // TODO: Create a builder to output inputs Rlon.Serialize(example.Inputs);
            serializedData[$"_exout_{index + 1}"] = example.Output;
        }

        return serializedData;
    }

    /// <summary>
    /// Converts a dictionary of string key-value pairs into a Prompt object.
    /// </summary>
    /// <param name="data">The dictionary containing the serialized prompt data.</param>
    /// <param name="namePrefix">Optional prefix for the prompt name.</param>
    /// <returns>A Prompt object deserialized from the dictionary.</returns>
    public static Prompt ToObject(Dictionary<string, string> data, string? namePrefix = "")
    {
        var prompt = new Prompt();
        var properties = typeof(Prompt).GetProperties();

        foreach (var property in properties)
        {
            var attribute = property.GetCustomAttributes(typeof(RConfigPropertyAttribute), false)
                .FirstOrDefault() as RConfigPropertyAttribute;

            if (attribute != null && data.TryGetValue(attribute.Name, out var value))
            {
                // Skip adding this property to the object/leave null
                if ((value.ToLower()) == "default")
                    continue;
                
                if (property.Name == "Name" && namePrefix != null)
                {
                    value = $"{namePrefix}{value}";
                }

                //Util.Log($"attribute.Name: {attribute.Name}, value: {value}");
                try
                {
                    // Check if property type is nullable and convert appropriately
                    object? convertedValue = RConfigParser.ConvertToType(value, property.PropertyType);
                    property.SetValue(prompt, convertedValue);
                }
                catch (Exception ex) // Catch exceptions during type conversion and handle them accordingly
                {
                    throw new FormatException($"Failed to convert value to target type. Property: {property.Name}", ex);
                }
            }
        }

        // Special handling for examples
        var examplePairs = ExtractExamples(data);
        prompt.Examples = ConvertExamples(examplePairs, prompt.RequestJson);
        
        try
        {
            CallInitIfExists(prompt);
        }
        catch (Exception e)
        {
            Util.Log($"Init exists but failed! Message: {e.Message}");
        }

        return prompt;
    }
    #endregion
}