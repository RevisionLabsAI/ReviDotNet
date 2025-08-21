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

namespace Revi;

public class PromptEvalTicket
{
    public string ID;
    public Prompt ParentPrompt;
    public DateTime StartTime;
    public DateTime EndTime;

    public string Provider;
    public ModelProfile Model;
    public Prompt Prompt;
    //public List<List<string>> ListOfInputs;

    public bool Started;
    public bool Complete;
    public List<TestTicket> TestTickets;
    
    
    public int? SchemaFailCount; // Schema fail would be if it's supposed to return JSON in a certain format but provides something else
    public float? ClosenessAverage;
    public float? ClosenessMedian;
    public float? SuccessRate;

    // Provide results to LLM and ask for an analysis
    //  - Prompt
    //  - Input
    //  - Output
    public string ResultAnalysis;

    public PromptEvalTicket(
        Prompt parentPrompt,
        Prompt promptToTest,
        List<Input>? inputs,
        int runCount)
    {
        ID = Guid.NewGuid().ToString();
        ParentPrompt = parentPrompt;
        Prompt = promptToTest; //new Prompt(parentPrompt);
        
        //StartTime = DateTime.Now;
        
        
    }
    
    private static float CalculateMedian(List<TestTicket> items)
    {
        var orderedItems = items
            .Where(item => item.Closeness.HasValue)
            .OrderBy(item => item.Closeness)
            .ToArray();
        
        if (!orderedItems.Any()) 
            return 0;

        float median;
        int halfIndex = orderedItems.Length / 2;
        if (orderedItems.Length % 2 == 0)
        {
            // even
            median = (orderedItems[halfIndex].Closeness.Value + orderedItems[halfIndex - 1].Closeness.Value) / 2;
        }
        else
        {
            // odd
            median = orderedItems[halfIndex].Closeness.Value;
        }

        return median;
    }
    
    public void Analyze()
    {
        float sumCloseness = 0;
        int closeCount = 0;
        int testCount = 0;
        int schemaFailCount = 0;

        foreach (TestTicket test in TestTickets)
        {
            if (test.SchemaFail is true)
                ++schemaFailCount;
        
            if (test.Closeness.HasValue)
            {
                sumCloseness += test.Closeness.Value;
                ++closeCount;
            }

            ++testCount;
        }

        ClosenessAverage = closeCount > 0 ? sumCloseness / closeCount : 0;
        ClosenessMedian = CalculateMedian(TestTickets);
        SchemaFailCount = schemaFailCount;
        SuccessRate = (float)schemaFailCount / testCount;
    }
}