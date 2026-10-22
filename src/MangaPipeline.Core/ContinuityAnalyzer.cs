using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace MangaPipeline.Core;

public sealed record ContinuityFactClaim(string DocumentId, string DocumentKind, JsonNode Value);

public sealed record ContinuityConflict(string FactId, List<ContinuityFactClaim> Claims);

public sealed record ContinuityCheckReport(
    string ProjectId,
    string RevisionId,
    List<ContinuityConflict> Conflicts);

public static class ContinuityAnalyzer
{
    public static ContinuityCheckReport Analyze(
        string projectId,
        string revisionId,
        IReadOnlyCollection<DocumentDefinition> documents)
    {
        var claimsByFact = new Dictionary<string, List<ContinuityFactClaim>>(StringComparer.Ordinal);
        foreach (var document in documents.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            if (!document.Content.TryGetPropertyValue("continuity", out var continuityNode))
            {
                continue;
            }

            if (continuityNode is not JsonObject continuity)
            {
                throw new WorkspaceException(
                    WorkspaceErrorCode.Validation,
                    $"Document '{document.Id}' continuity facts must be an object.");
            }

            foreach (var (factId, value) in continuity)
            {
                if (string.IsNullOrWhiteSpace(factId) || value is null)
                {
                    throw new WorkspaceException(
                        WorkspaceErrorCode.Validation,
                        $"Document '{document.Id}' continuity facts require non-empty ids and values.");
                }

                if (!claimsByFact.TryGetValue(factId, out var claims))
                {
                    claims = [];
                    claimsByFact.Add(factId, claims);
                }

                claims.Add(new ContinuityFactClaim(document.Id, document.Kind, value.DeepClone()));
            }
        }

        var conflicts = claimsByFact
            .Where(pair => pair.Value.Skip(1).Any(claim => !JsonNode.DeepEquals(pair.Value[0].Value, claim.Value)))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new ContinuityConflict(pair.Key, pair.Value))
            .ToList();
        return new ContinuityCheckReport(projectId, revisionId, conflicts);
    }
}
