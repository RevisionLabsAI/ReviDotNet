// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

public class Optimization
{
    // ==============
    //  Declarations
    // ==============
    //public const int TestsPerPrompt = 20;
    // TODO: Fail early if there are too many consecutive failures

    
    // ======================
    //  Supporting Functions
    // ======================
    
    private List<TestTicket> SelectFailures(PromptEvalTicket promptEvalTicket)
    {
        List<TestTicket> results = new();
        foreach (TestTicket result in promptEvalTicket.TestTickets)
        {
            //if (result.SchemaFail)
            //    results.Add(result);
        }
        /* 	    // Find the top 5 items
               var topItems = searchResults
           	    .Where(x => x.Desired)
           	    .OrderByDescending(x => x.Score)
           	    .ThenBy(x => x.Position)
           	    .Take(limit)
           	    .ToList();

         */
        return results;
    }

    private static bool CheckCanOptimize(Prompt prompt)
    {
        // todo: Validate that this prompt has suitable examples/is sufficient to optimize
        return true;
    }
    
    
    // ================
    //  Base Optimizer
    // ================
    
    private static async Task<List<PromptEvalTicket>> BaseOptimizer(Prompt prompt, string? modelType = null)
    {
        string optimizationPrompt;
        
        // Get the model type from the prompt if it's not specified to us
        if (string.IsNullOrEmpty(modelType)) 
            modelType = "llama3";
        
        switch (modelType)
        {
            case "llama3": 
                optimizationPrompt = "promptual/optimize/llama3";
                break;
            default:
                optimizationPrompt = "promptual/optimize/generic";
                break;
        }
        
        // Create inputs
        //var result = InferToObject<Response.BaseOptimization>(optimizationPrompt, );
        // Validate results
        // Take 3 from the list and convert to TestPrompt's
        
        // Format into return results
        List<PromptEvalTicket> results = new();
        return results;

    }
    
    
    // ======================
    //  Reflection Optimizer
    // ======================
    
    public static List<PromptEvalTicket> SelectReflectionPrompts(List<PromptEvalTicket> promptTickets)
    {
        
        // TODO: Actually select based on some criteria
        // For now just return back, we'll figure out how to select later
        return promptTickets;
    }
    
    public static async Task<List<PromptEvalTicket>> ReflectionOptimizer(List<PromptEvalTicket> promptTickets)
    {
        List<PromptEvalTicket> selectedPrompts = SelectReflectionPrompts(promptTickets);
        return promptTickets;
    }
    
    
    // =================
    //  Combo Optimizer
    // =================
    
    public static List<PromptEvalTicket> SelectComboPrompts(List<PromptEvalTicket> promptTickets)
    {
        // TODO: Actually select based on some criteria
        // For now just return back, we'll figure out how to select later
        return promptTickets;
    }

    public static async Task<List<PromptEvalTicket>> ComboOptimizer(List<PromptEvalTicket> promptTickets)
    {
        List<PromptEvalTicket> selectedPrompts = SelectComboPrompts(promptTickets);
        return promptTickets;
    }
    
    
    // ========================
    //  Optimization Executors
    // ========================
    
    public static async Task OptimizeSingle(Prompt prompt)
    {
        // Tree of Tests:
        //  - Single Optimization: Optimize for a single model type (runs through these passes just once)
        //  - Multi Optimization: Optimize to find the best model for the prompt (runs through these passes for each model type available)
        //
        //  - Output confinement optimization to match the example schema
        
        if (CheckCanOptimize(prompt) is false) 
            return;
        
        List<PromptEvalTicket> promptTickets = new();
        
        // Pass #1: Base Optimization
        //  - Follow best-practice guide to improve existing prompt
        promptTickets.AddRange(await BaseOptimizer(prompt));
        await Evaluation.TestAllUntested(promptTickets);
        
        // Pass #2: Reflection Optimization
        //  - Check results of previous output and look for ways to improve
        promptTickets.AddRange(await ReflectionOptimizer(promptTickets));
        await Evaluation.TestAllUntested(promptTickets);
        
        // Pass #3: Combo Optimization
        //  - Grab the best bits from all the previous optimizations
        promptTickets.AddRange(await ComboOptimizer(promptTickets));
        await Evaluation.TestAllUntested(promptTickets);
        
        // Pass #4: Parameter Tuning
        //  - Select top two results, tune parameters for the model
        //selectedPrompts.Clear();
        //selectedPrompts.AddRange(SelectComboPrompts(testPrompts));
        //testPrompts.AddRange(await ComboOptimizer(selectedPrompts));
        //await TestAllUntested(testPrompts);

        // Output: Interpret results to provide the best prompt
        //  - 
    }
}