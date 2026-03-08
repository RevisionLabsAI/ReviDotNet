// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Revi;

public class Example
{
    [JsonProperty("input")]
    public List<Input> Inputs;
    
    [JsonProperty("output")]
    public string Output;

    // Copy Constructor
    public Example(Example original)
    {
        Output = original.Output;
        Inputs = new List<Input>(original.Inputs);
    }
    
    public Example(List<Input> inputs, string output)
    {
        this.Inputs = inputs;
        this.Output = output;
    }
    
    public Example(Input input, object output)
    {
        var settings = new JsonSerializerSettings
        {
            Converters = new List<JsonConverter> { new StringEnumConverter() }
        };
        this.Inputs = new List<Input> { input };
        this.Output = JsonConvert.SerializeObject(output, settings);
    }
    
    public Example(List<Input> inputs, object output)
    {
        var settings = new JsonSerializerSettings
        {
            Converters = new List<JsonConverter> { new StringEnumConverter() }
        };
        this.Inputs = inputs;
        this.Output = JsonConvert.SerializeObject(output, settings);
    }
}