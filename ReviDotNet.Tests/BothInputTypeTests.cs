// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Collections.Generic;
using FluentAssertions;
using Revi;
using Xunit;

namespace ReviDotNet.Tests;

public class BothInputTypeTests
{
    [Fact]
    public void CompletionPrompt_BothInputType_FiltersFilledInputs()
    {
        // Arrange
        var prompt = new Prompt
        {
            Name = "TestPrompt",
            Version = 1,
            System = "System with {User-Name}",
            Instruction = "Instruction with {User-Age}",
            InstructionInputTypeOverride = InputType.Both
        };

        var model = new ModelProfile
        {
            DefaultSystemInputType = InputType.None,
            DefaultInstructionInputType = InputType.Listed,
            Structure = "{system}{instruction}{input}{output}",
            SystemSection = "S: {content}\n",
            InstructionSection = "I: {content}\n",
            InputSection = "L: {content}\n",
            InputItem = "{label}: {text}\n",
            InputItemMulti = "{label}: {text}\n"
        };

        var inputs = new List<Input>
        {
            new Input("User Name", "Alice"),
            new Input("User Age", "30"),
            new Input("Extra Info", "Something extra")
        };

        // Act
        // ProcessInputs is private, but BuildString is public
        string result = CompletionPrompt.BuildString(prompt, model, inputs);

        // Assert
        // User Name is NOT in System because systemInputType is None
        result.Should().NotContain("System with Alice");
        result.Should().Contain("System with {User-Name}");

        // User Age IS in Instruction because it's Both and {User-Age} is a placeholder
        result.Should().Contain("Instruction with 30");

        // User Age should NOT be in the Listed section because it was used as Filled
        result.Should().NotContain("User Age: 30");

        // Extra Info SHOULD be in the Listed section
        result.Should().Contain("Extra Info: Something extra");
    }

    [Fact]
    public void CompletionPrompt_BothInputType_SystemAndInstructionBothUseBoth()
    {
        // Arrange
        var prompt = new Prompt
        {
            Name = "TestPrompt",
            Version = 1,
            System = "System with {User-Name}",
            Instruction = "Instruction with {User-Age}",
            SystemInputTypeOverride = InputType.Both,
            InstructionInputTypeOverride = InputType.Both
        };

        var model = new ModelProfile
        {
            DefaultSystemInputType = InputType.Listed,
            DefaultInstructionInputType = InputType.Listed,
            Structure = "{system}{instruction}{input}{output}",
            SystemSection = "S: {content}\n",
            InstructionSection = "I: {content}\n",
            InputSection = "L: {content}\n",
            InputItem = "{label}: {text}\n",
            InputItemMulti = "{label}: {text}\n"
        };

        var inputs = new List<Input>
        {
            new Input("User Name", "Alice"),
            new Input("User Age", "30"),
            new Input("Extra Info", "Something extra")
        };

        // Act
        string result = CompletionPrompt.BuildString(prompt, model, inputs);

        // Assert
        result.Should().Contain("System with Alice");
        result.Should().Contain("Instruction with 30");
        
        // Neither Name nor Age should be in the list
        result.Should().NotContain("User Name: Alice");
        result.Should().NotContain("User Age: 30");
        
        // Extra Info should be in the list (actually it will be added to inputSection)
        result.Should().Contain("Extra Info: Something extra");
    }
}
