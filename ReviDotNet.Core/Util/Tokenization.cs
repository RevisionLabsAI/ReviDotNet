// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Diagnostics;
using System.Net.Mime;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.DeepDev;

namespace Revi;

// This file is bleh, please ignore how bleh this file is. Thank you. :) 

public static partial class Util
{
    public static int EstTokenCountFromCharCount(int characterCount)
    {
	    return Math.Max(0, (int)((characterCount - 2) * Math.Exp(-1)));
    }

    public static int EstCharCountFromTokenCount(int tokenCount)
    {
	    return Math.Max(0, (int)(tokenCount * Math.Exp(1))) + 2;
    }

    public static int EstCharCountForMaxTokens(int maxTokenCount)
    {
	    double sqrtMargin = 0.5;
	    double linearMargin = 1.010175047; 
	    return Math.Max(0, (int)(maxTokenCount * Math.Exp(1) - linearMargin - Math.Sqrt(Math.Max(0, maxTokenCount - sqrtMargin))));
    }

    public static string TrimTextToFitTokenLimit(string inputText, int maxTokenCount)
    {
	    int characterIndex = Math.Min(inputText.Length, EstCharCountForMaxTokens(maxTokenCount));
	    return inputText.Substring(0, characterIndex);
    }
    
    public static async Task<List<int>> Tokenize(string inputText)
    {
	    var IM_START = "<|im_start|>";
	    var IM_END = "<|im_end|>";
        
	    var specialTokens = new Dictionary<string, int>{
		    { IM_START, 100264},
		    { IM_END, 100265},
	    };
        
	    var tokenizer = await TokenizerBuilder.CreateByModelNameAsync("gpt-4", specialTokens);
        
	    var text = "<|im_start|>" + inputText + "<|im_end|>";
	    var encoded = tokenizer.Encode(text, new HashSet<string>(specialTokens.Keys));
        
	    return encoded;
    }
    
    public async static Task<int> CountTokens(string inputText)
    {
	    //var tokens = await Tokenize(inputText);
	    //return tokens.Count;
	    return Util.EstTokenCountFromCharCount(inputText.Length);
    }

    public static bool MayExceedTokenLimit(string text, int maxTokens)
    {
	    return (text.Length > EstCharCountForMaxTokens(maxTokens));
    }
    
    public static List<string> SplitStringByNearestWhitespace(string input, int maxChars)
    {
	    List<string> result = new List<string>();
	    int startIndex = 0;

	    while (startIndex < input.Length)
	    {
		    // Check if remaining characters are less than or equal to maxChars
		    if (startIndex + maxChars >= input.Length)
		    {
			    result.Add(input.Substring(startIndex).Trim());
			    break;
		    }

		    int segmentEnd = startIndex + maxChars;
		    int nearestWhitespaceIndex = segmentEnd;

		    // Search backwards for nearest whitespace
		    while (nearestWhitespaceIndex > startIndex && !char.IsWhiteSpace(input[nearestWhitespaceIndex]))
		    {
			    nearestWhitespaceIndex--;
		    }

		    // If no whitespace found, search forward
		    if (nearestWhitespaceIndex == startIndex)
		    {
			    nearestWhitespaceIndex = segmentEnd;
			    while (nearestWhitespaceIndex < input.Length && !char.IsWhiteSpace(input[nearestWhitespaceIndex]))
			    {
				    nearestWhitespaceIndex++;
			    }
		    }

		    // Add the substring from startIndex to nearestWhitespaceIndex
		    string segment = input.Substring(startIndex, nearestWhitespaceIndex - startIndex).Trim();
		    if (!string.IsNullOrEmpty(segment))
		    {
			    result.Add(segment);
		    }

		    // Move startIndex past the last whitespace
		    startIndex = nearestWhitespaceIndex + 1;
	    }

	    return result;
    }
    
    public static string SubStringByNearestWhitespace(string input, int position)
    {
	    int nearestWhitespaceIndex = -1;
	    for (int i = position; i >= 0; i--)
	    {
		    if (char.IsWhiteSpace(input[i]))
		    {
			    nearestWhitespaceIndex = i;
			    break;
		    }
	    }

	    if (nearestWhitespaceIndex != -1)
	    {
		    string firstPart = input.Substring(0, nearestWhitespaceIndex);
		    string secondPart = input.Substring(nearestWhitespaceIndex + 1);
		    return firstPart;
	    }
        
	    // No whitespace found before the given position
	    // Returning original string and an empty string
	    return string.Empty;
    }
}