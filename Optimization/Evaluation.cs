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

namespace Revi;

public class Evaluation
{
    // ==============
    //  Declarations
    // ==============
    private const int TestsPerPrompt = 20;
    // TODO: Fail early if there are too many consecutive failures
    // TODO: Add rcfg file which contains these settings

    
    // ======================
    //  Supporting Functions
    // ======================
    
    /// <summary>
    /// Retrieves an example item from a list based on the given index and start offset.
    /// </summary>
    /// <param name="list">The list of examples.</param>
    /// <param name="index">The index of the desired example item.</param>
    /// <param name="startOffset">The start offset of the subset of the list to choose from.</param>
    /// <returns>The selected example item.</returns>
    private static Example? GetExample(List<Example>? list, int index, int startOffset)
    {
        if (list is null)
            throw new ArgumentNullException(nameof(list));
        
        if (list.Count == 0)
            throw new InvalidOperationException("The list cannot be empty.");
        
        if (startOffset < 1 || startOffset > list.Count)
            throw new ArgumentOutOfRangeException(nameof(startOffset), "Start offset must be within the list's range and not zero-based.");

        // Calculate the effective size of the list, considering the offset
        int effectiveSize = list.Count - startOffset + 1;
        if (effectiveSize <= 0)
            throw new InvalidOperationException("Start offset must leave at least one element to choose from.");

        // Adjust the index to cycle through the available subset of the list
        int adjustedIndex = (index % effectiveSize) + startOffset - 1;

        // Return the selected item
        return list[adjustedIndex];
    }
    
    
    // ======================
    //  Test Ticket Creation
    // ======================
    
    private static Task CreateTestTask(TestTicket ticket)
    {
        // Form the task function
        return Task.Run(async () =>
        {
            var response = await Inference.Completion(ticket.PromptToTest, ticket.ExampleToTest.Inputs);

            if (response is null)
            {
                RUtil.Log("Null response...");
                return;
            }

            ticket.FullPrompt = response.FullPrompt;
            ticket.FullOutput = response.Selected;
            ticket.EndTime = DateTime.Now;
            ticket.Complete = true;
            ticket.Analyze();
        });
    }

    private static List<Task> CreateTestTasks(PromptEvalTicket promptTicket)
    {
        List<Task> tasksForPrompt = new();
        foreach (TestTicket testTicket in promptTicket.TestTickets)
        {
            tasksForPrompt.Add(CreateTestTask(testTicket));
        }

        return tasksForPrompt;
    }

    private static void CreateTestTickets(PromptEvalTicket promptTicket)
    {
        // Input strategy #1:
        //  - Just grab all the examples and loop through them
        int fewShotOffset = promptTicket.Prompt.FewShotExamples ?? 0;

        List<TestTicket> testTickets = new();
        for (int index = 0; index < TestsPerPrompt; ++index)
        {
            Example? foundExample = GetExample(promptTicket.Prompt.Examples, index, fewShotOffset);

            if (foundExample is null)
                throw new Exception("Could not find example?");
            
            testTickets.Add(new TestTicket(promptTicket.Prompt, promptTicket.Model, foundExample));
        }
        
    }
    
    
    // ==================================
    //  Prompt Evaluation Ticket Testing
    // ==================================

    public static async Task TestAllUntested(List<PromptEvalTicket> promptTickets)
    {
        // Declarations 
        List<Task> tasks = new();
        
        // Create the list of test tasks to execute
        foreach (PromptEvalTicket promptTicket in promptTickets)
        {
            if (promptTicket.Started)
                continue;

            promptTicket.Started = true;
            promptTicket.StartTime = DateTime.Now;

            CreateTestTickets(promptTicket);
            
            tasks.AddRange(CreateTestTasks(promptTicket));
        }
        
        // Run the tasks and analyze them!
        try
        {
            // Execute and wait for all the tasks to complete
            await Task.WhenAll(tasks);

            foreach (PromptEvalTicket promptTicket in promptTickets)
            {
                if (promptTicket.Complete || promptTicket.Started is false)
                    continue;

                promptTicket.EndTime = DateTime.Now;
                promptTicket.Complete = true;
                promptTicket.Analyze();
            }
        }

        // Catch any exceptions that were thrown
        catch (AggregateException ae)
        {
            foreach (var exception in ae.InnerExceptions)
                RUtil.Log($"Exception: {exception.Message}");
        }
    }
}