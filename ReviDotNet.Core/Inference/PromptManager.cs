// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Reflection;

namespace Revi
{
    /// <summary>
    /// Manages the conversion and handling of prompts into serialized data formats and vice-versa.
    /// </summary>
    public static class PromptManager
    {
        // ==============
        //  Declarations
        // ==============
        
        /// <summary>
        /// A private static collection of prompts that stores instances of the <see cref="Prompt"/> class.
        /// This list is used internally by the <see cref="PromptManager"/> to manage and process prompts
        /// for various operations such as loading from external sources and updating existing prompts.
        /// </summary>
        private static List<Prompt> _prompts = new();

        
        // ================
        //  Prompt Loading
        // ================
        
        #region Prompt Loading

        /// <summary>
        /// Loads prompt configurations from specified directory or embedded resources.
        /// </summary>
        /// <remarks>
        /// This method clears any existing prompts before attempting to load new ones from files located in the
        /// application's base directory under "RConfigs/Prompts". It searches for files with a ".pmt" extension.
        /// If the directory does not exist, it attempts to load prompts from embedded resources. Any errors encountered
        /// during the loading process are logged.
        /// </remarks>
        public static void Load(Assembly assembly)
        {
            // Clear existing prompts
            _prompts.Clear();

            // Collect the list of files
            try
            {
                string path = AppDomain.CurrentDomain.BaseDirectory + "RConfigs/Prompts/";
                //Util.Log($"Attempting to load prompts from {path}");
                List<string> files = Directory
                    .EnumerateFiles(path, "*.pmt", SearchOption.AllDirectories)
                    .ToList();

                // Load in the files
                foreach (var file in files)
                {
                    LoadPromptFromFile(file, path);
                }
            }
            catch (DirectoryNotFoundException e)
            {
                //Util.Log($"Directory not found: {e.Message}. Attempting to load from embedded resources.");
                LoadFromEmbeddedResources(assembly);
            }
            catch (Exception e)
            {
                Util.Log($"Error loading prompts: {e.Message}");
            }
        }

        /// <summary>
        /// Loads a prompt from a file and processes it by converting the file data into a prompt object,
        /// and then attempts to add it to the prompt collection.
        /// </summary>
        /// <param name="file">The path of the file containing the prompt data to load.</param>
        /// <param name="basePath">The base directory path where the prompt file resides.</param>
        private static void LoadPromptFromFile(string file, string basePath)
        {
            Dictionary<string, string> promptDictionary = RConfigParser.Read(file);
            string folder = Util.ExtractSubDirectories(basePath, file).ToLower();
            Prompt? prompt = Prompt.ToObject(promptDictionary, folder);

            if (prompt?.Name is null)
                return;

            CheckAdd(prompt, false);
        }

        /// <summary>
        /// Loads prompt configurations from embedded resources within the assembly.
        /// </summary>
        /// <remarks>
        /// This method searches for embedded resources with filenames containing ".Prompts." and ending with a ".pmt" extension.
        /// Each valid resource is read into a stream, parsed into a prompt dictionary using RConfigParser, and then
        /// converted to a <see cref="Prompt"/> object. If the prompt has a valid name, it is added to the collection of prompts.
        /// Any errors that occur during the loading process are captured and logged using the application's logging utility.
        /// </remarks>
        private static void LoadFromEmbeddedResources(Assembly assembly)
        {
            try
            {
                if (assembly is null)
                    throw new Exception("Assembly cannot be null.");
                
                var resourceNames = assembly.GetManifestResourceNames()
                    .Where(name => name.Contains(".Prompts.") &&
                                   name.EndsWith(".pmt", StringComparison.InvariantCultureIgnoreCase));

                foreach (var resourceName in resourceNames)
                {
                    //Util.Log($"Found resource: {resourceName}");
                    using var stream = assembly.GetManifestResourceStream(resourceName);
                    if (stream == null) 
                    {
                        Util.Log($"Stream not found for resource: {resourceName}");
                        continue;
                    }

                    using var reader = new StreamReader(stream);
                    var promptDictionary = RConfigParser.ReadEmbedded(reader.ReadToEnd());
                    string folder = Util.ExtractEmbeddedDirectories(".Prompts.", resourceName).ToLower();
                    Prompt? prompt = Prompt.ToObject(promptDictionary, folder);

                    if (prompt?.Name is null)
                        continue;

                    CheckAdd(prompt, true);
                }
            }
            catch (Exception e)
            {
                Util.Log($"Error loading from embedded resources: {e.Message}");
            }
        }
        #endregion
        
        
        // ======================
        //  Supporting Functions
        // ======================

        #region Supporting Functions
        /// <summary>
        /// Determines if a given prompt is a newer version or was updated later than an existing prompt.
        /// </summary>
        /// <param name="existingPrompt">The prompt currently stored, used as a comparison baseline.</param>
        /// <param name="newPrompt">The new prompt being evaluated for version or update status.</param>
        /// <returns>
        /// True if the new prompt represents a more recent version or has a later update date
        /// compared to the existing prompt; otherwise, false.
        /// </returns>
        private static bool IsNewerVersionOrUpdatedLater(Prompt existingPrompt, Prompt newPrompt)
        {
            return newPrompt.Version > existingPrompt.Version;
        }

        /// <summary>
        /// Determines if a new prompt should be added or an existing prompt should be updated
        /// based on name matching and version or update time comparison.
        /// </summary>
        /// <param name="newPrompt">The new prompt to evaluate for addition or update.</param>
        private static void CheckAdd(Prompt newPrompt, bool embedded)
        {
            var existingPrompt = _prompts.FirstOrDefault(p => p.Name == newPrompt.Name);
            if (existingPrompt == null)
            {
                _prompts.Add(newPrompt);
                if (embedded)
                    Util.Log($"Loaded embedded prompt \"{newPrompt.Name}\"");
                else
                    Util.Log($"Loaded prompt \"{newPrompt.Name}\" from file system");
            }
            else if (IsNewerVersionOrUpdatedLater(existingPrompt, newPrompt))
            {
                _prompts[_prompts.IndexOf(existingPrompt)] = newPrompt;
                Util.Log($"Updated prompt \"{newPrompt.Name}\" to newer version");
            }
        }
        #endregion
        
        
        // ===============
        //  Accessibility
        // ===============
        
        public static Prompt? Get(string name)
        {
            return _prompts.FirstOrDefault(model => model.Name == name);
        }

        public static List<Prompt> GetAll()
        {
            return [.._prompts];
        }

        /// <summary>
        /// Adds or updates a prompt in the registry from a file path.
        /// </summary>
        public static void LoadFromFile(string filePath)
        {
            string basePath = Path.GetDirectoryName(filePath)! + Path.DirectorySeparatorChar;
            LoadPromptFromFile(filePath, basePath);
        }

        /// <summary>
        /// Directly adds or replaces a prompt in the in-memory registry.
        /// </summary>
        public static void AddOrUpdate(Prompt prompt)
        {
            CheckAdd(prompt, false);
        }
    }
}