namespace GovUK.Dfe.AI.Agents.AISearch.Constants;

/// <summary>This package's error messages.</summary>
internal static class ErrorMessages
{
    internal const string AzureSearchIndexesInvalid = "Azure Search needs at least one entry under Indexes, each with a unique Name.";
    internal const string AzureSearchQueryFailed = "Azure Search query against '{0}' failed.";
    internal const string NoAzureSearchClientConfigured = "No Azure Search client configured for '{0}'.";
    internal const string NoAzureSearchInformationFound = "No {0} information found.";
    internal const string SearchSectionMissing = "AddAISearch needs the AiAgents:Search section (Endpoint and Indexes).";
    internal const string SearchCredentialMissing = "AddAISearch needs a credential: pass one, or set AiAgents:Search:{0}.";
    internal const string AzureSearchMaxEvidenceInvalid = "Search:MaxEvidenceCharacters must be at least 1 when set.";
    internal const string EvidenceResultsLeftOut = "[{0} less relevant result(s) left out to keep the evidence within {1} characters.]"; 
}
