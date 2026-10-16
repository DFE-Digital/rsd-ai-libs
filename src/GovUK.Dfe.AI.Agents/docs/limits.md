# Limits

The limits a run works within: those set by GovUK.Dfe.AI.Agents, and those set by Azure AI Foundry.


## Set by this library

| Limit                                                             | Default                             | Setting                               | When reached                                              |
| ----------------------------------------------------------------- | ----------------------------------- | ------------------------------------- | --------------------------------------------------------- |
| Output tokens per run (reasoning, tool rounds and retry included) | 64,000                              | `MaxOutputTokensPerRun`               | The run fails                                             |
| Evidence per run                                                  | 200,000 characters (~50,000 tokens) | `MaxEvidenceCharacters`               | Cut, keeping the start, with a note to the model          |
| Evidence per search (AISearch)                                    | 190,000 characters                  | `AISearch:MaxEvidenceCharacters`      | Most relevant results kept; the rest left out with a note |
| One tool output                                                   | 40,000 characters (~10,000 tokens)  | `MaxToolOutputCharacters`             | Cut, with a note to the model                             |
| Tool-call rounds per run                                          | 10                                  | Fixed                                 | The run fails                                             |
| Time per run                                                      | No limit                            | `RunTimeout`                          | `TimeoutException`                                        |
| Runs at once                                                      | No limit                            | `MaxConcurrency`, `GlobalConcurrency` | Waits up to `MaxWaitForRunSlot`, then `TimeoutException`  |
| Retries of a rate-limited (429) or failed call                    | 6, honouring `Retry-After`          | `MaxRetries`                          | The run fails; a rate limit says what to change           |

English text is about 4 characters per token. The defaults suit **gpt-5.1** (272,000 input tokens): full evidence plus
ten full tool outputs still leaves room. Lower them for smaller models, such as gpt-4o (128,000).

## Set by Foundry

Figures from Microsoft Learn, September 2026. They change, so check yours.

| Model                      | Context window (input + output)                | Max output tokens |
| -------------------------- | ---------------------------------------------- | ----------------- |
| gpt-5, gpt-5-mini, gpt-5.1 | 400,000 (input up to 272,000)                  | 128,000           |
| gpt-4.1, gpt-4.1-mini      | 1,047,576, but 300,000 on standard deployments | 32,768            |
| o3, o4-mini                | Input 200,000                                  | 100,000           |
| gpt-4o, gpt-4o-mini        | Input 128,000                                  | 16,384            |

- **The context window is shared** by instructions, prompt, evidence, tool outputs, earlier rounds, reasoning and answer.
- **Rate limits are per deployment,** in tokens and requests per minute, and shared by every app using it. For example,
  gpt-5.1 Global Standard starts at 1,000,000 TPM. Over the limit, calls get HTTP 429, which the library retries; size
  `MaxConcurrency` and `GlobalConcurrency` to stay under it.
- **Remaining tokens** are read from each response into `dfe.ai_agents.tokens.remaining`. Responses below
  `LowRemainingTokensPercent` (default 10%) are counted in `dfe.ai_agents.tokens.low`.
- **Agent Service:** up to 128 tools per agent, and no rate limit of its own.

Sources: [models](https://learn.microsoft.com/azure/ai-foundry/openai/concepts/models),
[quotas and rate limits](https://learn.microsoft.com/azure/ai-foundry/openai/quotas-limits),
[Agent Service limits](https://learn.microsoft.com/azure/ai-foundry/agents/quotas-limits).
