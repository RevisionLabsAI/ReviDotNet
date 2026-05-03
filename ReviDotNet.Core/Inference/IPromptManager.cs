// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Reflection;

namespace Revi;

/// <summary>DI interface for the prompt registry.</summary>
public interface IPromptManager
{
    /// <summary>Loads prompt files from the application assembly.</summary>
    Task LoadAsync(Assembly assembly, CancellationToken cancellationToken = default);

    /// <summary>Returns the prompt with the given name, or null if not found.</summary>
    Prompt? Get(string name);

    /// <summary>Returns all loaded prompts.</summary>
    List<Prompt> GetAll();

    /// <summary>Directly adds or replaces a prompt in the in-memory registry.</summary>
    void AddOrUpdate(Prompt prompt);

    /// <summary>Loads or reloads a prompt from a file path.</summary>
    void LoadFromFile(string filePath);
}
