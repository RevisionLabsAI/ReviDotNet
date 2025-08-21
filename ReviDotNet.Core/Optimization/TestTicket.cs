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

using Revi;

namespace Revi;

public class TestTicket
{
    // The prompt
    public Prompt PromptToTest;
    public ModelProfile Model;
    public Example ExampleToTest;
    
    // Observability
    public string? FullPrompt;
    public string? FullOutput;
    public string? ExtractedOutput;
    
    // Time tracking
    public DateTime? StartTime;
    public DateTime? EndTime;
    
    // Evaluation results
    public bool Complete = false;
    public bool? SchemaFail;
    public float? Closeness;

    public TestTicket(
        Prompt promptToTest,
        ModelProfile model,
        Example exampleToTest)
    {
        PromptToTest = promptToTest;
        Model = model;
        ExampleToTest = exampleToTest;
    }
    
    public void Analyze()
    {
        // TODO: This is wrong
        ExtractedOutput = Util.ExtractJson(
            FullOutput, 
            PromptToTest.ChainOfThought);

        // TODO: Validate to schema
        //SchemaFail = Inference.ValidateToSchema(ExtractedOutput, PromptToTest.Schema);
        Closeness = Util.CosineSimilarity(ExtractedOutput, ExampleToTest.Output);
    }
}
