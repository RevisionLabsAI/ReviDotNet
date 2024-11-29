// =================================================================================
//   Copyright © 2024 Revision Labs, Inc. - All Rights Reserved
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
using System.Text.RegularExpressions;

namespace Revi;

/// <summary>
/// Manages the conversion and handling of prompts into serialized data formats and vice-versa.
/// </summary>
public static class PromptManager
{
    private static List<Prompt> _prompts = new();
    
    private static bool IsNewerVersionOrUpdatedLater(Prompt existingPrompt, Prompt newPrompt)
    {
        bool isNewerVersion = newPrompt.Version > existingPrompt.Version;
        bool isUpdatedLater = newPrompt.Version == existingPrompt.Version && newPrompt.DateUpdated > existingPrompt.DateUpdated;
        return isNewerVersion || isUpdatedLater;
    }
    
    public static Prompt? Get(string name)
    {
        return _prompts.FirstOrDefault(model => model.Name == name);
    }
    
    private static void CheckAdd(Prompt newPrompt)
    {
        var existingPrompt = _prompts.FirstOrDefault(p => p.Name == newPrompt.Name);
        if (existingPrompt == null)
        {
            _prompts.Add(newPrompt);
            Util.Log($"Loading prompt named \"{newPrompt.Name}\"");
        }
        else if (IsNewerVersionOrUpdatedLater(existingPrompt, newPrompt))
        {
            _prompts[_prompts.IndexOf(existingPrompt)] = newPrompt;
            Util.Log($"Updating to newer prompt named \"{newPrompt.Name}\"");
        }
    }
    
    public static void Load()
    {
        // Clear existing prompts
        _prompts.Clear();
        
        // Collect the list of files
        try
        {
            string path = AppDomain.CurrentDomain.BaseDirectory + "RConfigs/Prompts/";
            Util.Log($"Attempting to load prompts from {path}");
            List<string> files = Directory
                .EnumerateFiles(path, "*.pmt", SearchOption.AllDirectories)
                .ToList();

            // Load in the files
            foreach (var file in files)
            {
                Dictionary<string, string> promptDictionary = RConfigParser.Read(file);
                string folder = Util.ExtractSubDirectories(path, file).ToLower();
                Prompt? prompt = Prompt.ToObject(promptDictionary, folder);
                
                if (prompt.Name is null)
                    continue;
                
                CheckAdd(prompt);
            }
        }
        catch (DirectoryNotFoundException e)
        {
            Util.Log($"Directory not found: {e.Message}");
        }
        catch (Exception e)
        {
            Util.Log($"Error loading prompts: {e.Message}");
        }
    }
}
