namespace GovUK.Dfe.AI.Agents.AISearch.Constants;

/// <summary>This package's error messages.</summary>
internal static class ErrorMessages
{
    internal const string AzureSearchIndexesInvalid = "Azure Search needs at least one entry under Indexes, each with a unique Name.";
    internal const string AzureSearchQueryFailed = "Azure Search query against '{0}' failed.";
    internal const string NoAzureSearchClientConfigured = "No Azure Search client configured for '{0}'.";
    internal const string NoAzureSearchInformationFound = "No {0} information found."; 
}
